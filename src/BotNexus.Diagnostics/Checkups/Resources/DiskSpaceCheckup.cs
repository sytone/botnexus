using BotNexus.Core.Abstractions;

namespace BotNexus.Diagnostics.Checkups.Resources;

public sealed class DiskSpaceCheckup(DiagnosticsPaths paths) : IHealthCheckup
{
    private const long WarningThresholdBytes = 500L * 1024 * 1024;
    private const long FailureThresholdBytes = 100L * 1024 * 1024;
    private readonly DiagnosticsPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly Func<string, long> _freeSpaceProvider = root => new DriveInfo(root).AvailableFreeSpace;

    public DiskSpaceCheckup(
        DiagnosticsPaths paths,
        Func<string, long> freeSpaceProvider)
        : this(paths)
    {
        _freeSpaceProvider = freeSpaceProvider ?? throw new ArgumentNullException(nameof(freeSpaceProvider));
    }

    public string Name => "DiskSpace";
    public string Category => "Resources";
    public string Description => "Checks available disk space for the drive containing ~/.botnexus.";

    public Task<CheckupResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            var homePath = _paths.HomePath;
            var root = Path.GetPathRoot(homePath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Fail,
                    $"Could not resolve drive root for '{homePath}'.",
                    "Set BOTNEXUS_HOME to a valid absolute path."));
            }

            var drive = new DriveInfo(root);
            var freeBytes = _freeSpaceProvider(root);
            var freeMb = freeBytes / (1024 * 1024);

            if (freeBytes < FailureThresholdBytes)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Fail,
                    $"Low disk space: {freeMb} MB available on drive '{drive.Name}'.",
                    "Free disk space immediately or move BOTNEXUS_HOME to a larger drive."));
            }

            if (freeBytes < WarningThresholdBytes)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Warn,
                    $"Disk space is getting low: {freeMb} MB available on drive '{drive.Name}'.",
                    "Free up space soon to avoid failures writing logs, sessions, or tokens."));
            }

            return Task.FromResult(new CheckupResult(
                CheckupStatus.Pass,
                $"Disk space is healthy: {freeMb} MB available on drive '{drive.Name}'."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CheckupResult(
                CheckupStatus.Fail,
                $"Failed to check disk space: {ex.Message}",
                "Verify BOTNEXUS_HOME points to an accessible local drive."));
        }
    }
}
