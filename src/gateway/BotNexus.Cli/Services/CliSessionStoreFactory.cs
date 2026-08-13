using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Cli.Services;

/// <summary>
/// The single place the CLI turns a loaded <see cref="PlatformConfig"/> into a live
/// <see cref="ISessionStore"/>.
/// </summary>
/// <remarks>
/// Why this exists as a seam rather than inline construction: the CLI must reach sessions through the
/// same store abstraction the gateway uses (issue #2812), never by opening <c>sessions.db</c> directly.
/// Two commands already need that resolution (<c>memory backfill</c> and <c>session</c>), and a second
/// hand-rolled copy is exactly how the two implementations drift apart - the recurring
/// "exemplar fixed, never propagated" defect shape. One factory means one place where the store type,
/// the mandatory conversation store and the path rules are decided.
/// </remarks>
internal sealed record CliSessionStoreResolution(
    ISessionStore? Store,
    IConversationStore? ConversationStore,
    string? RefusalMessage)
{
    public static CliSessionStoreResolution Refused(string message) => new(null, null, message);
}

internal static class CliSessionStoreFactory
{
    /// <summary>
    /// Resolves the configured session store. Returns a refusal (never throws) when the configuration
    /// selects a store that cannot be opened from the command line.
    /// </summary>
    /// <param name="config">The loaded platform configuration.</param>
    /// <param name="home">The BotNexus home the config was loaded from; relative store paths resolve against it.</param>
    /// <param name="fileSystem">File system abstraction, so tests need no real disk for the File store.</param>
    public static CliSessionStoreResolution Resolve(
        PlatformConfig config,
        BotNexusHome home,
        IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var sessionStore = config.Gateway?.SessionStore;
        var explicitType = sessionStore?.Type?.Trim();
        var sessionsDirectory = config.Gateway?.SessionsDirectory;
        var resolvedType = !string.IsNullOrWhiteSpace(explicitType)
            ? explicitType
            : !string.IsNullOrWhiteSpace(sessionsDirectory)
                ? "File"
                : "InMemory";

        if (resolvedType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = sessionStore?.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
                return CliSessionStoreResolution.Refused(
                    "gateway.sessionStore.connectionString is required for Sqlite session stores.");

            // #674: IConversationStore is mandatory on SqliteSessionStore. Conversations share the
            // same SQLite database in separate tables.
            var conversationStore = new SqliteConversationStore(
                connectionString,
                NullLoggerFactory.Instance.CreateLogger<SqliteConversationStore>());

            var store = new SqliteSessionStore(
                connectionString,
                NullLoggerFactory.Instance.CreateLogger<SqliteSessionStore>(),
                conversationStore);

            return new CliSessionStoreResolution(store, conversationStore, null);
        }

        if (resolvedType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var configuredPath = sessionStore?.FilePath ?? sessionsDirectory;
            if (string.IsNullOrWhiteSpace(configuredPath))
                return CliSessionStoreResolution.Refused(
                    "gateway.sessionStore.filePath is required for File session stores.");

            var sessionsPath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(home.RootPath, configuredPath);

            // Mirrors GatewayServiceCollectionExtensions: conversations live in a `conversations/`
            // subdirectory of the configured sessions path.
            var conversationsPath = Path.Combine(sessionsPath, "conversations");
            fileSystem.Directory.CreateDirectory(conversationsPath);

            var conversationStore = new FileConversationStore(
                conversationsPath,
                NullLoggerFactory.Instance.CreateLogger<FileConversationStore>(),
                fileSystem);

            var store = new FileSessionStore(
                sessionsPath,
                NullLoggerFactory.Instance.CreateLogger<FileSessionStore>(),
                fileSystem,
                conversationStore);

            return new CliSessionStoreResolution(store, conversationStore, null);
        }

        return CliSessionStoreResolution.Refused(
            $"Session store type '{resolvedType}' cannot be opened from the CLI. Use Sqlite or File.");
    }
}
