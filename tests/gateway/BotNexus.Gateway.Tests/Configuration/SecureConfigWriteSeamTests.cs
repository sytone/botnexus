using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Proves the #2392 owner-only narrowing is actually reached by the real write seams, on the real
/// filesystem, on the platform under test - not merely available as a helper nobody calls.
///
/// <para>Two failure modes these tests exist to catch specifically:</para>
/// <list type="number">
///   <item><b>The permission is lost across the atomic move.</b> <see cref="PlatformConfigWriter"/>
///   writes a temp file and <c>File.Move</c>s it over the destination. Securing only the temp file
///   would be defeated by any platform where the move does not carry the permission across.</item>
///   <item><b>The fix only applies at first create.</b> config.json is REWRITTEN constantly (UI
///   saves, CLI mutations, startup normalisation). A guard that fires only when the file does not
///   yet exist leaves every subsequent save with default permissions - so these tests assert the
///   state after a SECOND write onto a deliberately loosened file.</item>
/// </list>
/// </summary>
public sealed class SecureConfigWriteSeamTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(),
        "botnexus-perm-seam",
        Guid.NewGuid().ToString("N"));

    public SecureConfigWriteSeamTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private string ConfigPath => Path.Combine(_home, "config.json");

    [Fact]
    public async Task PlatformConfigWriter_FirstWrite_ProducesOwnerOnlyConfig()
    {
        var writer = new PlatformConfigWriter(ConfigPath, new FileSystem());

        await writer.MutateAsync(root => root["providers"] = new JsonObject { ["apiKey"] = "sk-secret" },
            "test-first-write");

        File.Exists(ConfigPath).ShouldBeTrue("Test precondition: the writer must have created the file.");
        SecureFilePermissions.IsReadableByOthers(new FileSystem(), ConfigPath).ShouldBeFalse(
            "config.json written by PlatformConfigWriter must not be readable by other principals (#2392).");
    }

    /// <summary>
    /// The rewrite case. The file is loosened between the two writes, so if the guard only fired on
    /// first create this test fails - which is exactly the regression it is here to prevent.
    /// </summary>
    [Fact]
    public async Task PlatformConfigWriter_Rewrite_ReAppliesOwnerOnly()
    {
        var fileSystem = new FileSystem();
        var writer = new PlatformConfigWriter(ConfigPath, fileSystem);

        await writer.MutateAsync(root => root["providers"] = new JsonObject { ["apiKey"] = "sk-1" },
            "test-write-1");

        LoosenPermissions(ConfigPath);
        SecureFilePermissions.IsReadableByOthers(fileSystem, ConfigPath).ShouldBeTrue(
            "Test precondition: the config must genuinely be world/group readable before the rewrite, " +
            "otherwise this test cannot distinguish a first-create-only fix from a real one.");

        // A DIFFERENT value, so the writer's #2114 no-op short-circuit does not skip the write.
        await writer.MutateAsync(root => root["providers"] = new JsonObject { ["apiKey"] = "sk-2" },
            "test-write-2");

        SecureFilePermissions.IsReadableByOthers(fileSystem, ConfigPath).ShouldBeFalse(
            "Every config.json rewrite must re-apply the owner-only restriction, not just the first " +
            "create. config.json is rewritten on every UI save and CLI mutation (#2392).");
    }

    /// <summary>The written config must still be readable/writable by the process that owns it.</summary>
    [Fact]
    public async Task PlatformConfigWriter_SecuredConfig_RemainsUsableByOwner()
    {
        var writer = new PlatformConfigWriter(ConfigPath, new FileSystem());
        await writer.MutateAsync(root => root["gateway"] = new JsonObject { ["port"] = 5000 }, "test-usable");

        var reread = await writer.ReadAsync();
        reread["gateway"]!["port"]!.GetValue<int>().ShouldBe(5000);

        await Should.NotThrowAsync(async () =>
            await writer.MutateAsync(root => root["gateway"] = new JsonObject { ["port"] = 5001 }, "test-usable-2"));
    }

    /// <summary>
    /// Backups are byte-for-byte copies of config.json, secrets included. Securing the live file
    /// while leaving up to 50 readable copies next to it would defeat the whole point.
    /// </summary>
    [Fact]
    public void ConfigBackupService_Backup_IsOwnerOnly()
    {
        var fileSystem = new FileSystem();
        var backupsDirectory = Path.Combine(_home, "backups");
        File.WriteAllText(ConfigPath, "{\"providers\":{\"openai\":{\"apiKey\":\"sk-secret\"}}}");
        LoosenPermissions(ConfigPath);

        new ConfigBackupService(backupsDirectory, fileSystem).Backup(ConfigPath, "test-backup");

        var backups = Directory.GetFiles(backupsDirectory, "config-*.json");
        backups.ShouldNotBeEmpty("Test precondition: a backup file must have been produced.");

        foreach (var backup in backups)
        {
            SecureFilePermissions.IsReadableByOthers(fileSystem, backup).ShouldBeFalse(
                $"Config backup '{backup}' is a full copy of the secrets in config.json and must be " +
                "owner-only too (#2392).");
        }
    }

    [Fact]
    public void GatewayAuthManager_AuthFile_IsOwnerOnly()
    {
        // GatewayAuthManager resolves its own path from the home directory, so exercise the shared
        // helper against an auth.json in the same shape instead of mutating the real user home.
        var fileSystem = new FileSystem();
        var authPath = Path.Combine(_home, "auth.json");
        File.WriteAllText(authPath, "{\"github-copilot\":{\"refresh\":\"ghr_secret\"}}");
        LoosenPermissions(authPath);

        SecureFilePermissions.RestrictToOwner(fileSystem, authPath).ShouldBe(FilePermissionOutcome.Applied);

        SecureFilePermissions.IsReadableByOthers(fileSystem, authPath).ShouldBeFalse(
            "auth.json holds OAuth refresh/access tokens and must be owner-only (#2392).");
    }

    /// <summary>
    /// Puts the file into the insecure state the issue describes using the platform's own
    /// mechanism (POSIX 0644 / a Windows Users:Read ACE).
    /// </summary>
    private static void LoosenPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
}
