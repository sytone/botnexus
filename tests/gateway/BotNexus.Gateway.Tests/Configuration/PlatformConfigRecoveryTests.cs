using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Tests for PlatformConfigLoader.IsConfigSuspicious and TryRecoverFromBackup.
/// </summary>
public sealed class PlatformConfigRecoveryTests
{
    // ── IsConfigSuspicious ─────────────────────────────────────────────────

    [Fact]
    public void IsConfigSuspicious_EmptyShellJson_ReturnsTrue()
    {
        var raw = """{"version":1}""";
        var config = new PlatformConfig();
        PlatformConfigLoader.IsConfigSuspicious(config, raw).ShouldBeTrue();
    }

    [Fact]
    public void IsConfigSuspicious_HealthyConfig_ReturnsFalse()
    {
        var raw = """
            {
              "version": 1,
              "providers": { "copilot": { "apiKey": "k" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """;
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new ProviderConfig { ApiKey = "k" }
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new AgentDefinitionConfig { Provider = "copilot", Model = "gpt-4.1" }
            }
        };
        PlatformConfigLoader.IsConfigSuspicious(config, raw).ShouldBeFalse();
    }

    [Fact]
    public void IsConfigSuspicious_StructurallyEmptyButLargeJson_ReturnsFalse()
    {
        // Large JSON (> MinHealthyConfigLength) with empty structure -- should NOT be suspicious
        // because physical size guard prevents false positives on intentionally minimal configs.
        var raw = new string(' ', PlatformConfigLoader.MinHealthyConfigLength + 1) + """{"version":1}""";
        var config = new PlatformConfig();
        PlatformConfigLoader.IsConfigSuspicious(config, raw).ShouldBeFalse();
    }

    // ── TryRecoverFromBackup ───────────────────────────────────────────────

    private static readonly string ValidBackupJson = """
        {
          "version": 1,
          "providers": { "copilot": { "apiKey": "backup-key" } },
          "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
        }
        """;

    [Fact]
    public void TryRecoverFromBackup_ValidBackupExists_ReturnsRecoveredConfig()
    {
        var fs = new MockFileSystem();
        var configPath = "/home/user/.botnexus/config.json";
        var backupsDir = "/home/user/.botnexus/backups";
        var backupPath = backupsDir + "/config-20260601-120000-pre-write.json";

        fs.AddDirectory(backupsDir);
        fs.AddFile(backupPath, new MockFileData(ValidBackupJson));

        var result = PlatformConfigLoader.TryRecoverFromBackup(configPath, out var recovered, fs);

        result.ShouldNotBeNull();
        recovered.ShouldNotBeNull();
        Path.GetFileName(recovered!).ShouldBe(Path.GetFileName(backupPath));
        result.Agents.ShouldNotBeNull();
        result.Agents.ContainsKey("assistant").ShouldBeTrue();
    }

    [Fact]
    public void TryRecoverFromBackup_NoBackupsDirectory_ReturnsNull()
    {
        var fs = new MockFileSystem();
        var configPath = "/home/user/.botnexus/config.json";

        var result = PlatformConfigLoader.TryRecoverFromBackup(configPath, out var recovered, fs);

        result.ShouldBeNull();
        recovered.ShouldBeNull();
    }

    [Fact]
    public void TryRecoverFromBackup_CorruptBackupOnly_ReturnsNull()
    {
        var fs = new MockFileSystem();
        var configPath = "/home/user/.botnexus/config.json";
        var backupsDir = "/home/user/.botnexus/backups";

        fs.AddDirectory(backupsDir);
        fs.AddFile(backupsDir + "/config-20260601-120000-bad.json", new MockFileData("not-json{{{{"));

        var result = PlatformConfigLoader.TryRecoverFromBackup(configPath, out var recovered, fs);

        result.ShouldBeNull();
        recovered.ShouldBeNull();
    }

    [Fact]
    public void TryRecoverFromBackup_MultipleBackups_PicksMostRecent()
    {
        var fs = new MockFileSystem();
        var configPath = "/home/user/.botnexus/config.json";
        var backupsDir = "/home/user/.botnexus/backups";

        // Older backup — valid but stale
        var older = backupsDir + "/config-20260101-000000-old.json";
        fs.AddDirectory(backupsDir);
        fs.AddFile(older, new MockFileData(ValidBackupJson));
        fs.GetFile(older).LastWriteTime = new DateTime(2026, 1, 1);

        // Newer backup — also valid
        var newerJson = ValidBackupJson.Replace("backup-key", "newer-key");
        var newer = backupsDir + "/config-20260601-120000-recent.json";
        fs.AddFile(newer, new MockFileData(newerJson));
        fs.GetFile(newer).LastWriteTime = new DateTime(2026, 6, 1);

        var result = PlatformConfigLoader.TryRecoverFromBackup(configPath, out var recovered, fs);

        result.ShouldNotBeNull();
        recovered.ShouldNotBeNull();
        Path.GetFileName(recovered!).ShouldBe(Path.GetFileName(newer));
    }
}
