using System.CommandLine;
using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class MemoryCommands
{
    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("memory", "Memory store operations.");

        var agentOption = new Option<string?>("--agent", "Backfill only this agent. If omitted, backfill all agents.");

        var backfillCommand = new Command("backfill", "Index conversation turns from existing sessions into memory stores.")
        {
            agentOption
        };

        backfillCommand.SetHandler(async context =>
        {
            var agentId = context.ParseResult.GetValueForOption(agentOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var home = CliPaths.ResolveTarget(target);
            var configPath = Path.Combine(home, "config.json");
            context.ExitCode = await ExecuteBackfillAsync(agentId, configPath, verbose, CancellationToken.None);
        });

        command.AddCommand(backfillCommand);
        return command;
    }

    private static async Task<int> ExecuteBackfillAsync(string? agentFilter, bool verbose, CancellationToken ct)
        => await ExecuteBackfillAsync(agentFilter, PlatformConfigLoader.DefaultConfigPath, verbose, ct);

    private static async Task<int> ExecuteBackfillAsync(string? agentFilter, string configPath, bool verbose, CancellationToken ct)
    {
        var config = await LoadConfigRequiredAsync(configPath, ct);
        if (config is null)
            return 1;

        var home = new BotNexusHome(Path.GetDirectoryName(configPath));
        var fileSystem = new FileSystem();

        // Resolve session store from config
        var sessionStore = config.Gateway?.SessionStore;
        var explicitType = sessionStore?.Type?.Trim();
        var sessionsDirectory = config.Gateway?.SessionsDirectory;
        var resolvedType = !string.IsNullOrWhiteSpace(explicitType)
            ? explicitType
            : !string.IsNullOrWhiteSpace(sessionsDirectory)
                ? "File"
                : "InMemory";

        Gateway.Abstractions.Sessions.ISessionStore store;
        IConversationStore conversationStore;

        if (resolvedType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = sessionStore?.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] gateway.sessionStore.connectionString is required for Sqlite session stores.");
                return 1;
            }

            // P9-I (#674): IConversationStore is mandatory on SqliteSessionStore.
            // The conversation store shares the same SQLite database (separate tables).
            conversationStore = new SqliteConversationStore(
                connectionString,
                NullLoggerFactory.Instance.CreateLogger<SqliteConversationStore>());
            store = new SqliteSessionStore(
                connectionString,
                NullLoggerFactory.Instance.CreateLogger<SqliteSessionStore>(),
                conversationStore);
        }
        else if (resolvedType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var configuredPath = sessionStore?.FilePath ?? sessionsDirectory;
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] gateway.sessionStore.filePath is required for File session stores.");
                return 1;
            }

            var sessionsPath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(home.RootPath, configuredPath);

            // P9-I (#674): IConversationStore is mandatory on FileSessionStore.
            // Mirror the wiring in GatewayServiceCollectionExtensions: conversations live
            // in a `conversations/` subdirectory of the configured sessions path.
            var conversationsPath = Path.Combine(sessionsPath, "conversations");
            fileSystem.Directory.CreateDirectory(conversationsPath);
            conversationStore = new FileConversationStore(
                conversationsPath,
                NullLoggerFactory.Instance.CreateLogger<FileConversationStore>(),
                fileSystem);
            store = new FileSessionStore(
                sessionsPath,
                NullLoggerFactory.Instance.CreateLogger<FileSessionStore>(),
                fileSystem,
                conversationStore);
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Session store type [green]{Markup.Escape(resolvedType)}[/] does not support backfill. Use [green]Sqlite[/] or [green]File[/].");
            return 1;
        }

        var memoryStoreFactory = new MemoryStoreFactory(agentId =>
        {
            var agentDirectory = home.GetAgentDirectory(agentId);
            return Path.Combine(agentDirectory, "data", "memory.sqlite");
        });

        await using (memoryStoreFactory)
        {
            using var loggerFactory = verbose
                ? LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug))
                : LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

            var logger = loggerFactory.CreateLogger("BotNexus.Memory.Backfill");

            AgentId? filter = !string.IsNullOrWhiteSpace(agentFilter)
                ? AgentId.From(agentFilter)
                : (AgentId?)null;

            try
            {
                var result = await MemoryIndexer.BackfillAsync(store, memoryStoreFactory, logger, filter, ct).ConfigureAwait(false);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[green]\u2713[/] Backfill complete: [green]{result.SessionsProcessed}[/] session(s) processed, [green]{result.TurnsIndexed}[/] turn(s) indexed.");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Backfill failed \u2014 {Markup.Escape(ex.Message)}");
                if (verbose)
                    AnsiConsole.WriteException(ex);
                return 1;
            }
        }
    }

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(CancellationToken cancellationToken)
        => await LoadConfigRequiredAsync(PlatformConfigLoader.DefaultConfigPath, cancellationToken);

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config file not found at [dim]{Markup.Escape(configPath)}[/]. Run [green]botnexus init[/] first.");
            return null;
        }

        try
        {
            return await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to load config: {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}
