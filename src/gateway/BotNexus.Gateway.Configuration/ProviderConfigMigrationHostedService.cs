using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Applies <see cref="ProviderConfigMigration"/> to <c>config.json</c> at gateway startup, so an
/// existing configuration is converted to the per-capability shape automatically rather than
/// depending on the operator hand-editing it (Jon's review on PR #3277).
///
/// <para><b>Backup before rewrite, always.</b> Unlike the heartbeat normalisation this is modelled on,
/// this transform MOVES existing operator-authored values rather than injecting a default block. The
/// blast radius of a bug is therefore "the operator's provider settings", not "an extra key", so the
/// pre-migration document is copied to <c>config.json.pre-2854.bak</c> first. Rollback is a file copy,
/// which is the only rollback story anyone can execute correctly at 3am.</para>
///
/// <para><b>Never fails startup.</b> Same reasoning as
/// <see cref="Shadow.ConfigShadowMigrationHostedService"/>: <c>BackgroundServiceExceptionBehavior</c>
/// is <c>StopHost</c> (#2731), so an exception escaping here would take down cron, portal, SignalR and
/// every agent surface. A failed migration is survivable because the flat fields are still fully
/// honoured by <c>ProviderConfig.Effective*</c> — the gateway simply keeps running on the legacy shape.
/// A migration that bricks the gateway is strictly worse than one that did not run.</para>
/// </summary>
public sealed class ProviderConfigMigrationHostedService(
    IFileSystem fileSystem,
    ILogger<ProviderConfigMigrationHostedService> logger) : IHostedService
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Suffix of the pre-migration backup written beside <c>config.json</c>.</summary>
    public const string BackupSuffix = ".pre-2854.bak";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.GetDefaultConfigPath(fileSystem);
        await TryMigrateAsync(configPath, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Reads, migrates and atomically rewrites the document, or leaves it untouched.</summary>
    public async Task TryMigrateAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!fileSystem.File.Exists(configPath))
            return;

        try
        {
            var rawJson = await fileSystem.File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(rawJson, nodeOptions: NodeOptions) is not JsonObject root)
                return;

            var migrated = ProviderConfigMigration.Migrate(root);
            if (migrated.Count == 0)
            {
                // The steady state on every start after the first. Deliberately silent: logging
                // "nothing to migrate" on every boot forever trains operators to ignore the category.
                return;
            }

            // Backup the ORIGINAL text, not a re-serialisation of the parsed document, so the rollback
            // artefact is byte-for-byte what the operator had — including any formatting or comments
            // a round-trip through JsonNode would quietly normalise away.
            var backupPath = configPath + BackupSuffix;
            await fileSystem.File.WriteAllTextAsync(backupPath, rawJson, cancellationToken).ConfigureAwait(false);
            SecureFilePermissions.RestrictToOwner(fileSystem, backupPath);

            await WriteAtomicallyAsync(configPath, root.ToJsonString(WriteOptions), cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Migrated {Count} provider entries ({Providers}) in '{ConfigPath}' to the per-capability " +
                "configuration shape (#2854). A pre-migration backup was written to '{BackupPath}'.",
                migrated.Count,
                string.Join(", ", migrated),
                configPath,
                backupPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // See the type-level remarks: the legacy shape still resolves correctly, so continuing on
            // the un-migrated document is a genuinely safe outcome. Failing startup is not.
            logger.LogWarning(
                ex,
                "Failed to migrate provider configuration in '{ConfigPath}' to the per-capability shape. " +
                "Startup continues on the existing configuration, which remains fully supported.",
                configPath);
        }
    }

    /// <summary>Temp-file + atomic move, matching the other config rewrite seams (#2392).</summary>
    private async Task WriteAtomicallyAsync(string configPath, string contents, CancellationToken cancellationToken)
    {
        var dirName = fileSystem.Path.GetDirectoryName(configPath) ?? string.Empty;
        var tempPath = fileSystem.Path.Combine(
            dirName,
            fileSystem.Path.GetFileName(configPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await fileSystem.File.WriteAllTextAsync(tempPath, contents, cancellationToken).ConfigureAwait(false);
            SecureFilePermissions.RestrictToOwner(fileSystem, tempPath);
            fileSystem.File.Move(tempPath, configPath, overwrite: true);
            SecureFilePermissions.RestrictToOwner(fileSystem, configPath);
        }
        catch
        {
            if (fileSystem.File.Exists(tempPath))
                fileSystem.File.Delete(tempPath);
            throw;
        }
    }
}
