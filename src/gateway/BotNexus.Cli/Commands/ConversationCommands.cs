using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI subcommand group for conversation management via the gateway REST API.
/// Provides list, inspect, and archive operations against a running gateway instance.
/// </summary>
internal sealed class ConversationCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("conversation", "Manage conversations via the gateway REST API.");

        var formatOption = new Option<string>("--format", () => "table", "Output format: table or json.");
        var urlOption = new Option<string>("--url", () => "http://localhost:5005", "Gateway base URL.");
        command.AddOption(formatOption);
        command.AddOption(urlOption);

        // ── list ──
        var agentOption = new Option<string?>("--agent", "Filter by agent ID.");
        var listCommand = new Command("list", "List active conversations.")
        {
            agentOption
        };
        listCommand.SetHandler(async context =>
        {
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var url = context.ParseResult.GetValueForOption(urlOption) ?? "http://localhost:5005";
            var agent = context.ParseResult.GetValueForOption(agentOption);
            context.ExitCode = await ExecuteListAsync(url, agent, format, CancellationToken.None);
        });

        // ── inspect ──
        var idArgument = new Argument<string>("id", "Conversation ID to inspect.");
        var inspectCommand = new Command("inspect", "Show conversation metadata, participants, and bindings.")
        {
            idArgument
        };
        inspectCommand.SetHandler(async context =>
        {
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var url = context.ParseResult.GetValueForOption(urlOption) ?? "http://localhost:5005";
            var id = context.ParseResult.GetValueForArgument(idArgument);
            context.ExitCode = await ExecuteInspectAsync(url, id, format, CancellationToken.None);
        });

        // ── archive ──
        var archiveIdArgument = new Argument<string>("id", "Conversation ID to archive.");
        var archiveCommand = new Command("archive", "Archive a conversation.")
        {
            archiveIdArgument
        };
        archiveCommand.SetHandler(async context =>
        {
            var url = context.ParseResult.GetValueForOption(urlOption) ?? "http://localhost:5005";
            var id = context.ParseResult.GetValueForArgument(archiveIdArgument);
            context.ExitCode = await ExecuteArchiveAsync(url, id, CancellationToken.None);
        });

        command.AddCommand(listCommand);
        command.AddCommand(inspectCommand);
        command.AddCommand(archiveCommand);

        return command;
    }

    internal static async Task<int> ExecuteListAsync(string baseUrl, string? agentId, string format, CancellationToken ct)
    {
        using var client = CreateClient(baseUrl);
        try
        {
            var query = agentId != null ? $"?agentId={Uri.EscapeDataString(agentId)}" : "";
            var conversations = await client.GetFromJsonAsync<JsonElement>($"/api/conversations{query}", ct);

            if (format == "json")
            {
                AnsiConsole.WriteLine(JsonSerializer.Serialize(conversations, JsonOptions));
            }
            else
            {
                var table = new Table()
                    .AddColumn("ID")
                    .AddColumn("Agent")
                    .AddColumn("Title")
                    .AddColumn("Updated");

                if (conversations.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in conversations.EnumerateArray())
                    {
                        var id = c.TryGetProperty("conversationId", out var cid) ? cid.GetString() ?? "" : "";
                        var agent = c.TryGetProperty("agentId", out var aid) ? aid.GetString() ?? "" : "";
                        var title = c.TryGetProperty("title", out var t) ? t.GetString() ?? "(untitled)" : "(untitled)";
                        var updated = c.TryGetProperty("lastUpdatedUtc", out var u) ? u.GetString() ?? "" : "";
                        table.AddRow(
                            Markup.Escape(TruncateId(id)),
                            Markup.Escape(agent),
                            Markup.Escape(Truncate(title, 40)),
                            Markup.Escape(updated));
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

    internal static async Task<int> ExecuteInspectAsync(string baseUrl, string conversationId, string format, CancellationToken ct)
    {
        using var client = CreateClient(baseUrl);
        try
        {
            var response = await client.GetAsync($"/api/conversations/{Uri.EscapeDataString(conversationId)}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine("[yellow]Conversation '{0}' not found.[/]", Markup.Escape(conversationId));
                return 1;
            }
            response.EnsureSuccessStatusCode();
            var conversation = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

            if (format == "json")
            {
                AnsiConsole.WriteLine(JsonSerializer.Serialize(conversation, JsonOptions));
            }
            else
            {
                AnsiConsole.Write(new Rule("[bold blue]Conversation[/]") { Justification = Justify.Left });

                if (conversation.TryGetProperty("conversationId", out var cid))
                    AnsiConsole.MarkupLine("[dim]ID:[/]      {0}", Markup.Escape(cid.GetString() ?? ""));
                if (conversation.TryGetProperty("agentId", out var aid))
                    AnsiConsole.MarkupLine("[dim]Agent:[/]   {0}", Markup.Escape(aid.GetString() ?? ""));
                if (conversation.TryGetProperty("title", out var title))
                    AnsiConsole.MarkupLine("[dim]Title:[/]   {0}", Markup.Escape(title.GetString() ?? ""));
                if (conversation.TryGetProperty("status", out var status))
                    AnsiConsole.MarkupLine("[dim]Status:[/]  {0}", Markup.Escape(status.GetString() ?? ""));
                if (conversation.TryGetProperty("createdUtc", out var created))
                    AnsiConsole.MarkupLine("[dim]Created:[/] {0}", Markup.Escape(created.GetString() ?? ""));
                if (conversation.TryGetProperty("lastUpdatedUtc", out var updated))
                    AnsiConsole.MarkupLine("[dim]Updated:[/] {0}", Markup.Escape(updated.GetString() ?? ""));

                if (conversation.TryGetProperty("participants", out var participants) && participants.ValueKind == JsonValueKind.Array)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[dim]Participants:[/] {0}", participants.GetArrayLength());
                    foreach (var p in participants.EnumerateArray())
                    {
                        var citizenId = p.TryGetProperty("citizenId", out var pid) ? pid.GetString() ?? "" : "";
                        AnsiConsole.MarkupLine("  - {0}", Markup.Escape(citizenId));
                    }
                }

                if (conversation.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Array)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[dim]Bindings:[/] {0}", bindings.GetArrayLength());
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

    internal static async Task<int> ExecuteArchiveAsync(string baseUrl, string conversationId, CancellationToken ct)
    {
        using var client = CreateClient(baseUrl);
        try
        {
            var response = await client.DeleteAsync(
                $"/api/conversations/{Uri.EscapeDataString(conversationId)}",
                ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine("[yellow]Conversation '{0}' not found.[/]", Markup.Escape(conversationId));
                return 1;
            }

            response.EnsureSuccessStatusCode();
            AnsiConsole.MarkupLine("[green]Conversation '{0}' archived.[/]", Markup.Escape(conversationId));
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
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private static string TruncateId(string id)
        => id.Length > 12 ? id[..12] + "..." : id;

    private static string Truncate(string value, int maxLength)
        => value.Length > maxLength ? value[..maxLength] + "..." : value;
}