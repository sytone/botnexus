using System.IO.Abstractions.TestingHelpers;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class ConfigBackupServiceTests
{
    private static string MakeBackupsDir() =>
        Path.Combine(Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"), "backups");

    // 1. Backup creates a timestamped file in the backups directory
    [Fact]
    public void Backup_CreatesTimestampedFile_InBackupsDirectory()
    {
        var fs = new MockFileSystem();
        var backupsDir = MakeBackupsDir();
        fs.Directory.CreateDirectory(backupsDir);

        var configPath = Path.Combine(Path.GetTempPath(), "config.json");
        fs.File.WriteAllText(configPath, "{}");

        var sut = new ConfigBackupService(backupsDir, fs);
        sut.Backup(configPath, "before-agent-create-larry");

        var files = fs.Directory.GetFiles(backupsDir);
        files.Length.ShouldBe(1);

        var name = Path.GetFileName(files[0]);
        name.ShouldMatch(@"^config-\d{8}-\d{6}-before-agent-create-larry\.json$");
    }

    // 2. Backup is a no-op when config file does not exist
    [Fact]
    public void Backup_WhenConfigDoesNotExist_IsNoOp()
    {
        var fs = new MockFileSystem();
        var backupsDir = MakeBackupsDir();
        fs.Directory.CreateDirectory(backupsDir);

        var configPath = Path.Combine(Path.GetTempPath(), "nonexistent-config.json");

        var sut = new ConfigBackupService(backupsDir, fs);
        sut.Backup(configPath, "before-agent-create-larry");

        var files = fs.Directory.GetFiles(backupsDir);
        files.Length.ShouldBe(0);
    }

    // 3. Backup prunes oldest when exceeding MaxBackups
    [Fact]
    public void Backup_PrunesOldestWhenExceedsMax()
    {
        var fs = new MockFileSystem();
        var backupsDir = MakeBackupsDir();
        fs.Directory.CreateDirectory(backupsDir);

        // Seed 50 existing backups with staggered timestamps
        for (var i = 0; i < ConfigBackupService.MaxBackups; i++)
        {
            var ts = new DateTime(2026, 1, 1, 0, 0, i, DateTimeKind.Local);
            var name = $"config-{ts:yyyyMMdd-HHmmss}-old-backup.json";
            fs.File.WriteAllText(Path.Combine(backupsDir, name), "{}");
        }

        var configPath = Path.Combine(Path.GetTempPath(), "config.json");
        fs.File.WriteAllText(configPath, "{}");

        var sut = new ConfigBackupService(backupsDir, fs);
        sut.Backup(configPath, "new-backup");

        var files = fs.Directory.GetFiles(backupsDir);
        files.Length.ShouldBe(ConfigBackupService.MaxBackups);
    }

    // 4. Reason slug appears in filename
    [Fact]
    public void Backup_ReasonSlug_AppearsInFilename()
    {
        var fs = new MockFileSystem();
        var backupsDir = MakeBackupsDir();
        fs.Directory.CreateDirectory(backupsDir);

        var configPath = Path.Combine(Path.GetTempPath(), "config.json");
        fs.File.WriteAllText(configPath, "{}");

        var sut = new ConfigBackupService(backupsDir, fs);
        sut.Backup(configPath, "my-custom-reason");

        var files = fs.Directory.GetFiles(backupsDir);
        files.Length.ShouldBe(1);
        Path.GetFileName(files[0]).ShouldContain("my-custom-reason");
    }

    // 5. Special chars in reason are sanitized to hyphens
    [Fact]
    public void Backup_SpecialCharsInReason_AreSanitized()
    {
        var fs = new MockFileSystem();
        var backupsDir = MakeBackupsDir();
        fs.Directory.CreateDirectory(backupsDir);

        var configPath = Path.Combine(Path.GetTempPath(), "config.json");
        fs.File.WriteAllText(configPath, "{}");

        var sut = new ConfigBackupService(backupsDir, fs);
        sut.Backup(configPath, "before/agent create larry");

        var files = fs.Directory.GetFiles(backupsDir);
        files.Length.ShouldBe(1);
        var name = Path.GetFileName(files[0]);
        name.ShouldContain("before-agent-create-larry");
        name.ShouldNotContain("/");
        name.ShouldNotContain(" ");
    }
}
