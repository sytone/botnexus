using System.IO.Abstractions;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The result of an attempt to restrict a secret-bearing file to its owner.
/// </summary>
public enum FilePermissionOutcome
{
    /// <summary>The restriction was genuinely applied to the file on disk.</summary>
    Applied,

    /// <summary>
    /// Nothing was applied because the target is not a physical file this process can
    /// address with the platform ACL/mode APIs (for example a virtual/mock filesystem in
    /// tests, or a path that does not exist).
    /// </summary>
    Skipped,

    /// <summary>
    /// The platform call was attempted and failed (permission denied, unsupported
    /// filesystem, path on a network share without ACL support, ...). Callers treat this
    /// as non-fatal: the write itself already succeeded and the process must keep running.
    /// </summary>
    Failed
}

/// <summary>
/// The single seam through which every secret-bearing file BotNexus writes
/// (<c>config.json</c>, <c>auth.json</c>) is narrowed to owner-only access (#2392).
///
/// <para><b>Why one helper.</b> The alternative - a <c>chmod</c> call sprinkled at each
/// write site - guarantees the next write path added will forget it. Every seam calls
/// <see cref="RestrictToOwner(IFileSystem, string)"/> instead, and
/// <c>SecretFilePermissionFenceArchitectureTests</c> fails the build if a known
/// secret-writing file stops routing through here.</para>
///
/// <para><b>Cross-platform, and genuinely applied on both.</b> POSIX and Windows are
/// different worlds and a fix that no-ops on one of them while claiming to secure files is
/// worse than none:</para>
/// <list type="bullet">
///   <item><b>Linux/macOS:</b> the mode is set to <c>0600</c> (<c>UserRead | UserWrite</c>),
///   removing the group/other read bits that a default <c>umask 022</c> leaves on.
///   <see cref="System.IO.File.SetUnixFileMode(string, UnixFileMode)"/> throws
///   <see cref="PlatformNotSupportedException"/> on Windows, so it is never called there.</item>
///   <item><b>Windows:</b> <see cref="File.SetUnixFileMode(string, UnixFileMode)"/> is NOT a
///   silent no-op - it throws - so Windows gets a real DACL instead: inheritance is
///   disabled (dropping any parent-directory grant to e.g. <c>Users</c>) and explicit
///   FullControl is granted to the file's owner, <c>SYSTEM</c> and <c>Administrators</c>.</item>
/// </list>
///
/// <para><b>Why SYSTEM and Administrators are kept on Windows.</b> Restricting to the owner
/// SID alone would lock out a gateway installed as a Windows service running as
/// <c>LocalSystem</c> from a config written by an interactive admin install - i.e. it would
/// break a legitimate reader. SYSTEM and Administrators can take ownership of any file
/// regardless, so denying them buys no security and only breaks service startup. The
/// security win here is the removal of *inherited* grants to unprivileged groups.</para>
///
/// <para><b>Never throws.</b> Securing a file is defence-in-depth; failing to secure it must
/// never take down a running gateway or fail a CLI command that otherwise succeeded. Every
/// entry point returns an outcome and swallows platform exceptions.</para>
/// </summary>
public static class SecureFilePermissions
{
    /// <summary>POSIX 0600 - owner read/write, nothing for group or other.</summary>
    public const UnixFileMode OwnerOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Restricts <paramref name="path"/> to owner-only access using the physical filesystem.
    /// Use this overload from call sites that write with <see cref="System.IO.File"/> directly.
    /// </summary>
    /// <param name="path">Absolute path to an existing file.</param>
    /// <returns>Whether the restriction was applied, skipped, or failed.</returns>
    public static FilePermissionOutcome RestrictToOwner(string path)
        => RestrictToOwner(new FileSystem(), path);

