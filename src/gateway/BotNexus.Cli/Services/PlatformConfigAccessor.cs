using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Services;

/// <summary>
/// Resolves <see cref="PlatformConfig"/> for a CLI invocation through the framework configuration
/// pipeline (#3504).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the CLI needs this at all.</b> The gateway resolves <c>IOptionsMonitor&lt;PlatformConfig&gt;</c>
/// straight from DI, because its configuration path is fixed when the host is built. A CLI
/// invocation chooses its path per run via <c>--target</c>, so the path is not known at container
/// construction time and a singleton binding cannot be registered up front.
/// </para>
/// <para>
/// The answer is to build the pipeline per resolved path, NOT to read the file by hand. Every CLI
/// command previously called <c>PlatformConfigLoader.LoadAsync</c> directly, which meant fourteen
/// commands that could not see the SQLite store, got no last-known-good protection on a malformed
/// file (#2358), and would report values the running gateway was not using.
/// </para>
/// </remarks>
public interface IPlatformConfigAccessor
{
    /// <summary>
    /// Returns the effective configuration for <paramref name="configPath"/>.
    /// </summary>
    /// <remarks>
    /// Values come from the JSON file and, when <c>config.db</c> exists beside it, the SQLite store -
    /// with the store winning, exactly as in the gateway. A missing file yields defaults rather than
    /// throwing, matching the previous loaders' <c>optional: true</c> behaviour.
    /// </remarks>
    PlatformConfig Get(string configPath);
}

/// <inheritdoc />
public sealed class PlatformConfigAccessor : IPlatformConfigAccessor
{
    /// <summary>
    /// Process-wide accessor for CLI call sites that are static or otherwise outside the container.
    /// </summary>
    /// <remarks>
    /// A static entry point is a compromise and worth naming as one. Most CLI config resolution
    /// happens in static helper methods on command classes, and threading an injected dependency
    /// through every one of them would be a far larger change than this issue's scope - which is to
    /// remove the hand-rolled reads, not to restructure the CLI's composition. Commands that DO have
    /// constructor injection should take <see cref="IPlatformConfigAccessor"/>.
    /// </remarks>
    public static IPlatformConfigAccessor Shared { get; } = new PlatformConfigAccessor();

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Deliberately not cached.</b> The obvious optimisation - cache the built pipeline per path -
    /// is wrong here, and the test suite proved it: a CLI invocation that mutates config (add an
    /// agent, add a provider, set a key, remove the agent) reads between every write, and a cached
    /// value serves the pre-mutation document to every subsequent step.
    /// </para>
    /// <para>
    /// A CLI process is short-lived and reads config a handful of times, so rebuilding is cheap and
    /// correct. Caching would trade a few milliseconds for a class of stale-read bug that only
    /// appears in multi-step commands - exactly the kind that reaches production because a single
    /// read looks fine.
    /// </para>
    /// </remarks>
    public PlatformConfig Get(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        return PlatformConfigurationSources.BuildMonitor(configPath).CurrentValue;
    }
}
