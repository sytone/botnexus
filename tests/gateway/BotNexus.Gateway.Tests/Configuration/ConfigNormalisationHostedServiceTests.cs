using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Shouldly;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ConfigNormalisationHostedService"/>.
/// Covers acceptance criteria for issue #822.
/// </summary>
public sealed class ConfigNormalisationHostedServiceTests
{
    // -------------------------------------------------------------------------
    // HeartbeatAgentConfig.Enabled defaults to true
    // -------------------------------------------------------------------------

    [Fact]
    public void HeartbeatAgentConfig_DefaultEnabled_IsTrue()
    {
        var config = new HeartbeatAgentConfig();
        config.Enabled.ShouldBeTrue("Enabled should default to true (issue #822)");
    }

    [Fact]
    public void HeartbeatAgentConfig_DefaultIntervalMinutes_Is30()
    {
        var config = new HeartbeatAgentConfig();
        config.IntervalMinutes.ShouldBe(30);
    }

    // -------------------------------------------------------------------------
    // Normalisation: missing heartbeat block in agents.defaults is injected
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_ConfigMissingAgentsDefaultsHeartbeat_InjectsDefaultHeartbeatBlock()
    {
        // Arrange — config.json has agents but no agents.defaults.heartbeat
        var configJson = """
            {
              "configVersion": 1,
              "agents": {
                "defaults": {
                  "provider": "github-copilot"
                },
                "alpha": {
                  "enabled": true,
                  "displayName": "Alpha",
                  "model": "gpt-4.1"
                }
              }
            }
            """;
        var (service, fs, configPath) = BuildService(configJson);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — agents.defaults.heartbeat is now present
        var updatedJson = fs.File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(updatedJson);
        var defaults = doc.RootElement.GetProperty("agents").GetProperty("defaults");
        defaults.TryGetProperty("heartbeat", out var heartbeat).ShouldBeTrue("heartbeat block should be injected into agents.defaults");
        heartbeat.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        heartbeat.GetProperty("intervalMinutes").GetInt32().ShouldBe(30);
        heartbeat.GetProperty("quietHours").GetProperty("start").GetString().ShouldBe("23:00");
        heartbeat.GetProperty("quietHours").GetProperty("end").GetString().ShouldBe("07:00");
    }

    [Fact]
    public async Task StartAsync_ConfigHasAgentsDefaultsHeartbeat_IsNotOverwritten()
    {
        // Arrange — config.json already has agents.defaults.heartbeat with custom values
        var configJson = """
            {
              "configVersion": 1,
              "agents": {
                "defaults": {
                  "heartbeat": {
                    "enabled": false,
                    "intervalMinutes": 60
                  }
                }
              }
            }
            """;
        var (service, fs, configPath) = BuildService(configJson);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — explicit values must not be overwritten
        var updatedJson = fs.File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(updatedJson);
        var heartbeat = doc.RootElement.GetProperty("agents").GetProperty("defaults").GetProperty("heartbeat");
        heartbeat.GetProperty("enabled").GetBoolean().ShouldBeFalse("explicit enabled=false must not be overwritten");
        heartbeat.GetProperty("intervalMinutes").GetInt32().ShouldBe(60, "explicit intervalMinutes=60 must not be overwritten");
    }

    [Fact]
    public async Task StartAsync_MissingConfigFile_DoesNotThrow()
    {
        // Arrange — config.json does not exist
        var fs = new MockFileSystem();
        var service = new ConfigNormalisationHostedService(fs, NullLogger<ConfigNormalisationHostedService>.Instance);

        // Act & Assert — should not throw even if config.json is absent
        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_NoAgentsSection_DoesNotModifyConfig()
    {
        // Arrange — config.json has no agents block
        var configJson = """
            {
              "configVersion": 1,
              "gateway": {}
            }
            """;
        var (service, fs, configPath) = BuildService(configJson);
        var originalJson = fs.File.ReadAllText(configPath);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — file unchanged (no agents block means nothing to normalise)
        var resultJson = fs.File.ReadAllText(configPath);
        resultJson.ShouldBe(originalJson, "file should be unchanged when agents block is absent");
    }

    [Fact]
    public async Task StartAsync_NoAgentsDefaultsSection_CreatesDefaultsAndInjectsHeartbeat()
    {
        // Arrange — config.json has agents but no agents.defaults key
        var configJson = """
            {
              "configVersion": 1,
              "agents": {
                "alpha": {
                  "enabled": true,
                  "displayName": "Alpha",
                  "model": "gpt-4.1",
                  "provider": "github-copilot"
                }
              }
            }
            """;
        var (service, fs, configPath) = BuildService(configJson);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — defaults block created and heartbeat injected
        var updatedJson = fs.File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(updatedJson);
        var agents = doc.RootElement.GetProperty("agents");
        agents.TryGetProperty("defaults", out var defaults).ShouldBeTrue("defaults block should be created");
        defaults.TryGetProperty("heartbeat", out var heartbeat).ShouldBeTrue("heartbeat should be injected");
        heartbeat.GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a <see cref="ConfigNormalisationHostedService"/> backed by a
    /// <see cref="MockFileSystem"/> containing a <c>config.json</c> at the
    /// default path for the current user.
    /// </summary>
    private static (ConfigNormalisationHostedService service, MockFileSystem fs, string configPath) BuildService(
        string configJsonContent)
    {
        var fs = new MockFileSystem();
        var configPath = PlatformConfigLoader.GetDefaultConfigPath(fs);
        var dir = System.IO.Path.GetDirectoryName(configPath)!;
        fs.Directory.CreateDirectory(dir);
        fs.File.WriteAllText(configPath, configJsonContent);

        var service = new ConfigNormalisationHostedService(fs, NullLogger<ConfigNormalisationHostedService>.Instance);
        return (service, fs, configPath);
    }
}
