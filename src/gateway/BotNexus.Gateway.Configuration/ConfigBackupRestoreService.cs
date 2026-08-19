using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The validated restore path for <c>config.json</c> backups (#2884).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConfigBackupService"/> has always been write-only: it produced up to
/// <see cref="ConfigBackupService.MaxBackups"/> artefacts and offered no supported way back. The
/// unsupported way back - copying a file out of the backups directory by hand - is actively
/// dangerous for two reasons this service exists to close:
/// </para>
/// <list type="number">
///   <item><b>No schema check.</b> A snapshot written by an older build may only load after the
///   legacy-key migration runs. Copied verbatim it can leave the gateway unable to start, which is
///   the failure mode the upstream cautionary commit (OpenClaw <c>267268f646cd</c>) records. So the
///   snapshot is migrated and validated <em>before</em> anything is written, and a snapshot that
///   still fails validation is refused rather than written and discovered at next startup.</item>
///   <item><b>No secret reconciliation.</b> The config UI serves redacted secrets, so a snapshot
///   can legitimately contain <c>***</c> placeholders. Writing those over live secrets destroys
///   them. The restore therefore goes through <see cref="PlatformConfigWriter"/>, whose pipeline
///   applies <see cref="ConfigSecretMerge.RestoreSecrets"/> against the document on disk, so a
///   placeholder in the snapshot resolves back to the live value instead of overwriting it.</item>
/// </list>
/// <para>
/// Routing through the writer rather than doing a file copy also buys the rest of the write
/// contract for free and by construction: the pre-restore document is backed up (so a restore is
/// itself undoable), the write is atomic, the cross-process lock is held, and the
/// destructive-section guard (#2816) still applies.
/// </para>
/// <para>
/// <b>Dry-run is the default.</b> Restore is the one config operation whose whole purpose is to
/// discard the current document, so the commit is opt-in: callers must pass
/// <c>commit: true</c>. A caller that forgets gets a preview, not a silent overwrite.
/// </para>
/// </remarks>
public sealed class ConfigBackupRestoreService
{
    /// <summary>
    /// The sections a restore is entitled to replace wholesale (#2816). A restore's declared job is
    /// to reinstate a complete previously-valid document, so it names the whole document - the same
    /// declaration <c>init --force</c> makes, and for the same reason: the operator has explicitly
    /// asked for the current document to be discarded. It is still not a bypass, because the
    /// candidate must pass full schema validation first, which <c>init --force</c>'s regenerated
    /// document also does.
    /// </summary>
    private static readonly IReadOnlyCollection<string> RestoreNamedSections = ConfigSectionGuard.EntireDocument;

    private readonly ConfigBackupService _backups;
    private readonly PlatformConfigWriter _writer;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initialises the restore service over the backup store and the writer that owns the live
    /// config file. Both are required: the writer is what makes a restore validated, atomic,
    /// backed-up and secret-safe, so there is deliberately no constructor that restores without one.
    /// </summary>
    public ConfigBackupRestoreService(
        ConfigBackupService backups,
        PlatformConfigWriter writer,
        IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _backups = backups;
        _writer = writer;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Enumerates every retained backup with its load verdict against the current schema.
    /// </summary>
    public IReadOnlyList<ConfigBackupInspection> ListWithVerdicts()
        => _backups.List().Select(Inspect).ToList();

    /// <summary>
    /// Judges a single backup against the current schema without writing anything.
    /// </summary>
    /// <remarks>
    /// Validation runs <see cref="PlatformConfigLoader.ValidateRawJson"/>, which is the same
    /// deserialize -&gt; migrate -&gt; validate pipeline the real loader uses. That matters: a
    /// verdict derived from a bespoke check could pass a document the gateway then refuses to load,
    /// which would make the verdict worse than useless. The migration is part of that pipeline, so
    /// an old-schema snapshot that migrates cleanly validates cleanly - and is reported as
    /// <see cref="ConfigBackupVerdict.NeedsMigration"/> only to tell the operator that the bytes on
    /// disk are not what will be written.
    /// </remarks>
    public ConfigBackupInspection Inspect(ConfigBackupEntry backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        string raw;
        try
        {
            raw = _fileSystem.File.ReadAllText(backup.FullPath);
        }
        catch (IOException ex)
        {
            return new ConfigBackupInspection(
                backup, ConfigBackupVerdict.Unloadable, [$"Unable to read backup: {ex.Message}"]);
        }

        JsonObject? parsed;
        try
        {
            parsed = JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException ex)
        {
            return new ConfigBackupInspection(
                backup, ConfigBackupVerdict.Unloadable, [$"Backup is not valid JSON. {ex.Message}"]);
        }

        if (parsed is null)
        {
            return new ConfigBackupInspection(
                backup,
                ConfigBackupVerdict.Unloadable,
                ["Backup is not a JSON object; a config document must have an object at its root."]);
        }

        var errors = PlatformConfigLoader.ValidateRawJson(raw);
        if (errors.Count > 0)
            return new ConfigBackupInspection(backup, ConfigBackupVerdict.Unloadable, errors);

        var verdict = UsesLegacySchema(parsed)
            ? ConfigBackupVerdict.NeedsMigration
            : ConfigBackupVerdict.Valid;

        return new ConfigBackupInspection(backup, verdict, []);
    }

    /// <summary>
    /// Restores <paramref name="backupId"/> over the live config document.
    /// </summary>
    /// <param name="backupId">
    /// The backup identifier from <see cref="ConfigBackupEntry.Id"/>. Resolved against the
    /// enumerated backups directory, so it cannot address a file outside it.
    /// </param>
    /// <param name="commit">
    /// <see langword="false"/> (the default) previews the restore and writes nothing.
    /// <see langword="true"/> performs it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The outcome. <see cref="ConfigRestoreResult.Restored"/> is <see langword="true"/> only when
    /// the live file was actually replaced.
    /// </returns>
    public async Task<ConfigRestoreResult> RestoreAsync(
        string backupId,
        bool commit = false,
        CancellationToken ct = default)
    {
        var backup = _backups.Resolve(backupId);
        if (backup is null)
        {
            return new ConfigRestoreResult(
                false, !commit, ConfigBackupVerdict.Unloadable,
                [$"No backup found with id '{backupId}'. Run 'botnexus config backups list' to see the retained backups."]);
        }

        // Judge BEFORE writing, always - including on a dry run, so the preview reports the same
        // verdict the commit would act on. A snapshot that cannot load is refused here and the live
        // file is never opened, which is the acceptance criterion this method exists for.
        var inspection = Inspect(backup);
        if (inspection.Verdict == ConfigBackupVerdict.Unloadable)
        {
            return new ConfigRestoreResult(
                false,
                !commit,
                ConfigBackupVerdict.Unloadable,
                [
                    $"Backup '{backup.Id}' cannot be restored: it does not load against the current schema.",
                    .. inspection.Errors,
                ]);
        }

        if (!commit)
            return new ConfigRestoreResult(false, true, inspection.Verdict, []);

        var raw = _fileSystem.File.ReadAllText(backup.FullPath);
        var snapshot = JsonNode.Parse(raw)!.AsObject();

        // MutateValidatedAsync gives the whole contract in one call: the writer lock and the
        // cross-process lock are held across read-mutate-validate-write, the pre-restore document
        // is backed up by WriteRootAsync before the swap, the candidate is validated again inside
        // the lock, and a rejected candidate leaves the file byte-for-byte unchanged.
        var errors = await _writer.MutateValidatedAsync(
            root =>
            {
                var candidate = snapshot.DeepClone().AsObject();

                // #1955 / AC3: the snapshot may carry redaction placeholders. Resolve them against
                // the LIVE document before the swap, so a restore can never write "***" over a real
                // secret. This is the same call UpdateSectionAsync and ApplyPatchAsync make, walking
                // the same reflection-discovered secret paths.
                ConfigSecretMerge.RestoreSecrets(root, candidate);

                root.Clear();
                foreach (var kvp in candidate)
                    root[kvp.Key] = kvp.Value?.DeepClone();

                return null;
            },
            $"before-restore-{backup.Id}",
            ct,
            RestoreNamedSections);

        return errors.Count > 0
            ? new ConfigRestoreResult(false, false, inspection.Verdict, errors)
            : new ConfigRestoreResult(true, false, inspection.Verdict, []);
    }

    /// <summary>
    /// Whether the document carries any legacy top-level setting the migration lifts into
    /// <c>gateway</c>, which is what makes it an old-schema snapshot.
    /// </summary>
    private static bool UsesLegacySchema(JsonObject root)
        => PlatformConfigLoader.LegacyRootSettingKeys.Any(root.ContainsKey);
}
