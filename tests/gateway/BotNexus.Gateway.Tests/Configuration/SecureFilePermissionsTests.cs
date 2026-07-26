using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Behavioural tests for the #2392 owner-only file-permission guard-rail.
///
/// <para><b>These run against the REAL filesystem, deliberately.</b> A test that asserts a
/// <c>MockFileSystem</c> recorded a mode change proves only that the mock recorded it - and on
/// Windows the mock happily accepts a <c>SetUnixFileMode</c> that the real
/// <see cref="System.IO.File"/> API rejects with
/// <see cref="PlatformNotSupportedException"/>. So every assertion here reads the permission
/// state back off a real temp file, through the platform's own API, on the platform the test is
/// executing on. The mock is used only in the one test that pins the "virtual filesystem is
/// skipped, not silently claimed as secured" contract.</para>
/// </summary>
public sealed class SecureFilePermissionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "botnexus-perm-tests",
        Guid.NewGuid().ToString("N"));

    public SecureFilePermissionsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the suite.
        }
    }

    private string NewFile(string name = "config.json")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "{\"providers\":{\"openai\":{\"apiKey\":\"sk-secret\"}}}");
        return path;
    }

    /// <summary>
    /// The core guarantee: the restriction is genuinely applied on the platform under test, and
    /// reading the permission state back proves it. This test has no OS guard on the assertion
    /// path - both branches assert something real, so it cannot pass vacuously on either OS.
    /// </summary>
    [Fact]
    public void RestrictToOwner_AppliesRealRestriction_OnThisPlatform()
    {
        var path = NewFile();
        var fileSystem = new FileSystem();

        var outcome = SecureFilePermissions.RestrictToOwner(fileSystem, path);

        outcome.ShouldBe(
            FilePermissionOutcome.Applied,
            "The restriction must actually fire on a real file on this platform. A Skipped/Failed " +
            "outcome here means config.json is still being written with default permissions (#2392).");

        AssertOwnerOnlyOnDisk(path);
    }

    /// <summary>
    /// The pre-existing broad grant must be REMOVED, not merely joined by an owner grant. This is
    /// the assertion that distinguishes a real fix from a cosmetic one.
    /// </summary>
    [Fact]
    public void RestrictToOwner_RemovesPreExistingBroadAccess()
    {
        var path = NewFile();

        MakeBroadlyReadable(path);
        SecureFilePermissions.IsReadableByOthers(new FileSystem(), path).ShouldBeTrue(
            "Test precondition: the file must genuinely be readable by others before the fix runs, " +
            "otherwise this test proves nothing.");

        SecureFilePermissions.RestrictToOwner(new FileSystem(), path).ShouldBe(FilePermissionOutcome.Applied);

        SecureFilePermissions.IsReadableByOthers(new FileSystem(), path).ShouldBeFalse(
            "After RestrictToOwner the previously broad grant must be gone.");
        AssertOwnerOnlyOnDisk(path);
    }

    /// <summary>
    /// The owner must retain read AND write access. A "fix" that locks the running gateway out of
    /// its own config is a worse outage than the exposure it prevents.
    /// </summary>
    [Fact]
    public void RestrictToOwner_LeavesFileReadableAndWritableByOwner()
    {
        var path = NewFile();

        SecureFilePermissions.RestrictToOwner(new FileSystem(), path).ShouldBe(FilePermissionOutcome.Applied);

        Should.NotThrow(() => File.ReadAllText(path));
        Should.NotThrow(() => File.WriteAllText(path, "{}"));
        File.ReadAllText(path).ShouldBe("{}");
    }

    /// <summary>Re-applying must be idempotent - every config save runs this path.</summary>
    [Fact]
    public void RestrictToOwner_IsIdempotent()
    {
        var path = NewFile();

        SecureFilePermissions.RestrictToOwner(new FileSystem(), path).ShouldBe(FilePermissionOutcome.Applied);
        SecureFilePermissions.RestrictToOwner(new FileSystem(), path).ShouldBe(FilePermissionOutcome.Applied);

        AssertOwnerOnlyOnDisk(path);
        Should.NotThrow(() => File.ReadAllText(path));
    }

    [Fact]
    public void RestrictToOwner_MissingFile_IsSkippedAndDoesNotThrow()
    {
        var missing = Path.Combine(_root, "does-not-exist.json");

        SecureFilePermissions.RestrictToOwner(new FileSystem(), missing)
            .ShouldBe(FilePermissionOutcome.Skipped);
    }

    [Fact]
    public void RestrictToOwner_NullOrEmptyPath_IsSkippedAndDoesNotThrow()
    {
        SecureFilePermissions.RestrictToOwner(new FileSystem(), string.Empty)
            .ShouldBe(FilePermissionOutcome.Skipped);
        SecureFilePermissions.RestrictToOwner(new FileSystem(), "   ")
            .ShouldBe(FilePermissionOutcome.Skipped);
    }

    /// <summary>
    /// A virtual filesystem has no real DACL on Windows, so the helper must report Skipped there
    /// rather than claim to have secured a file it cannot address. On POSIX the mock does model
    /// the mode, so Applied is correct and the mode is asserted.
    /// </summary>
    [Fact]
    public void RestrictToOwner_VirtualFileSystem_NeverFalselyClaimsApplied()
    {
        var mock = new MockFileSystem();
        var path = mock.Path.Combine(mock.Path.GetTempPath(), "config.json");
        mock.AddFile(path, new MockFileData("{}"));

        var outcome = SecureFilePermissions.RestrictToOwner(mock, path);

        if (OperatingSystem.IsWindows())
        {
            outcome.ShouldBe(
                FilePermissionOutcome.Skipped,
                "On Windows the helper needs the Win32 ACL APIs, which a virtual filesystem has no " +
                "backing for. Reporting Applied there would be a lie that hides an unsecured file.");
        }
        else
        {
            outcome.ShouldBe(FilePermissionOutcome.Applied);
            mock.File.GetUnixFileMode(path).ShouldBe(SecureFilePermissions.OwnerOnlyMode);
        }
    }

    [Fact]
    public void IsReadableByOthers_OwnerOnlyFile_ReportsFalse()
    {
        var path = NewFile();
        SecureFilePermissions.RestrictToOwner(new FileSystem(), path).ShouldBe(FilePermissionOutcome.Applied);

        SecureFilePermissions.IsReadableByOthers(new FileSystem(), path).ShouldBeFalse();
    }

    [Fact]
    public void IsReadableByOthers_BroadFile_ReportsTrue()
    {
        var path = NewFile();
        MakeBroadlyReadable(path);

        SecureFilePermissions.IsReadableByOthers(new FileSystem(), path).ShouldBeTrue();
    }

    [Fact]
    public void IsReadableByOthers_MissingFile_ReportsFalse()
    {
        SecureFilePermissions
            .IsReadableByOthers(new FileSystem(), Path.Combine(_root, "nope.json"))
            .ShouldBeFalse();
    }

    /// <summary>
    /// Reads the permission state back through the platform's own API and asserts owner-only.
    /// Both branches assert; neither is a no-op, so this cannot pass vacuously.
    /// </summary>
    private static void AssertOwnerOnlyOnDisk(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            AssertWindowsOwnerOnly(path);
            return;
        }

        var mode = File.GetUnixFileMode(path);
        mode.ShouldBe(
            SecureFilePermissions.OwnerOnlyMode,
            $"Expected mode 0600 on '{path}' but found {mode}. Group/other bits mean the secrets " +
            "in this file are still readable by other local accounts (#2392).");
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsOwnerOnly(string path)
    {
        var security = new FileInfo(path).GetAccessControl();

        security.AreAccessRulesProtected.ShouldBeTrue(
            "Inheritance must be broken, otherwise a broad grant on the parent directory (e.g. " +
            "Users:Read on the install root) still applies to the secret file.");

        var allowed = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow
                        && (r.FileSystemRights & FileSystemRights.Read) != 0)
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        allowed.ShouldNotBeEmpty("The owner must still be able to read the file.");

        var permitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        };
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                    ?? WindowsIdentity.GetCurrent().User;
        if (owner is not null)
            permitted.Add(owner.Value);

        foreach (var sid in allowed)
        {
            permitted.ShouldContain(
                sid,
                $"SID '{sid}' can still read '{path}'. Only the owner plus the always-privileged " +
                "SYSTEM/Administrators accounts may retain read access (#2392).");
        }
    }

    /// <summary>
    /// Puts the file into the exact insecure state #2392 describes, using the platform's own
    /// mechanism, so the fix is proven to actually remove real broad access rather than to
    /// tidy up a state that was already safe.
    /// </summary>
    private static void MakeBroadlyReadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            MakeWindowsBroadlyReadable(path);
            return;
        }

        // 0644 - exactly what a default umask 022 leaves behind.
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    [SupportedOSPlatform("windows")]
    private static void MakeWindowsBroadlyReadable(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
