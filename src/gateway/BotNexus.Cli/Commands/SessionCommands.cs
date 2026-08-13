using System.CommandLine;
using System.IO.Abstractions;
using BotNexus.Cli.Services;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Supported CLI lifecycle management for sessions: list, archive, delete (issue #2812).
/// </summary>
/// <remarks>
/// <para>
/// Every operation goes through <see cref="ISessionStore"/> - the same abstraction the gateway
/// writes through - and never opens <c>sessions.db</c> directly. That matters because the store
/// enforces invariants a raw SQL statement cannot: archiving drains the in-flight run bound to the
/// session before sealing it (#2903), and deletion removes the transcript rows alongside the
/// session row. The throwaway SQLite scripts this command replaces bypassed both.
/// </para>
/// <para>
/// <c>botnexus debug sessions</c> remains a separate, deliberately read-only offline dump that
/// reads the database file directly for diagnostics; it is not migrated here.
/// </para>
/// </remarks>
internal sealed class SessionCommands
{
    /// <summary>
    /// Characters that turn an id into a selector. Delete refuses these outright rather than
    /// interpreting them: a glob that matches more than the operator expected is unrecoverable,
    /// and bulk/pattern deletion is explicitly out of scope for #2812.
    /// </summary>
    private static readonly char[] AmbiguousSelectorCharacters = ['*', '?', '%', ','];

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("session", "Manage session lifecycle through the gateway session store.");

        var formatOption = new Option<string>("--format", () => "table", "Output format: table or json.");
        command.AddOption(formatOption);

        // ── list ──
        var agentOption = new Option<string?>("--agent", "Filter by agent ID.");
        var limitOption = new Option<int>("--limit", () => 20, "Maximum sessions to show.");
        var listCommand = new Command("list", "List sessions via the session store.")
        {
            agentOption, limitOption
        };
        listCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var agent = context.ParseResult.GetValueForOption(agentOption);
            var limit = context.ParseResult.GetValueForOption(limitOption);
            context.ExitCode = await RunWithStoreAsync(
                target,
                store => ExecuteListAsync(store, agent, limit, format, CancellationToken.None));
        });

        // ── archive ──
        var archiveIdArgument = new Argument<string>("id", "Session ID to archive.");
        var archiveCommand = new Command("archive", "Archive (seal) a session, preserving its transcript.")
        {
            archiveIdArgument
        };
        archiveCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var id = context.ParseResult.GetValueForArgument(archiveIdArgument);
            context.ExitCode = await RunWithStoreAsync(
                target,
                store => ExecuteArchiveAsync(store, id, CancellationToken.None));
        });

        // ── delete ──
        var deleteIdArgument = new Argument<string>("id", "Exact session ID to delete. Patterns are refused.");
        var deleteCommand = new Command("delete", "Permanently delete a session and its transcript.")
        {
            deleteIdArgument
        };
        deleteCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var id = context.ParseResult.GetValueForArgument(deleteIdArgument);
            context.ExitCode = await RunWithStoreAsync(
                target,
                store => ExecuteDeleteAsync(store, id, CancellationToken.None));
        });

        command.AddCommand(listCommand);
        command.AddCommand(archiveCommand);
        command.AddCommand(deleteCommand);
        return command;
    }

    // ── Execution (store-injected, so tests assert store state rather than console output) ──

    internal static async Task<int> ExecuteListAsync(
        ISessionStore store,
        string? agentId,
        int limit,
        string format,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        AgentId? filter = !string.IsNullOrWhiteSpace(agentId) ? AgentId.From(agentId) : null;
        var sessions = await store.ListAsync(filter, ct).ConfigureAwait(false);

        var page = sessions
            .OrderByDescending(session => session.UpdatedAt)
            .Take(Math.Max(0, limit))
            .ToList();

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            var projection = page.Select(session => new
            {
                sessionId = session.SessionId.ToString(),
                agentId = session.AgentId.ToString(),
                conversationId = session.ConversationId.ToString(),
                status = session.Status.ToString(),
                messageCount = session.MessageCount,
                updatedAt = session.UpdatedAt
            });
            AnsiConsole.WriteLine(System.Text.Json.JsonSerializer.Serialize(projection, JsonOptions));
            return 0;
        }

        if (page.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sessions found.[/]");
            return 0;
        }

        var table = new Table()
            .AddColumn("Session ID")
            .AddColumn("Agent")
            .AddColumn("Status")
            .AddColumn("Messages")
            .AddColumn("Updated");

        foreach (var session in page)
        {
            table.AddRow(
                Markup.Escape(session.SessionId.ToString()),
                Markup.Escape(session.AgentId.ToString()),
                Markup.Escape(session.Status.ToString()),
                session.MessageCount.ToString(),
                Markup.Escape(session.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{page.Count} session(s) shown.[/]");
        return 0;
    }

    internal static async Task<int> ExecuteArchiveAsync(ISessionStore store, string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var refusal = ValidateExplicitId(id);
        if (refusal is not null)
        {
            AnsiConsole.MarkupLine("[red]{0}[/]", Markup.Escape(refusal));
            return 2;
        }

        var sessionId = SessionId.From(id.Trim());
        var existing = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            AnsiConsole.MarkupLine("[yellow]Session '{0}' not found.[/]", Markup.Escape(id));
            return 1;
        }

        // Idempotence (AC2): an already-sealed session is already in the requested state, so the
        // archive is reported as success WITHOUT calling the store again. Re-archiving is not a
        // harmless no-op at the store: ArchiveAsync drains the bound run and rewrites the row's
        // UpdatedAt, so calling it a second time would change state the caller asked to leave alone.
        if (existing.Status == SessionStatus.Sealed)
        {
            AnsiConsole.MarkupLine("[green]Session '{0}' is already archived.[/]", Markup.Escape(id));
            return 0;
        }

        await store.ArchiveAsync(sessionId, ct).ConfigureAwait(false);
        AnsiConsole.MarkupLine("[green]Session '{0}' archived.[/]", Markup.Escape(id));
        return 0;
    }

    internal static async Task<int> ExecuteDeleteAsync(ISessionStore store, string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var refusal = ValidateExplicitId(id);
        if (refusal is not null)
        {
            AnsiConsole.MarkupLine("[red]{0}[/]", Markup.Escape(refusal));
            return 2;
        }

        var sessionId = SessionId.From(id.Trim());
        var existing = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            AnsiConsole.MarkupLine("[yellow]Session '{0}' not found.[/]", Markup.Escape(id));
            return 1;
        }

        await store.DeleteAsync(sessionId, ct).ConfigureAwait(false);
        AnsiConsole.MarkupLine("[green]Session '{0}' deleted.[/]", Markup.Escape(id));
        return 0;
    }

    /// <summary>
    /// Returns a refusal message when <paramref name="id"/> is empty or is a selector rather than an
    /// exact id, or <c>null</c> when it is an acceptable explicit id (AC3).
    /// </summary>
    internal static string? ValidateExplicitId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "A session id is required. Refusing to operate on an empty selector.";

        var trimmed = id.Trim();
        if (trimmed.IndexOfAny(AmbiguousSelectorCharacters) >= 0)
            return $"'{trimmed}' looks like a pattern. Refusing an ambiguous selector - pass one exact session id.";

        if (trimmed.Contains(' ', StringComparison.Ordinal))
            return $"'{trimmed}' contains whitespace. Refusing an ambiguous selector - pass one exact session id.";

        return null;
    }

    // ── Store resolution ──

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> RunWithStoreAsync(string? target, Func<ISessionStore, Task<int>> action)
    {
        var home = CliPaths.ResolveTarget(target);
        var configPath = Path.Combine(home, "config.json");
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] Config file not found at [dim]{0}[/]. Run [green]botnexus init[/] first.",
                Markup.Escape(configPath));
            return 1;
        }

        PlatformConfig config;
        try
        {
            config = await PlatformConfigLoader.LoadAsync(configPath, CancellationToken.None, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Unable to load config: {0}", Markup.Escape(ex.Message));
            return 1;
        }

        var resolution = CliSessionStoreFactory.Resolve(config, new BotNexusHome(home), new FileSystem());
        if (resolution.Store is null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(resolution.RefusalMessage!));
            return 1;
        }

        return await action(resolution.Store).ConfigureAwait(false);
    }
}
