using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration.Shadow;

/// <summary>
/// Produces the store's round-trip of a configuration document, for comparison against the source.
///
/// <para>
/// This is the seam #2646 implements. It exists here so the shadow harness can be built, tested and
/// merged before the SQLite store does - and so the store, when it arrives, lands against a diff that
/// already works rather than shipping its own verification.
/// </para>
/// </summary>
public interface IConfigStoreRoundTrip
{
    /// <summary>
    /// Migrates <paramref name="source"/> into the store and reads it back as a document.
    /// </summary>
    /// <returns>
    /// The reconstructed document. Returning something structurally unequal to <paramref name="source"/>
    /// is the finding, not an error - the caller diffs it.
    /// </returns>
    Task<JsonObject?> MigrateAndReadBackAsync(JsonObject source, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the shadow migration on start and reports the diff, without ever affecting behaviour
/// (#2766 AC3, AC5, AC6).
///
/// <para>
/// <b>Failure is free, and that is the point.</b> A shadow migration that crashes, produces garbage or
/// loses a key costs a log line. The same failure at cutover costs an outage. This service therefore
/// catches everything: no shadow outcome - exception, cancellation, timeout or a diff full of
/// differences - may fail startup.
/// </para>
///
/// <para>
/// <b>That catch-all is load-bearing rather than defensive.</b> #2731 records the gateway dying outright
/// on a startup fault: <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>, so one background
/// service's exception terminates cron, portal, SignalR and every agent surface. A diagnostic that can
/// take the host down is worse than no diagnostic, so the exception boundary here is the difference
/// between a safety mechanism and a new outage vector.
/// </para>
///
/// <para>
/// <b>Read-only with respect to <c>config.json</c>.</b> This service never calls
/// <see cref="PlatformConfigWriter"/> and never opens the file for writing. Rollback is consequently
/// "delete the store" - there is no restore procedure, because nothing was ever modified.
/// </para>
/// </summary>
public sealed class ConfigShadowMigrationHostedService(
    IConfigShadowSource source,
    IConfigStoreRoundTrip roundTrip,
    IConfigShadowReportSink sink,
    IConfigShadowGate gate,
    ILogger<ConfigShadowMigrationHostedService> logger,
    TimeProvider? timeProvider = null) : IHostedService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await gate.IsShadowEnabledAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var document = await source.ReadRawDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                // No document is not a discrepancy - there is nothing to be faithful to. Recording it as
                // a clean diff would be the vacuous-instrument failure: a sweep reporting zero over an
                // empty input looks identical to one that verified something.
                logger.LogInformation(
                    "Config shadow migration skipped: no configuration document was available to compare.");
                sink.RecordFailure("no configuration document available");
                return;
            }

            var reconstructed = await roundTrip
                .MigrateAndReadBackAsync(document, cancellationToken)
                .ConfigureAwait(false);

            var report = ConfigShadowDiff.Compare(document, reconstructed, _timeProvider);
            sink.Record(report);

            if (report.IsClean)
            {
                logger.LogInformation("Config shadow migration {Summary}.", report.Summary);
                return;
            }

            // Warning, not Error: a non-empty diff is the harness working correctly and telling us the
            // migration is not yet faithful. It is expected output during development of #2646.
            logger.LogWarning(
                "Config shadow migration {Summary}. First differences: {Differences}",
                report.Summary,
                string.Join("; ", report.Differences.Take(10).Select(Describe)));
        }
        catch (Exception ex)
        {
            // Deliberately catches everything, including OperationCanceledException. See the type-level
            // remarks: with BackgroundServiceExceptionBehavior=StopHost (#2731), letting anything escape
            // here would turn a diagnostic into a gateway outage.
            logger.LogError(
                ex,
                "Config shadow migration failed. Startup continues and configuration is unaffected; " +
                "the gateway serves JSON-sourced configuration exactly as it would with the shadow " +
                "feature disabled.");
            sink.RecordFailure(ex.Message);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string Describe(ConfigDiffEntry entry) => entry.Kind switch
    {
        ConfigDiffKind.MissingFromStore => $"{entry.Path}: missing from store",
        ConfigDiffKind.ExtraInStore => $"{entry.Path}: extra in store",
        _ => $"{entry.Path}: {entry.Source.State}({entry.Source.Value ?? "-"}) vs {entry.Store.State}({entry.Store.Value ?? "-"})",
    };
}

/// <summary>Supplies the raw configuration document to compare against.</summary>
public interface IConfigShadowSource
{
    /// <summary>
    /// Reads the configuration document as raw JSON.
    ///
    /// <para>
    /// Must return the <em>raw document</em> rather than a round-tripped <see cref="PlatformConfig"/>:
    /// binding collapses "absent" and "explicitly null" into an identical null field, which is exactly
    /// the distinction the diff exists to police.
    /// </para>
    /// </summary>
    Task<JsonObject?> ReadRawDocumentAsync(CancellationToken cancellationToken);
}

/// <summary>Evaluates the shadow feature flag. Abstracted so the policy is testable without a feature manager.</summary>
public interface IConfigShadowGate
{
    /// <summary>Whether <see cref="ConfigStoreFeatures.ShadowMigration"/> is enabled.</summary>
    Task<bool> IsShadowEnabledAsync(CancellationToken cancellationToken);
}
