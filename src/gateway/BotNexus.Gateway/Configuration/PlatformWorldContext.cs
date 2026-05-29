using BotNexus.Domain;
using BotNexus.Gateway.Abstractions.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Default <see cref="IWorldContext"/> backed by the live <see cref="PlatformConfig"/>.
/// Delegates to <see cref="WorldIdentityResolver"/> on every read so configuration reloads
/// (e.g. operator edits to <c>config.json</c>) take effect without restarting the gateway.
/// </summary>
public sealed class PlatformWorldContext : IWorldContext
{
    private readonly IOptionsMonitor<PlatformConfig> _platformConfig;

    /// <summary>Initialises a new <see cref="PlatformWorldContext"/>.</summary>
    /// <param name="platformConfig">Monitor for live platform configuration.</param>
    public PlatformWorldContext(IOptionsMonitor<PlatformConfig> platformConfig)
    {
        _platformConfig = platformConfig;
    }

    /// <inheritdoc />
    public WorldIdentity Current => WorldIdentityResolver.Resolve(_platformConfig.CurrentValue);
}
