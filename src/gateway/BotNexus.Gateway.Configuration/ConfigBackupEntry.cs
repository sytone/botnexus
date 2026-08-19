namespace BotNexus.Gateway.Configuration;

/// <summary>
/// One retained <c>config.json</c> backup, as surfaced by
/// <see cref="ConfigBackupService.List"/> (#2884).
/// </summary>
/// <param name="Id">
/// The stable identifier an operator passes to <c>botnexus config restore</c>: the backup filename
/// without its <c>.json</c> extension.
/// </param>
/// <param name="FileName">The backup filename including extension.</param>
/// <param name="FullPath">The absolute path to the backup file.</param>
/// <param name="Timestamp">
/// The moment the backup was taken, decoded from the filename, or <see langword="null"/> when the
/// filename does not carry a parseable timestamp (a hand-placed file).
/// </param>
/// <param name="Reason">The trigger reason slug recorded in the filename; empty when unparseable.</param>
/// <param name="SizeBytes">Size of the backup file in bytes.</param>
public sealed record ConfigBackupEntry(
    string Id,
    string FileName,
    string FullPath,
    DateTime? Timestamp,
    string Reason,
    long SizeBytes);

/// <summary>
/// Whether a retained backup can be loaded against the <em>current</em> config schema (#2884).
/// </summary>
/// <remarks>
/// This is deliberately a three-state verdict rather than a boolean. The upstream cautionary case
/// (OpenClaw <c>267268f646cd</c>) is a snapshot that is perfectly well-formed but was written by an
/// older build: restoring it verbatim is its own outage. Collapsing "needs migration" into either
/// "valid" or "unloadable" loses exactly the distinction an operator needs to decide whether the
/// snapshot is recoverable.
/// </remarks>
public enum ConfigBackupVerdict
{
    /// <summary>Parses and passes current-schema validation; safe to restore.</summary>
    Valid,

    /// <summary>
    /// Parses, but only validates after the migration pipeline has been applied. Restorable,
    /// because restore migrates before it validates.
    /// </summary>
    NeedsMigration,

    /// <summary>
    /// Does not parse as JSON, or fails current-schema validation even after migration. Cannot be
    /// restored - the restore path refuses it rather than writing a file the gateway cannot load.
    /// </summary>
    Unloadable,
}

/// <summary>
/// A backup paired with the verdict of validating it against the current schema.
/// </summary>
/// <param name="Backup">The backup that was inspected.</param>
/// <param name="Verdict">The load verdict.</param>
/// <param name="Errors">
/// The validation messages behind an <see cref="ConfigBackupVerdict.Unloadable"/> verdict; empty
/// otherwise. Carried so the CLI can tell an operator <em>why</em> a snapshot is unusable instead
/// of only that it is.
/// </param>
public sealed record ConfigBackupInspection(
    ConfigBackupEntry Backup,
    ConfigBackupVerdict Verdict,
    IReadOnlyList<string> Errors);

/// <summary>
/// The outcome of a restore attempt (#2884).
/// </summary>
/// <param name="Restored">
/// <see langword="true"/> only when the live config file was actually replaced. A refused restore
/// and a dry run both report <see langword="false"/>, so a caller cannot mistake either for a
/// completed restore.
/// </param>
/// <param name="DryRun">Whether this was a preview that deliberately wrote nothing.</param>
/// <param name="Verdict">The verdict the snapshot was judged with before any write was attempted.</param>
/// <param name="Errors">Why the restore was refused; empty on success.</param>
public sealed record ConfigRestoreResult(
    bool Restored,
    bool DryRun,
    ConfigBackupVerdict Verdict,
    IReadOnlyList<string> Errors);
