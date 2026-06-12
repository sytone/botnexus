using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI subcommand for inspecting a live gateway instance via its REST API.
/// Unlike other debug subcommands that work offline against local files,
/// this requires a running gateway and makes HTTP requests to its endpoints.
/// </summary>
internal sealed class DebugGatewayCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public Command Build(Option<string?> targetOption)
    {
        var command = new Command("gateway", "Inspect live gateway state via REST API (requires running gateway).");
        var formatOption = new Option<string>("--format", () => "table", "Output format: table or json.");
        var urlOption = new Option<string>("--url", () => "http://localhost:5005", "Gateway base URL.");
        command.AddOption(formatOption);
        command.AddOption(urlOption);

        // ── status ──
        var statusCommand = new Command("status", "Show gateway uptime, version, and session count.");
        statusCommand.SetHandler(async context =>
        {
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var url = context.ParseResult.GetValueForOption(urlOption) ?? "http://localhost:5005";
            context.ExitCode = await ExecuteStatusAsync(url, format, context.GetCancellationToken());
        });

        // ── sessions ──
        var agentOption = new Option<string?>("--agent", "Filter by agent ID.");
        var limitOption = new Option<int>("--limit", () => 20, "Maximum sessions to return.");
        var sessionsCommand = new Command("sessions", "List active sessions from the gateway.")
        {
            agentOption, limitOption
        };
        sessionsCommand.SetHandler(async context =>
        {
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var url = context.ParseResult.GetValueForOption(urlOption) ?? "http://localhost:5005";
            var agent = context.ParseResult.GetValueForOption(agentOption);
            var limit = context.ParseResult.GetValueForOption(limitOption);
            context.ExitCode = await ExecuteSessionsAsync(url, agent, limit, format, context.GetCancellationToken());
        });

        // ── providers ──
        var providersCommand = new Command("providers", "List registered providers and model counts.");
        providersCommand.SetHandler(async context =>
        {
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var url = context.ParseResult.GetValueForOption(urlOption) ?? "http://localhost:5005";
            context.ExitCode = await ExecuteProvidersAsync(url, format, context.GetCancellationToken());
        });

        // ── config ──
        var sectionOption = new Option<string?>("--section", "Filter to a specific config section.");
        var configCommand = new Command("config", "Dump resolved gateway configuration (secrets redacted).")
        {
            sectionOption
        };
        configCommand.SetHandler(async context =>
        {
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var url = context.ParseResult.GetValueForOption(urlOption) ?? "http://localhost:5005";
            var section = context.ParseResult.GetValueForOption(sectionOption);
            context.ExitCode = await ExecuteConfigAsync(url, section, format, context.GetCancellationToken());
        });

        command.AddCommand(statusCommand);
        command.AddCommand(sessionsCommand);
        command.AddCommand(providersCommand);
        command.AddCommand(configCommand);

        return command;
    }

    internal static async Task<int> ExecuteStatusAsync(string baseUrl, string format, CancellationToken ct)
    {
        using var client = CreateClient(baseUrl);
        try
        {
            var health = await client.GetAsync("/health", ct);
            if (!health.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine("[red]Gateway is not healthy.[/] Status: {0}", health.StatusCode);
                return 1;
            }

            var diagnostics = await client.GetFromJsonAsync<JsonElement>("/api/diagnostics/threadpool", ct);
            var activity = await client.GetFromJsonAsync<JsonElement>("/api/diagnostics/activity", ct);

            if (format == "json")
            {
                var result = new { health = "healthy", diagnostics, activity };
                AnsiConsole.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                AnsiConsole.Write(new Rule("[bold blue]Gateway Status[/]") { Justification = Justify.Left });
                AnsiConsole.MarkupLine("[green]● Healthy[/]");
                AnsiConsole.WriteLine();

                if (diagnostics.TryGetProperty("pendingWorkItems", out var pending))
                    AnsiConsole.MarkupLine("[dim]ThreadPool pending:[/] {0}", pending);
                if (diagnostics.TryGetProperty("workerThreads", out var workers))
                    AnsiConsole.MarkupLine("[dim]Worker threads:[/]    {0}", workers);
                if (activity.TryGetProperty("lastActivityUtc", out var lastAct))
                    AnsiConsole.MarkupLine("[dim]Last activity:[/]    {0}", lastAct);
                if (activity.TryGetProperty("inactivitySeconds", out var inactivity))
                    AnsiConsole.MarkupLine("[dim]Inactivity:[/]       {0}s", inactivity);
            }

            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine("[red]Cannot reach gateway at {0}:[/] {1}", Markup.Escape(baseUrl), Markup.Escape(ex.Message));
            return 1;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[red]Request to gateway timed out.[/]");
            return 1;
        }
    }

    internal static async Task<int> ExecuteSessionsAsync(string baseUrl, string? agentId, int limit, string format, CancellationToken ct)
    {
        using var client = CreateClient(baseUrl);
        try
        {
            var query = agentId != null ? $"?agentId={Uri.EscapeDataString(agentId)}" : "";
            var sessions = await client.GetFromJsonAsync<JsonElement>($"/api/sessions/stats{query}", ct);

            if (format == "json")
            {
                AnsiConsole.WriteLine(JsonSerializer.Serialize(sessions, JsonOptions));
            }
            else
            {
                AnsiConsole.Write(new Rule("[bold blue]Session Statistics[/]") { Justification = Justify.Left });
                if (sessions.TryGetProperty("totalSessions", out var total))
                    AnsiConsole.MarkupLine("[dim]Total sessions:[/]  {0}", total);
                if (sessions.TryGetProperty("activeSessions", out var active))
                    AnsiConsole.MarkupLine("[dim]Active sessions:[/] {0}", active);
                if (sessions.TryGetProperty("agentBreakdown", out var breakdown) && breakdown.ValueKind == JsonValueKind.Object)
                {
                    AnsiConsole.WriteLine();
                    var table = new Table().AddColumn("Agent").AddColumn("Count");
                    foreach (var prop in breakdown.EnumerateObject())
                        table.AddRow(prop.Name, prop.Value.ToString());
                    AnsiConsole.Write(table);
                }
            }

            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine("[red]Cannot reach gateway at {0}:[/] {1}", Markup.Escape(baseUrl), Markup.Escape(ex.Message));
            return 1;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[red]Request to gateway timed out.[/]");
            return 1;
        }
    }

    internal static async Task<int> ExecuteProvidersAsync(string baseUrl, string format, CancellationToken ct)
    {
        using var client = CreateClient(baseUrl);
        try
        {
            var providers = await client.GetFromJsonAsync<JsonElement>("/api/providers", ct);
            var models = await client.GetFromJsonAsync<JsonElement>("/api/models", ct);

            if (format == "json")
            {
                var result = new { providers, models };
                AnsiConsole.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                AnsiConsole.Write(new Rule("[bold blue]Providers[/]") { Justification = Justify.Left });
                var table = new Table().AddColumn("Provider").AddColumn("Models");

                if (providers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in providers.EnumerateArray())
                    {
                        var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                        var modelCount = 0;
                        if (models.ValueKind == JsonValueKind.Array)
                        {
                            modelCount = models.EnumerateArray()
                                .Count(m => m.TryGetProperty("provider", out var mp) &&
                                            string.Equals(mp.GetString(), name, StringComparison.OrdinalIgnoreCase));
                        }
                        table.AddRow(Markup.Escape(name), modelCount.ToString());
                    }
                }

                AnsiConsole.Write(table);
            }

            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine("[red]Cannot reach gateway at {0}:[/] {1}", Markup.Escape(baseUrl), Markup.Escape(ex.Message));
            return 1;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[red]Request to gateway timed out.[/]");
            return 1;
        }
    }

    internal static async Task<int> ExecuteConfigAsync(string baseUrl, string? section, string format, CancellationToken ct)
    {
        using var client = CreateClient(baseUrl);
        try
        {
            var config = await client.GetFromJsonAsync<JsonElement>("/api/config", ct);

            if (section != null && config.TryGetProperty(section, out var sectionValue))
                config = sectionValue;
            else if (section != null)
            {
                AnsiConsole.MarkupLine("[yellow]Section '{0}' not found in configuration.[/]", Markup.Escape(section));
                return 1;
            }

            if (format == "json")
            {
                AnsiConsole.WriteLine(JsonSerializer.Serialize(config, JsonOptions));
            }
            else
            {
                AnsiConsole.Write(new Rule("[bold blue]Configuration[/]") { Justification = Justify.Left });
                AnsiConsole.WriteLine(JsonSerializer.Serialize(config, JsonOptions));
            }

            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine("[red]Cannot reach gateway at {0}:[/] {1}", Markup.Escape(baseUrl), Markup.Escape(ex.Message));
            return 1;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[red]Request to gateway timed out.[/]");
            return 1;
        }
    }

    private static HttpClient CreateClient(string baseUrl)
    {
        return new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }
}