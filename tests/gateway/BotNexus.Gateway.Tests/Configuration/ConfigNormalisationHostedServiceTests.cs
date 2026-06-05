using System.Text.Json;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;
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
        var config = new BotNexus.Gateway.Abstractions.Models.HeartbeatAgentConfig();
        config.Enabled.ShouldBeTrue("Enabled should default to true (issue #822)");
    }

    [Fact]
    public void HeartbeatAgentConfig_DefaultIntervalMinutes_Is30()
    {
        var config = new BotNexus.Gateway.Abstractions.Models.HeartbeatAgentConfig();
        config.IntervalMinutes.ShouldBe(30);
    }

    // -------------------------------------------------------------------------
    // Normalisation: missing heartbeat block is injected
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_AgentFileMissingHeartbeat_InjectsDefaultHeartbeatBlock()
    {
        // Arrange
        var dir = CreateTempDirectory();
        try
        {
            var agentJson = """
                {
                  "agentId": "alpha",
                  "displayName": "Alpha",
                  "modelId": "gpt-4.1",
                  "apiProvider": "github-copilot"
                }
                """;
            var filePath = Path.Combine(dir, "alpha.json");
            await File.WriteAllTextAsync(filePath, agentJson);

            var (service, _) = BuildService(dir);

            // Act
            await service.StartAsync(CancellationToken.None);

            // Assert — heartbeat block must now be present
            var updatedJson = await File.ReadAllTextAsync(filePath);
            using var doc = JsonDocument.Parse(updatedJson);
            doc.RootElement.TryGetProperty("heartbeat", out var heartbeat).ShouldBeTrue("heartbeat block should be injected");
            heartbeat.GetProperty("enabled").GetBoolean().ShouldBeTrue();
            heartbeat.GetProperty("intervalMinutes").GetInt32().ShouldBe(30);
            heartbeat.GetProperty("quietHours").GetProperty("start").GetString().ShouldBe("23:00");
            heartbeat.GetProperty("quietHours").GetProperty("end").GetString().ShouldBe("07:00");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_AgentFileHasHeartbeat_IsNotOverwritten()
    {
        // Arrange — agent already has an explicit heartbeat block with custom values
        var dir = CreateTempDirectory();
        try
        {
            var agentJson = """
                {
                  "agentId": "beta",
                  "displayName": "Beta",
                  "modelId": "gpt-4.1",
                  "apiProvider": "github-copilot",
                  "heartbeat": {
                    "enabled": false,
                    "intervalMinutes": 60
                  }
                }
                """;
            var filePath = Path.Combine(dir, "beta.json");
            await File.WriteAllTextAsync(filePath, agentJson);

            var (service, _) = BuildService(dir);

            // Act
            await service.StartAsync(CancellationToken.None);

            // Assert — explicit values not overwritten
            var updatedJson = await File.ReadAllTextAsync(filePath);
            using var doc = JsonDocument.Parse(updatedJson);
            var heartbeat = doc.RootElement.GetProperty("heartbeat");
            heartbeat.GetProperty("enabled").GetBoolean().ShouldBeFalse("explicit enabled=false must not be overwritten");
            heartbeat.GetProperty("intervalMinutes").GetInt32().ShouldBe(60, "explicit intervalMinutes=60 must not be overwritten");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_MissingDirectory_DoesNotThrow()
    {
        // Arrange — use a path that doesn't exist
        var nonExistentDir = Path.Combine(Path.GetTempPath(), "botnexus-norm-test-nonexistent-" + Guid.NewGuid().ToString("N"));
        var (service, _) = BuildService(nonExistentDir);

        // Act & Assert — should not throw
        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_MultipleFiles_NormalisesOnlyMissingHeartbeats()
    {
        // Arrange — two agent files: one with, one without heartbeat
        var dir = CreateTempDirectory();
        try
        {
            var fileWithout = Path.Combine(dir, "alpha.json");
            var fileWith = Path.Combine(dir, "beta.json");

            await File.WriteAllTextAsync(fileWithout,
                """{"agentId":"alpha","displayName":"Alpha","modelId":"gpt-4.1","apiProvider":"github-copilot"}""");
            await File.WriteAllTextAsync(fileWith,
                """{"agentId":"beta","displayName":"Beta","modelId":"gpt-4.1","apiProvider":"github-copilot","heartbeat":{"enabled":false}}""");

            var (service, _) = BuildService(dir);

            // Act
            await service.StartAsync(CancellationToken.None);

            // Assert alpha: heartbeat injected
            using var docAlpha = JsonDocument.Parse(await File.ReadAllTextAsync(fileWithout));
            docAlpha.RootElement.TryGetProperty("heartbeat", out _).ShouldBeTrue("alpha should have heartbeat injected");

            // Assert beta: heartbeat unchanged (still enabled=false, no quietHours added)
            using var docBeta = JsonDocument.Parse(await File.ReadAllTextAsync(fileWith));
            var betaHb = docBeta.RootElement.GetProperty("heartbeat");
            betaHb.GetProperty("enabled").GetBoolean().ShouldBeFalse("beta heartbeat.enabled must not be changed");
            betaHb.TryGetProperty("quietHours", out _).ShouldBeFalse("beta should not have quietHours added");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "botnexus-norm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (ConfigNormalisationHostedService service, IFileSystem fileSystem) BuildService(string directory)
    {
        var realFs = new System.IO.Abstractions.FileSystem();
        var sources = new IAgentConfigurationSource[]
        {
            new FileAgentConfigurationSource(directory, NullLogger<FileAgentConfigurationSource>.Instance, realFs)
        };
        var service = new ConfigNormalisationHostedService(sources, realFs, NullLogger<ConfigNormalisationHostedService>.Instance);
        return (service, realFs);
    }
}
