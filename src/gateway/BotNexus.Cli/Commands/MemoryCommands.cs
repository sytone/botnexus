using System.CommandLine;
using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using BotNexus.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Cli.Commands;

internal sealed class MemoryCommands
{
    public Command Build(Option<bool> verboseOption)
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
            context.ExitCode = await ExecuteBackfillAsync(agentId, verbose, CancellationToken.None);
        });

        command.AddCommand(backfillCommand);
        return command;
    }

    private static async Task<int> ExecuteBackfillAsync(string? agentFilter, bool verbose, CancellationToken ct)
    {
        var config = await LoadConfigRequiredAsync(ct);
        if (config is null)
            return 1;

        var home = new BotNexusHome();
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

        if (resolvedType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = sessionStore?.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.WriteLine("Error: gateway.sessionStore.connectionString is required for Sqlite session stores.");
                return 1;
            }

            store = new SqliteSessionStore(
                connectionString,
                NullLoggerFactory.Instance.CreateLogger<SqliteSessionStore>());
        }
        else if (resolvedType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var configuredPath = sessionStore?.FilePath ?? sessionsDirectory;
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                Console.WriteLine("Error: gateway.sessionStore.filePath is required for File session stores.");
                return 1;
            }

            var sessionsPath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(home.RootPath, configuredPath);

            store = new FileSessionStore(
                sessionsPath,
                NullLoggerFactory.Instance.CreateLogger<FileSessionStore>(),
                fileSystem);
        }
        else
        {
            Console.WriteLine($"Error: session store type '{resolvedType}' does not support backfill. Use 'Sqlite' or 'File'.");
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
                Console.WriteLine();
                Console.WriteLine($"Backfill complete: {result.SessionsProcessed} session(s) processed, {result.TurnsIndexed} turn(s) indexed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: backfill failed — {ex.Message}");
                if (verbose)
                    Console.WriteLine(ex.ToString());
                return 1;
            }
        }
    }

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Error: config file not found at '{configPath}'. Run 'botnexus init' first.");
            return null;
        }

        try
        {
            return await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: unable to load config: {ex.Message}");
            return null;
        }
    }
}
