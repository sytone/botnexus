using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Shadow;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.FeatureManagement;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration.Shadow;

/// <summary>
/// Reads the configuration document from disk for the shadow comparison (#2646 PBI 2).
///
/// <para>
/// <b>Reads the raw file, never a bound <see cref="PlatformConfig"/>.</b> Binding collapses "absent"
/// and "explicitly null" into an identical <c>null</c> field, which is exactly the distinction the
/// shadow diff exists to police - a source built on bound objects would report clean against a store
/// that had already lost it. It also deliberately does not route through
/// <see cref="PlatformConfigLoader"/>: the loader applies defaults, hydration and normalisation, and
/// the question being answered is whether the store reproduces <em>the file</em>, not whether it
/// reproduces the loader's interpretation of the file.
/// </para>
///
/// <para>
/// Strictly read-only. This type never opens the file for writing and never references
/// <see cref="PlatformConfigWriter"/>, which is what makes rollback "delete the store file" rather
/// than a restore procedure.
/// </para>
/// </summary>
public sealed class FileConfigShadowSource(IFileSystem fileSystem, string? configPath = null) : IConfigShadowSource
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <inheritdoc />
    public async Task<JsonObject?> ReadRawDocumentAsync(CancellationToken cancellationToken)
    {
        var path = configPath ?? PlatformConfigLoader.GetDefaultConfigPath(_fileSystem);
        if (!_fileSystem.File.Exists(path))
        {
            // Not an error and not a clean comparison: there is simply nothing to be faithful to.
            // The hosted service records this as a failure rather than as a zero-difference report,
            // because a sweep reporting zero over an empty input is indistinguishable from a broken one.
            return null;
        }

        var raw = await _fileSystem.File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw) as JsonObject;
    }
}

/// <summary>
/// Evaluates <see cref="ConfigStoreFeatures.ShadowMigration"/> via <see cref="IFeatureManager"/>.
///
/// <para>
/// <b>Fails closed.</b> If the flag cannot be evaluated the shadow pass is treated as disabled, which
/// leaves the platform in its default state rather than running an unrequested migration on the
/// strength of a failed lookup. Mirrors the existing precedent in <c>ApiKeyGatewayAuthHandler</c>,
/// which treats a failed flag evaluation as "enforcement off" for the same reason.
/// </para>
/// </summary>
public sealed class FeatureManagerConfigShadowGate(
    IFeatureManager featureManager,
    ILogger<FeatureManagerConfigShadowGate> logger) : IConfigShadowGate
{
    /// <inheritdoc />
    public async Task<bool> IsShadowEnabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await featureManager
                .IsEnabledAsync(ConfigStoreFeatures.ShadowMigration)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to evaluate feature flag '{Feature}'; treating the configuration shadow " +
                "migration as disabled.",
                ConfigStoreFeatures.ShadowMigration);
            return false;
        }
    }
}

/// <summary>
/// Evaluates <see cref="ConfigStoreFeatures.Authoritative"/> via <see cref="IFeatureManager"/>.
///
/// <para>
/// <b>Fails closed, and the direction matters more here than anywhere else in the rollout.</b> A failed
/// flag lookup is read as "not authoritative", which leaves <c>config.json</c> serving configuration -
/// the behaviour the platform has had for its whole life. The opposite default would let a transient
/// feature-management fault silently promote an unverified store into the configuration read path,
/// which is the single outcome the two-flag split exists to prevent.
/// </para>
///
/// <para>
/// Separate from <see cref="FeatureManagerConfigShadowGate"/> rather than one gate returning both
/// answers: the flags are independent by design, and a shared evaluation path would let one flag's
/// failure change the other's answer.
/// </para>
/// </summary>
public sealed class FeatureManagerConfigStoreAuthoritativeGate(
    IFeatureManager featureManager,
    ILogger<FeatureManagerConfigStoreAuthoritativeGate> logger) : IConfigStoreAuthoritativeGate
{
    /// <inheritdoc />
    public async Task<bool> IsAuthoritativeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await featureManager
                .IsEnabledAsync(ConfigStoreFeatures.Authoritative)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to evaluate feature flag '{Feature}'; treating the configuration store as NOT " +
                "authoritative so the configuration file continues to serve reads.",
                ConfigStoreFeatures.Authoritative);
            return false;
        }
    }
}

/// <summary>
/// Implements the round-trip seam by writing the document into the store and reading it back
/// (#2646 PBI 2).
/// <para>
/// <b>Reads back as entries, then rebuilds nothing.</b> The store's natural read shape is
/// <see cref="ConfigEntry"/> - the only shape able to express <see cref="ConfigValueState.Unset"/> -
/// so this adapter hands those entries straight to the diff rather than reconstructing a
/// <see cref="JsonObject"/>. Reconstructing one would force every state through a format that cannot
/// represent "present and unset", silently discarding the distinction under test.
/// </para>
/// </summary>
public sealed class ConfigStoreRoundTrip(IConfigStore store) : IConfigStoreEntryRoundTrip
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ConfigEntry>> MigrateAndReadBackEntriesAsync(
        JsonObject source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await store.WriteDocumentAsync(source, cancellationToken).ConfigureAwait(false);
        return await store.ReadEntriesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Satisfies the document-shaped seam when only the entry-shaped one is in use.
///
/// <para>
/// The hosted service takes <see cref="IConfigStoreRoundTrip"/> as a required argument and prefers
/// <see cref="IConfigStoreEntryRoundTrip"/> whenever it is registered, so this exists purely to keep
/// the service graph resolvable. It returns <see langword="null"/> rather than echoing the source:
/// echoing would make a mis-registration - the entry seam missing - look like a perfectly clean diff,
/// which is the one outcome a verification harness must never fake.
/// </para>
/// </summary>
public sealed class NoOpConfigStoreRoundTrip : IConfigStoreRoundTrip
{
    /// <inheritdoc />
    public Task<JsonObject?> MigrateAndReadBackAsync(JsonObject source, CancellationToken cancellationToken)
        => Task.FromResult<JsonObject?>(null);
}
