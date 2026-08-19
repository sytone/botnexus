using System.IO.Abstractions;
using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Creates timestamped backups of config.json before writes,
/// retaining at most <see cref="MaxBackups"/> copies. Callers pass a
/// short reason slug that appears in the filename so operators can
/// identify what triggered each backup without opening the file.
/// </summary>
public sealed class ConfigBackupService
{
    /// <summary>Maximum number of backup files to retain in the backups directory.</summary>
    public const int MaxBackups = 50;

    private static readonly Regex UnsafeChars = new(@"[^a-zA-Z0-9\-]", RegexOptions.Compiled);

    // config-{yyyyMMdd}-{HHmmss}-{reason}.json. The reason slug is already sanitised to
    // [a-zA-Z0-9-] on the write path, so the trailing group can be greedy without ambiguity.
    private static readonly Regex BackupName = new(
        @"^config-(?<date>\d{8})-(?<time>\d{6})-(?<reason>.+)\.json$", RegexOptions.Compiled);

    private readonly string _backupsDirectory;
    private readonly IFileSystem _fileSystem;

    /// <summary>The directory retained backups are written to and enumerated from.</summary>
    public string BackupsDirectory => _backupsDirectory;

    /// <summary>
    /// Initialises the service. The backups directory is created on first
    /// <see cref="Backup"/> call if it does not yet exist.
    /// </summary>
    public ConfigBackupService(string backupsDirectory, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _backupsDirectory = backupsDirectory;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Copies <paramref name="configPath"/> into the backups directory as
    /// <c>config-{timestamp}-{reason}.json</c>.
    /// No-op if the config file does not exist yet.
    /// Prunes oldest backups when the count would exceed <see cref="MaxBackups"/>.
    /// </summary>
    public void Backup(string configPath, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!_fileSystem.File.Exists(configPath))
            return;

        _fileSystem.Directory.CreateDirectory(_backupsDirectory);

        var safeReason = UnsafeChars.Replace(reason, "-");
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupName = $"config-{timestamp}-{safeReason}.json";
        var backupPath = _fileSystem.Path.Combine(_backupsDirectory, backupName);

        _fileSystem.File.Copy(configPath, backupPath, overwrite: true);
        // #2392: a backup is a byte-for-byte copy of config.json, secrets included, so it needs
        // the same owner-only narrowing as the live file. File.Copy does NOT carry the source
        // permissions across on POSIX (the new file gets the process umask), so securing only
        // config.json would leave up to MaxBackups readable copies of the same secrets behind.
        SecureFilePermissions.RestrictToOwner(_fileSystem, backupPath);

        Prune();
    }

    /// <summary>
    /// Enumerates the retained backups, newest first, decoding the timestamp and trigger reason
    /// back out of each filename (#2884).
    /// </summary>
    /// <remarks>
    /// The filename is the only place the trigger reason is recorded, so it is also the only place
    /// it can be read back from - there is no sidecar index, deliberately: an index would be a
    /// second source of truth that could disagree with the directory after a manual copy or
    /// deletion, and operators do manipulate this directory by hand. A file whose name does not
    /// match the emitted shape is therefore surfaced with a null timestamp rather than hidden,
    /// because a hand-copied snapshot is exactly the artefact an operator most needs to see listed.
    /// </remarks>
    public IReadOnlyList<ConfigBackupEntry> List()
    {
        if (!_fileSystem.Directory.Exists(_backupsDirectory))
            return [];

        var entries = new List<ConfigBackupEntry>();
        foreach (var path in _fileSystem.Directory.GetFiles(_backupsDirectory, "config-*.json"))
        {
            var fileName = _fileSystem.Path.GetFileName(path);
            var match = BackupName.Match(fileName);

            DateTime? timestamp = null;
            var reason = string.Empty;
            if (match.Success)
            {
                reason = match.Groups["reason"].Value;
                if (DateTime.TryParseExact(
                        $"{match.Groups["date"].Value}-{match.Groups["time"].Value}",
                        "yyyyMMdd-HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var parsed))
                {
                    timestamp = parsed;
                }
            }

            var size = _fileSystem.FileInfo.New(path).Length;
            entries.Add(new ConfigBackupEntry(
                Id: _fileSystem.Path.GetFileNameWithoutExtension(fileName),
                FileName: fileName,
                FullPath: path,
                Timestamp: timestamp,
                Reason: reason,
                SizeBytes: size));
        }

        // Newest first. Files with an unparseable name sort last rather than being dropped.
        return entries
            .OrderByDescending(e => e.Timestamp ?? DateTime.MinValue)
            .ThenByDescending(e => e.FileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Resolves a caller-supplied backup identifier to a retained backup, or <see langword="null"/>
    /// when no such backup exists. Accepts the id with or without the <c>.json</c> suffix.
    /// </summary>
    /// <remarks>
    /// Resolution is deliberately performed by matching against the <em>enumerated</em> directory
    /// contents rather than by combining the identifier onto the backups path. An identifier is
    /// operator input arriving from the command line; combining it directly would make
    /// <c>../../config.json</c> a readable "backup" and turn restore into an arbitrary-file read
    /// against the live config path. Matching against the listing cannot leave the directory.
    /// </remarks>
    public ConfigBackupEntry? Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var normalised = id.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? id[..^5]
            : id;

        return List().FirstOrDefault(e => string.Equals(e.Id, normalised, StringComparison.OrdinalIgnoreCase));
    }

    // Deletes oldest backup files when the directory exceeds MaxBackups.
    private void Prune()
    {
        var files = _fileSystem.Directory
            .GetFiles(_backupsDirectory, "config-*.json")
            .OrderBy(f => _fileSystem.File.GetCreationTimeUtc(f))
            .ToList();

        var excess = files.Count - MaxBackups;
        for (var i = 0; i < excess; i++)
            _fileSystem.File.Delete(files[i]);
    }
}
