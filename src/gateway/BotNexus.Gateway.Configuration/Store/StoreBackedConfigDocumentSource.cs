using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Shadow;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Reads the platform configuration document from the SQLite store instead of <c>config.json</c>
/// (#2646 PBI 3, #2766 AC7).
///
/// <para>
/// <b>This is the cutover seam, and it is the only place that decides which source wins.</b> Callers
/// ask for a document; whether it came from the store or the file is decided here and nowhere else.
/// Spreading that decision across consumers would make the rollback a code change rather than a flag.
/// </para>
///
/// <para>
/// <b>Fails SAFE, not closed.</b> When <see cref="ConfigStoreFeatures.Authoritative"/> is off - the
/// default - the file is read and the store is not consulted at all, so the read path is byte-identical
/// to today's. When it is on but the store cannot be read or produces nothing, the file is used and the
/// failure is logged at error level. Refusing to start would convert a store defect into a total
/// outage; falling back converts it into a logged degradation with the platform still serving the
/// configuration the operator wrote by hand. The store is the new thing; the file is the thing that has
/// worked for the life of the product, so the file is the safe direction.
/// </para>
///
/// <para>
/// <b>An empty store is treated as a failure, not as an empty configuration.</b> A store containing no
/// rows is indistinguishable in shape from a valid configuration that happens to set nothing, and
/// serving the latter reading would silently reset every setting on the platform. The far more likely
/// cause of zero rows is that the shadow migration never ran, so this reads it as "the store is not
/// ready" and defers to the file.
/// </para>
/// </summary>
public sealed class StoreBackedConfigDocumentSource(
    IConfigStore store,
    IConfigShadowSource fileSource,
    IConfigStoreAuthoritativeGate gate,
    ILogger<StoreBackedConfigDocumentSource> logger) : IConfigDocumentSource
{
    /// <inheritdoc />
    public async Task<ConfigDocumentRead> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.IsAuthoritativeAsync(cancellationToken).ConfigureAwait(false))
        {
            var fileDocument = await fileSource.ReadRawDocumentAsync(cancellationToken).ConfigureAwait(false);
            return new ConfigDocumentRead(fileDocument, ConfigDocumentOrigin.File, FellBack: false);
        }

        try
        {
            var entries = await store.ReadEntriesAsync(cancellationToken).ConfigureAwait(false);

            if (entries.Count > 0)
            {
                return new ConfigDocumentRead(
                    ConfigDocumentRehydrator.Rehydrate(entries),
                    ConfigDocumentOrigin.Store,
                    FellBack: false);
            }

            logger.LogError(
                "Feature '{Feature}' is enabled but the configuration store contains no entries. " +
                "Falling back to the configuration file. An empty store is being read as 'not ready' " +
                "rather than as an empty configuration, because serving it as empty would silently " +
                "reset every platform setting.",
                ConfigStoreFeatures.Authoritative);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Feature '{Feature}' is enabled but reading the configuration store failed. Falling " +
                "back to the configuration file so the platform continues to serve the configuration " +
                "the operator wrote. Disable the flag to silence this.",
                ConfigStoreFeatures.Authoritative);
        }

        var fallback = await fileSource.ReadRawDocumentAsync(cancellationToken).ConfigureAwait(false);
        return new ConfigDocumentRead(fallback, ConfigDocumentOrigin.File, FellBack: true);
    }
}
