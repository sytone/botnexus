using System.IO.Abstractions;
using BotNexus.Cli.Commands.Doctor;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Doctor;

/// <summary>
/// Tests for the <c>botnexus doctor</c> secret-file permission check (#2392).
///
/// <para>These drive the check over a REAL temp home on the real filesystem, because the whole
/// point of the check is to report the actual on-disk permission state. A MockFileSystem models
/// a Unix mode even on Windows, so a mock-based test would report a state that has nothing to do
/// with what an operator's disk actually looks like.</para>
/// </summary>
public sealed class SecretFilePermissionCheckTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(),
        "botnexus-doctor-perm",
        Guid.NewGuid().ToString("N"));

    public SecretFilePermissionCheckTests() => Directory.CreateDirectory(_home);

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

    private DoctorCheckContext Context(bool verbose = false)
        => new(Path.Combine(_home, "config.json"), _home, verbose);

    private static async Task<DoctorCheckResult> RunAsync(DoctorCheckContext context)
        => await new SecretFilePermissionCheck(new FileSystem()).RunAsync(context, CancellationToken.None);

    [Fact]
    public void Check_IsRegisteredInTheAggregateSuite()
    {
        DoctorCheckRegistry.CreateDefault()
            .Select(c => c.Id)
            .ShouldContain(
                "secret-file-permissions",
                "The permission check must run as part of the bare 'botnexus doctor' suite, otherwise " +
                "an operator never learns their pre-existing config.json is world-readable (#2392).");
    }

    [Fact]
    public async Task EmptyHome_IsHealthy()
    {
        var result = await RunAsync(Context());

        result.Outcome.ShouldBe(DoctorOutcome.Healthy);
        result.Summary.ShouldContain("no secret-bearing files");
    }

    [Fact]
    public async Task OwnerOnlyFiles_AreHealthy()
    {
        WriteSecret("config.json");
        WriteSecret("auth.json");
        Secure("config.json");
        Secure("auth.json");

        var result = await RunAsync(Context());

        result.Outcome.ShouldBe(
            DoctorOutcome.Healthy,
            "Files already narrowed to their owner must not be reported as findings - a check that " +
            "cries wolf on a correct install gets ignored. Details: " + string.Join(" | ", result.Details));
        result.Summary.ShouldContain("owner-only");
    }

    [Fact]
    public async Task BroadlyReadableConfig_IsReportedAsAWarning()
    {
        WriteSecret("config.json");
        Loosen("config.json");

        var result = await RunAsync(Context());

        result.Outcome.ShouldBe(
            DoctorOutcome.Warning,
            "A config.json readable by other local accounts is exactly the #2392 finding this check exists " +
            "to surface.");
        string.Join("\n", result.Details).ShouldContain("config.json");
    }

    [Fact]
    public async Task BroadlyReadableBackup_IsReportedEvenWhenLiveConfigIsSecure()
    {
        WriteSecret("config.json");
        Secure("config.json");

        var backups = Directory.CreateDirectory(Path.Combine(_home, "backups"));
        var backupPath = Path.Combine(backups.FullName, "config-20260101-000000-test.json");
        File.WriteAllText(backupPath, "{\"providers\":{\"openai\":{\"apiKey\":\"sk-secret\"}}}");
        LoosenPath(backupPath);

        var result = await RunAsync(Context());

        result.Outcome.ShouldBe(
            DoctorOutcome.Warning,
            "A backup is a full copy of config.json's secrets; a secure live file does not make an " +
            "exposed backup safe.");
        string.Join("\n", result.Details).ShouldContain("config-20260101-000000-test.json");
    }

    [Fact]
    public async Task Check_IsReadOnly_AndDoesNotAlterPermissions()
    {
        WriteSecret("config.json");
        Loosen("config.json");

        var first = await RunAsync(Context());
        var second = await RunAsync(Context());

        first.Outcome.ShouldBe(DoctorOutcome.Warning);
        second.Outcome.ShouldBe(
            DoctorOutcome.Warning,
            "doctor is a diagnostic: it must report the finding, not silently fix it. A second run " +
            "reporting Healthy would mean the check mutated the file behind the operator's back.");
    }

    private void WriteSecret(string name)
        => File.WriteAllText(
            Path.Combine(_home, name),
            "{\"providers\":{\"openai\":{\"apiKey\":\"sk-secret\"}}}");

    private void Secure(string name)
        => BotNexus.Gateway.Configuration.SecureFilePermissions
            .RestrictToOwner(new FileSystem(), Path.Combine(_home, name));

    private void Loosen(string name) => LoosenPath(Path.Combine(_home, name));

    /// <summary>
    /// Puts the file into the insecure state #2392 describes, using each platform's own mechanism:
    /// POSIX 0644 (a default umask 022 result) or a Windows Users:Read ACE.
    /// </summary>
    private static void LoosenPath(string path)
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