    /// <summary>
    /// Restricts <paramref name="path"/> to owner-only access.
    /// </summary>
    /// <remarks>
    /// The Windows branch needs the real Win32 ACL APIs, which <see cref="IFileSystem"/> does
    /// not model. When <paramref name="fileSystem"/> is not the physical filesystem (a
    /// <c>MockFileSystem</c> in a unit test), the Windows branch reports
    /// <see cref="FilePermissionOutcome.Skipped"/> rather than pretending to have secured
    /// anything - which is exactly why the behavioural tests for this helper run against real
    /// temp files on the real filesystem on both platforms.
    /// </remarks>
    /// <param name="fileSystem">The filesystem the file was written through.</param>
    /// <param name="path">Absolute path to an existing file.</param>
    /// <returns>Whether the restriction was applied, skipped, or failed.</returns>
    public static FilePermissionOutcome RestrictToOwner(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (string.IsNullOrWhiteSpace(path))
            return FilePermissionOutcome.Skipped;

        try
        {
            if (!fileSystem.File.Exists(path))
                return FilePermissionOutcome.Skipped;

            if (OperatingSystem.IsWindows())
            {
                // The ACL APIs only address real on-disk files. A virtual filesystem has no DACL
                // to narrow, so report Skipped instead of a misleading Applied.
                if (!File.Exists(path))
                    return FilePermissionOutcome.Skipped;

                return RestrictWindowsAcl(path);
            }

            fileSystem.File.SetUnixFileMode(path, OwnerOnlyMode);
            return FilePermissionOutcome.Applied;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or PlatformNotSupportedException
                                      or NotSupportedException
                                      or InvalidOperationException
                                      or IdentityNotMappedException)
        {
            return FilePermissionOutcome.Failed;
        }
    }

    /// <summary>
    /// Reports whether <paramref name="path"/> is readable by principals other than its owner
    /// (plus, on Windows, the always-privileged SYSTEM/Administrators). Used by the
    /// <c>botnexus doctor</c> permission check to surface an over-permissive secret file that
    /// pre-dates this guard-rail.
    /// </summary>
    /// <param name="fileSystem">The filesystem to inspect through.</param>
    /// <param name="path">Absolute path to the file to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when a non-owner principal can read the file;
    /// <see langword="false"/> when it is owner-only or cannot be determined.
    /// </returns>
    public static bool IsReadableByOthers(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (!fileSystem.File.Exists(path))
                return false;

            if (OperatingSystem.IsWindows())
            {
                if (!File.Exists(path))
                    return false;

                return HasWindowsNonOwnerGrant(path);
            }

            var mode = fileSystem.File.GetUnixFileMode(path);
            const UnixFileMode Broad =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            return (mode & Broad) != UnixFileMode.None;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or PlatformNotSupportedException
                                      or NotSupportedException
                                      or InvalidOperationException
                                      or IdentityNotMappedException)
        {
            // Undeterminable is not a finding: doctor must not cry wolf on an exotic filesystem.
            return false;
        }
    }

    /// <summary>
    /// Replaces the file's DACL with an explicit, non-inherited owner-only set. Separated out and
    /// attributed so the platform-compatibility analyser (CA1416) can see the Windows guard.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FilePermissionOutcome RestrictWindowsAcl(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();

        // Break inheritance and DISCARD the inherited rules (the second argument is
        // preserveInheritance). Copying them would keep the very parent-directory grants -
        // e.g. Users:Read on a world-readable install root - that this fix exists to remove.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);

        foreach (var identity in OwnerOnlyIdentities(security))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        info.SetAccessControl(security);
        return FilePermissionOutcome.Applied;
    }

    /// <summary>
    /// The principals that keep FullControl on Windows: the file's owner (falling back to the
    /// current process identity when the owner SID cannot be read), plus SYSTEM and the local
    /// Administrators group, which can take ownership regardless and whose removal would only
    /// break a service-hosted gateway.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<IdentityReference> OwnerOnlyIdentities(FileSecurity security)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                    ?? WindowsIdentity.GetCurrent().User;
        if (owner is not null && seen.Add(owner.Value))
            yield return owner;

        foreach (var wellKnown in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            var sid = new SecurityIdentifier(wellKnown, null);
            if (seen.Add(sid.Value))
                yield return sid;
        }
    }

    /// <summary>
    /// Returns true when the file's DACL grants read access to a principal that is neither the
    /// owner nor one of the always-privileged well-known accounts.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool HasWindowsNonOwnerGrant(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var privileged = new HashSet<string>(
            OwnerOnlyIdentities(security).Select(id => id.Value),
            StringComparer.OrdinalIgnoreCase);

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if ((rule.FileSystemRights & FileSystemRights.Read) == 0)
                continue;
            if (!privileged.Contains(rule.IdentityReference.Value))
                return true;
        }

        return false;
    }
}
