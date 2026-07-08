using System.IO.Abstractions;
using System.Runtime.InteropServices;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Default <see cref="INetworkPathDetector"/> implementation. Detection is a portable
/// best-effort heuristic:
/// <list type="bullet">
///   <item>Windows UNC paths (<c>\\server\share</c>) are always network.</item>
///   <item>On Windows a mapped drive whose <see cref="DriveInfo.DriveType"/> is
///   <see cref="DriveType.Network"/> is network.</item>
///   <item>On Linux/macOS the mount backing the path is inspected via the injected
///   <see cref="IFileSystem"/>; well-known network filesystem types (nfs, cifs, smb,
///   fuse.sshfs, etc.) are treated as network. When the mount type cannot be resolved
///   the path is treated as local.</item>
/// </list>
/// The heuristic deliberately errs toward "local" so a false positive never needlessly
/// disables WAL - the effective-mode verification in <see cref="SqliteWalMaintenance"/>
/// catches the case where WAL silently fails to engage on an undetected network mount.
/// </summary>
public sealed class NetworkPathDetector(IFileSystem fileSystem) : INetworkPathDetector
{
    private static readonly string[] NetworkFsTypes =
    [
        "nfs", "nfs4", "cifs", "smb", "smbfs", "smb2", "smb3",
        "fuse.sshfs", "fuse.davfs", "9p", "afs", "ncpfs", "glusterfs",
    ];

    private readonly IFileSystem _fileSystem = fileSystem;

    /// <inheritdoc />
    public bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = _fileSystem.Path.GetFullPath(path);
        }
        catch
        {
            // A malformed path cannot be proven to be a network mount - treat as local.
            return false;
        }

        // UNC paths (\\server\share or //server/share) are unambiguously network on any OS.
        if (IsUncPath(fullPath))
        {
            return true;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? IsWindowsNetworkDrive(fullPath)
            : IsPosixNetworkMount(fullPath);
    }

    private static bool IsUncPath(string fullPath)
    {
        if (fullPath.Length < 2)
        {
            return false;
        }

        var c0 = fullPath[0];
        var c1 = fullPath[1];
        return (c0 == '\\' || c0 == '/') && (c1 == '\\' || c1 == '/');
    }

    private bool IsWindowsNetworkDrive(string fullPath)
    {
        try
        {
            var root = _fileSystem.Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var drive = _fileSystem.DriveInfo.New(root);
            return drive.DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private bool IsPosixNetworkMount(string fullPath)
    {
        try
        {
            // Find the mount whose root is the longest prefix of fullPath, then inspect its
            // reported filesystem type. IDriveInfo.DriveFormat surfaces the mount fs type on
            // Unix (e.g. "nfs4", "cifs").
            var best = _fileSystem.DriveInfo.GetDrives()
                .Where(d => !string.IsNullOrEmpty(d.Name) && fullPath.StartsWith(d.Name, StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length)
                .FirstOrDefault();

            if (best is null)
            {
                return false;
            }

            var format = best.DriveFormat;
            if (string.IsNullOrEmpty(format))
            {
                return false;
            }

            return NetworkFsTypes.Contains(format, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
