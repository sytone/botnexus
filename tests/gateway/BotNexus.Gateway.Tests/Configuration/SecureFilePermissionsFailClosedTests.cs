using System.IO.Abstractions;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class SecureFilePermissionsFailClosedTests
{
    private const string Owner = "S-1-5-21-1-2-3-1001";
    private const string Other = "S-1-5-21-1-2-3-1002";

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0002)]
    [InlineData(0x0004)]
    [InlineData(0x0100)]
    [InlineData(0x0010)]
    [InlineData(0x10000)]
    [InlineData(0x40000)]
    [InlineData(0x80000)]
    [InlineData(0x0020)]
    [InlineData(0x1F01FF)]
    [InlineData(unchecked((int)0x80000000))] // GENERIC_READ
    [InlineData(0x40000000)] // GENERIC_WRITE
    [InlineData(0x10000000)] // GENERIC_ALL
    public void WindowsSecretAcl_NonOwnerContentOrControlGrant_IsUnsafe(int rights)
    {
        SecureFilePermissions.HasUnsafeWindowsSecretAcl(Owner, true,
            [(Other, rights, true, false)]).ShouldBeTrue();
    }

    [Fact]
    public void WindowsSecretAcl_NullDaclOrUnknownOwner_IsUnsafe()
    {
        SecureFilePermissions.HasUnsafeWindowsSecretAcl(Owner, false, []).ShouldBeTrue();
        SecureFilePermissions.HasUnsafeWindowsSecretAcl(null, true, []).ShouldBeTrue();
        SecureFilePermissions.HasUnsafeWindowsSecretAcl(string.Empty, true, []).ShouldBeTrue();
        SecureFilePermissions.HasUnsafeWindowsSecretAcl(Owner, true, []).ShouldBeFalse(
            "An empty DACL denies access; it is not an unrestricted/null DACL.");
    }

    [Theory]
    [InlineData(Owner)]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    public void WindowsSecretAcl_OwnerSystemAndAdministrators_ArePermitted(string principal)
    {
        SecureFilePermissions.HasUnsafeWindowsSecretAcl(Owner, true,
            [(principal, 0x1F01FF, true, false)]).ShouldBeFalse();
    }

    [Fact]
    public void WindowsSecretAcl_DenyInheritOnlyAndSynchronize_DoNotGrantContentAccess()
    {
        SecureFilePermissions.HasUnsafeWindowsSecretAcl(Owner, true,
            [(Other, 0x1F01FF, false, false),
             (Other, 0x1F01FF, true, true),
             (Other, 0x100000, true, false)]).ShouldBeFalse();
    }

    [Fact]
    public void WindowsSecretAcl_DenyDoesNotCancelUnsafeAllow_ConservativelyRejects()
    {
        SecureFilePermissions.HasUnsafeWindowsSecretAcl(Owner, true,
            [(Other, 0x0002, false, false),
             (Other, 0x0002, true, false)]).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0x0002)]
    [InlineData(0x40000)]
    [InlineData(0x80000)]
    public void IsReadableByOthers_WindowsModificationGrant_OnlyFailClosedRejects(int rights)
    {
        if (!OperatingSystem.IsWindows())
            return;

        WithWindowsFile(path =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null), (FileSystemRights)rights, AccessControlType.Allow));
            info.SetAccessControl(security);

            SecureFilePermissions.IsReadableByOthers(new FileSystem(), path).ShouldBeFalse();
            SecureFilePermissions.IsReadableByOthers(new FileSystem(), path, failClosed: true).ShouldBeTrue();
        });
    }

    [Fact]
    public void IsReadableByOthers_WindowsNullDacl_FailClosedRejects()
    {
        if (!OperatingSystem.IsWindows())
            return;

        WithWindowsFile(path =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            descriptor.DiscretionaryAcl = null;
            descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclPresent);
            var binary = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(binary, 0);
            security.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Access);
            info.SetAccessControl(security);

            // Windows/.NET may materialize a null DACL as Everyone:FullControl. Both are unsafe.
            SecureFilePermissions.IsReadableByOthers(new FileSystem(), path, failClosed: true).ShouldBeTrue();
        });
    }

    [Fact]
    public void IsReadableByOthers_RestrictedRealFile_IsSafeOnThisPlatform()
    {
        var path = Path.GetTempFileName();
        try
        {
            SecureFilePermissions.RestrictToOwner(path).ShouldBe(FilePermissionOutcome.Applied);
            SecureFilePermissions.IsReadableByOthers(new FileSystem(), path, failClosed: true).ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WithWindowsFile(Action<string> assertion)
    {
        var path = Path.GetTempFileName();
        var original = new FileInfo(path).GetAccessControl();
        try
        {
            SecureFilePermissions.RestrictToOwner(path).ShouldBe(FilePermissionOutcome.Applied);
            assertion(path);
        }
        finally
        {
            new FileInfo(path).SetAccessControl(original);
            File.Delete(path);
        }
    }
}
