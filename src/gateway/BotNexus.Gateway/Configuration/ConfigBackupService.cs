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

    private readonly string _backupsDirectory;
    private readonly IFileSystem _fileSystem;

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

        Prune();
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
