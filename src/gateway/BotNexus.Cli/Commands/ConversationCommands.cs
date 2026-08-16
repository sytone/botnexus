using BotNexus.Domain.Text;
using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Cli.Diagnostics;
using BotNexus.Cli.Services;
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
        var urlOption = new Option<string>("--url", () => GatewayClientFactory.DefaultUrl, "Gateway base URL.");
        var tokenOption = new Option<string?>("--token", "Gateway API credential. Required when --url is not the local gateway.");
        command.AddOption(formatOption);
        command.AddOption(urlOption);
        command.AddOption(tokenOption);

        // ── list ──
        var agentOption = new Option<string?>("--agent", "Filter by agent ID.");
        var listCommand = new Command("list", "List active conversations.")
        {
            agentOption
        };
        listCommand.SetHandler(async context =>
        {
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var url = context.ParseResult.GetValueForOption(urlOption) ?? GatewayClientFactory.DefaultUrl;
            var token = context.ParseResult.GetValueForOption(tokenOption);
            var agent = context.ParseResult.GetValueForOption(agentOption);
            context.ExitCode = await ExecuteListAsync(url, agent, format, CancellationToken.None, token);
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
            var url = context.ParseResult.GetValueForOption(urlOption) ?? GatewayClientFactory.DefaultUrl;
            var token = context.ParseResult.GetValueForOption(tokenOption);
            var id = context.ParseResult.GetValueForArgument(idArgument);
            context.ExitCode = await ExecuteInspectAsync(url, id, format, CancellationToken.None, token);
        });

        // ── archive ──
        var archiveIdArgument = new Argument<string>("id", "Conversation ID to archive.");
        var archiveCommand = new Command("archive", "Archive a conversation.")
        {
            archiveIdArgument
        };
        archiveCommand.SetHandler(async context =>
        {
            var url = context.ParseResult.GetValueForOption(urlOption) ?? GatewayClientFactory.DefaultUrl;
            var token = context.ParseResult.GetValueForOption(tokenOption);
            var id = context.ParseResult.GetValueForArgument(archiveIdArgument);
            context.ExitCode = await ExecuteArchiveAsync(url, id, CancellationToken.None, token);
        });

        command.AddCommand(listCommand);
        command.AddCommand(inspectCommand);
        command.AddCommand(archiveCommand);

        return command;
    }

    internal static async Task<int> ExecuteListAsync(string baseUrl, string? agentId, string format, CancellationToken ct, string? token = null)
    {
        var resolution = CreateClient(baseUrl, token);
        if (resolution.Client is null)
        {
            AnsiConsole.MarkupLine("[red]{0}[/]", CliText.SafeDisplay(resolution.RefusalMessage!));
            return 1;
        }

        using var client = resolution.Client;
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
                            CliText.SafeDisplay(TruncateId(id)),
                            CliText.SafeDisplay(agent),
                            CliText.SafeDisplay(Truncate(title, 40)),
                            CliText.SafeDisplay(updated));
                    }
                }

                AnsiConsole.Write(table);
            }
            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine("[red]Cannot reach gateway at {0}:[/] {1}", CliText.SafeDisplay(GatewayDiagnosticsProjection.ProjectUrl(baseUrl)), CliText.SafeDisplay(GatewayDiagnosticsProjection.ProjectMessage(ex.Message)));
            return 1;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[red]Request to gateway timed out.[/]");
            return 1;
        }
    }

    internal static async Task<int> ExecuteInspectAsync(string baseUrl, string conversationId, string format, CancellationToken ct, string? token = null)
    {
        var resolution = CreateClient(baseUrl, token);
        if (resolution.Client is null)
        {
            AnsiConsole.MarkupLine("[red]{0}[/]", CliText.SafeDisplay(resolution.RefusalMessage!));
            return 1;
        }

        using var client = resolution.Client;
        try
        {
            var response = await client.GetAsync($"/api/conversations/{Uri.EscapeDataString(conversationId)}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine("[yellow]Conversation '{0}' not found.[/]", CliText.SafeDisplay(conversationId));
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
                    AnsiConsole.MarkupLine("[dim]ID:[/]      {0}", CliText.SafeDisplay(cid.GetString() ?? ""));
                if (conversation.TryGetProperty("agentId", out var aid))
                    AnsiConsole.MarkupLine("[dim]Agent:[/]   {0}", CliText.SafeDisplay(aid.GetString() ?? ""));
                if (conversation.TryGetProperty("title", out var title))
                    AnsiConsole.MarkupLine("[dim]Title:[/]   {0}", CliText.SafeDisplay(title.GetString() ?? ""));
                if (conversation.TryGetProperty("status", out var status))
                    AnsiConsole.MarkupLine("[dim]Status:[/]  {0}", CliText.SafeDisplay(status.GetString() ?? ""));
                if (conversation.TryGetProperty("createdUtc", out var created))
                    AnsiConsole.MarkupLine("[dim]Created:[/] {0}", CliText.SafeDisplay(created.GetString() ?? ""));
                if (conversation.TryGetProperty("lastUpdatedUtc", out var updated))
                    AnsiConsole.MarkupLine("[dim]Updated:[/] {0}", CliText.SafeDisplay(updated.GetString() ?? ""));

                if (conversation.TryGetProperty("participants", out var participants) && participants.ValueKind == JsonValueKind.Array)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[dim]Participants:[/] {0}", participants.GetArrayLength());
                    foreach (var p in participants.EnumerateArray())
                    {
                        var citizenId = p.TryGetProperty("citizenId", out var pid) ? pid.GetString() ?? "" : "";
                        AnsiConsole.MarkupLine("  - {0}", CliText.SafeDisplay(citizenId));
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
            AnsiConsole.MarkupLine("[red]Cannot reach gateway at {0}:[/] {1}", CliText.SafeDisplay(GatewayDiagnosticsProjection.ProjectUrl(baseUrl)), CliText.SafeDisplay(GatewayDiagnosticsProjection.ProjectMessage(ex.Message)));
            return 1;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[red]Request to gateway timed out.[/]");
            return 1;
        }
    }

    internal static async Task<int> ExecuteArchiveAsync(string baseUrl, string conversationId, CancellationToken ct, string? token = null)
    {
        var resolution = CreateClient(baseUrl, token);
        if (resolution.Client is null)
        {
            AnsiConsole.MarkupLine("[red]{0}[/]", CliText.SafeDisplay(resolution.RefusalMessage!));
            return 1;
        }

        using var client = resolution.Client;
        try
        {
            var response = await client.DeleteAsync(
                $"/api/conversations/{Uri.EscapeDataString(conversationId)}",
                ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine("[yellow]Conversation '{0}' not found.[/]", CliText.SafeDisplay(conversationId));
                return 1;
            }

            response.EnsureSuccessStatusCode();
            AnsiConsole.MarkupLine("[green]Conversation '{0}' archived.[/]", CliText.SafeDisplay(conversationId));
            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine("[red]Cannot reach gateway at {0}:[/] {1}", CliText.SafeDisplay(GatewayDiagnosticsProjection.ProjectUrl(baseUrl)), CliText.SafeDisplay(GatewayDiagnosticsProjection.ProjectMessage(ex.Message)));
            return 1;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[red]Request to gateway timed out.[/]");
            return 1;
        }
    }

    // Delegates to the one factory (issue #2747) so the credential policy - local
    // credential never follows an operator-supplied --url - is defined in a single place.
    private static GatewayClientResolution CreateClient(string baseUrl, string? token)
        => GatewayClientFactory.Resolve(
            baseUrl,
            TimeSpan.FromSeconds(10),
            token,
            GatewayClientFactory.DefaultCredentialSource());

    /// <summary>
    /// Shortens an identifier for display. Deliberately keeps raw slicing: ids are generated
    /// ASCII (a <c>c_</c>/<c>s_</c> prefix plus hex), never user text, so no surrogate can occur
    /// here and the #2883 helper would only add indirection.
    /// </summary>
    private static string TruncateId(string id)
        => id.Length > 12 ? id[..12] + "..." : id;

    private static string Truncate(string value, int maxLength)
        => TextTruncation.SafeTruncate(value, maxLength, "...")!;
}