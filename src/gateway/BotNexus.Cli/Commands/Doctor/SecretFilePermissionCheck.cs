using System.IO.Abstractions;
using BotNexus.Gateway.Configuration;
using BotNexus.Cli.Commands.Doctor.Generated;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Aggregate-suite check that flags secret-bearing files under the BotNexus home which are
/// readable by principals other than their owner (#2392).
///
/// <para>The write seams now narrow these files to owner-only on every save, but a home
/// directory created before that guard-rail keeps whatever permissions the umask (POSIX) or the
/// inherited parent ACL (Windows) gave it until something rewrites the file. This check is the
/// operator-visible half of the fix: it reports the stale files so they can be fixed, and it
/// stays strictly read-only - it never changes permissions behind the operator's back.</para>
///
/// <para>Files inspected: <c>config.json</c> (provider API keys, channel bot tokens),
/// <c>auth.json</c> (OAuth refresh/access tokens), every <c>config-*.json</c> under
/// <c>backups/</c> (full copies of the same secrets), and the SQLite configuration store
/// <c>config.db</c> together with its WAL-mode sidecars <c>config.db-wal</c> and
/// <c>config.db-shm</c> (#3414 - the store holds a full copy of every value in config.json, and the
/// sidecars hold recently written pages before they are checkpointed).</para>
/// </summary>
[DoctorCheck(Id = "secret-file-permissions", Suite = DoctorSuite.Aggregate, Order = 2)]
internal sealed class SecretFilePermissionCheck : IDoctorCheck
{
    private readonly IFileSystem _fileSystem;

    public SecretFilePermissionCheck()
        : this(new FileSystem())
    {
    }

    // Test seam: inject a filesystem so the check can be driven over a temp home.
    internal SecretFilePermissionCheck(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public string Id => "secret-file-permissions";

    public string Title => "Secret file permissions";

    public Task<DoctorCheckResult> RunAsync(DoctorCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var inspected = 0;
        var findings = new List<string>();

        foreach (var path in EnumerateSecretFiles(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspected++;
            if (SecureFilePermissions.IsReadableByOthers(_fileSystem, path))
                findings.Add($"  - {path}");
        }

        if (inspected == 0)
            return Task.FromResult(DoctorCheckResult.Healthy("no secret-bearing files found to inspect"));

        if (findings.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Healthy(
                $"{inspected} secret-bearing file(s) are owner-only"));
        }

        var details = new List<string>
        {
            "These files are readable by principals other than their owner:"
        };
        details.AddRange(findings);
        details.Add(OperatingSystem.IsWindows()
            ? "Fix: remove inherited/broad ACE grants, or re-save the file (writes now restrict it automatically)."
            : "Fix: chmod 600 on the listed files (writes now restrict them automatically).");

        return Task.FromResult(new DoctorCheckResult(
            DoctorOutcome.Warning,
            $"{findings.Count} of {inspected} secret-bearing file(s) are readable by others",
            details));
    }

    /// <summary>
    /// The SQLite configuration store and its WAL-mode sidecars, relative to the home directory
    /// (#3414). Named here rather than globbed so that dropping one from the inspected set is a
    /// visible source edit that <c>SecretFilePermissionCheckTests</c> catches.
    /// </summary>
    internal static readonly string[] ConfigStoreFileNames = ["config.db", "config.db-wal", "config.db-shm"];

    /// <summary>
    /// Yields the existing secret-bearing files for the active home. Uses
    /// <see cref="IFileSystem.Path"/> throughout so the paths are correct on both platforms
    /// (never a hand-concatenated separator).
    /// </summary>
    private IEnumerable<string> EnumerateSecretFiles(DoctorCheckContext context)
    {
        if (_fileSystem.File.Exists(context.ConfigPath))
            yield return context.ConfigPath;

        var authPath = _fileSystem.Path.Combine(context.HomePath, "auth.json");
        if (_fileSystem.File.Exists(authPath))
            yield return authPath;

        // The configuration store carries the same secrets as config.json, so an unnarrowed
        // config.db is exactly the exposure this check exists to surface (#3414).
        foreach (var storeFile in ConfigStoreFileNames)
        {
            var storePath = _fileSystem.Path.Combine(context.HomePath, storeFile);
            if (_fileSystem.File.Exists(storePath))
                yield return storePath;
        }

        var backupsDirectory = _fileSystem.Path.Combine(context.HomePath, "backups");
        if (!_fileSystem.Directory.Exists(backupsDirectory))
            yield break;

        string[] backups;
        try
        {
            backups = _fileSystem.Directory.GetFiles(backupsDirectory, "config-*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var backup in backups.OrderBy(p => p, StringComparer.Ordinal))
            yield return backup;
    }
}
