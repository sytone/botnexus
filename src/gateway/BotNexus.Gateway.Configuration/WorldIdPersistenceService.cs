using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Persists the resolved <see cref="WorldId"/> into <c>config.json</c> on first start.
/// </summary>
/// <remarks>
/// <para>The identity is resolved once at DI-registration time (see the <see cref="WorldId"/>
/// singleton registration) and this service only writes it out. It deliberately does <b>not</b>
/// re-derive the value: writing a second derivation is exactly the failure mode #2834 exists to
/// prevent.</para>
/// <para>A home that already carries a <c>worldId</c> is left byte-for-byte untouched - no write is
/// issued at all, so a start against an existing world cannot rewrite, reformat, or back up
/// <c>config.json</c>.</para>
/// </remarks>
public sealed class WorldIdPersistenceService : IHostedService
{
    private readonly PlatformConfigWriter _writer;
    private readonly WorldId _identity;
    private readonly WorldIdOrigin _origin;
    private readonly ILogger<WorldIdPersistenceService> _logger;

    public WorldIdPersistenceService(
        PlatformConfigWriter writer,
        WorldId identity,
        WorldIdOrigin origin,
        ILogger<WorldIdPersistenceService> logger)
    {
        _writer = writer;
        _identity = identity;
        _origin = origin;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_origin.WasGenerated)
            return;

        try
        {
            await _writer.MutateAsync(
                root => root[WorldIdResolver.ConfigPropertyName] = _identity.Value,
                "world-identity-bootstrap",
                cancellationToken);

            // Creating a world identity happens exactly once in a home's lifetime - notable enough
            // for information level, and quiet on every subsequent start because we return above.
            _logger.LogInformation(
                "Created world identity {WorldId} and persisted it to config.json.",
                _identity.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only config mount must not prevent the gateway from starting; the resolved
            // identity is still valid for this process, it just will not survive a restart.
            _logger.LogWarning(
                ex,
                "Could not persist world identity {WorldId} to config.json; continuing with an in-memory identity.",
                _identity.Value);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Records whether the injected <see cref="WorldId"/> was read from disk or minted during this
/// start. Kept separate from <see cref="WorldId"/> so consumers of the identity cannot branch
/// on provenance - they only ever see the one resolved value.
/// </summary>
/// <param name="WasGenerated"><see langword="true"/> when the ID was minted during this start.</param>
public sealed record WorldIdOrigin(bool WasGenerated);
