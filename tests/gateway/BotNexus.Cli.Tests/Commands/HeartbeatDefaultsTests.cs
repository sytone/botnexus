using System.Text.Json;
using BotNexus.Cli.Commands;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for heartbeat default config scaffolding in InitCommand and AgentCommands.
/// Covers acceptance criteria for issue #823.
/// </summary>
public sealed class HeartbeatDefaultsTests
{
    // -------------------------------------------------------------------------
    // InitCommand — agents.defaults must include heartbeat block
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Init_DefaultConfig_IncludesHeartbeatInAgentDefaults()
    {
        // Arrange
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-hb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            var cmd = new InitCommand();

            // Act
            await cmd.ExecuteAsync(tempHome, force: false, verbose: false, CancellationToken.None);

            // Assert — agents.defaults.heartbeat must be present
            var configPath = Path.Combine(tempHome, "config.json");
            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            root.TryGetProperty("agents", out var agents).ShouldBeTrue("agents section missing");
            agents.TryGetProperty("defaults", out var defaults).ShouldBeTrue("agents.defaults missing");
            defaults.TryGetProperty("heartbeat", out var heartbeat).ShouldBeTrue("agents.defaults.heartbeat missing");
            heartbeat.TryGetProperty("enabled", out var enabled).ShouldBeTrue("heartbeat.enabled missing");
            enabled.GetBoolean().ShouldBeTrue("heartbeat.enabled should be true");
            heartbeat.TryGetProperty("intervalMinutes", out var interval).ShouldBeTrue("heartbeat.intervalMinutes missing");
            interval.GetInt32().ShouldBe(30);
            heartbeat.TryGetProperty("quietHours", out var quietHours).ShouldBeTrue("heartbeat.quietHours missing");
            quietHours.TryGetProperty("enabled", out var qhEnabled).ShouldBeTrue();
            qhEnabled.GetBoolean().ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task Init_DefaultConfig_HeartbeatDefaults_HasExpectedQuietHoursBoundaries()
    {
        // Arrange
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-hb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            var cmd = new InitCommand();

            // Act
            await cmd.ExecuteAsync(tempHome, force: false, verbose: false, CancellationToken.None);

            // Assert — quiet hours default 23:00–07:00
            var configPath = Path.Combine(tempHome, "config.json");
            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            var quietHours = doc.RootElement
                .GetProperty("agents")
                .GetProperty("defaults")
                .GetProperty("heartbeat")
                .GetProperty("quietHours");

            quietHours.GetProperty("start").GetString().ShouldBe("23:00");
            quietHours.GetProperty("end").GetString().ShouldBe("07:00");
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // AgentCommands.ExecuteAddAsync — new agent should include heartbeat block
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AgentAdd_NewAgent_IncludesHeartbeatBlock()
    {
        // Arrange
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-hb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            // Write a minimal config.json
            var configPath = Path.Combine(tempHome, "config.json");
            var minimalConfig = """
                {
                  "version": 1,
                  "agents": {}
                }
                """;
            await File.WriteAllTextAsync(configPath, minimalConfig);

            var cmds = new AgentCommands();

            // Act
            var result = await cmds.ExecuteAddAsync(
                id: "test-agent",
                provider: "github-copilot",
                model: "gpt-4.1",
                enabled: true,
                configPath: configPath,
                verbose: false,
                cancellationToken: CancellationToken.None);

            // Assert
            result.ShouldBe(0, "ExecuteAddAsync should succeed");

            var updatedJson = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(updatedJson);
            var agentElem = doc.RootElement.GetProperty("agents").GetProperty("test-agent");

            agentElem.TryGetProperty("heartbeat", out var heartbeat).ShouldBeTrue("heartbeat block missing from new agent");
            heartbeat.GetProperty("enabled").GetBoolean().ShouldBeTrue();
            heartbeat.GetProperty("intervalMinutes").GetInt32().ShouldBe(30);
            heartbeat.GetProperty("quietHours").GetProperty("start").GetString().ShouldBe("23:00");
            heartbeat.GetProperty("quietHours").GetProperty("end").GetString().ShouldBe("07:00");
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task AgentAdd_NewAgent_HeartbeatEnabled_IsTrue()
    {
        // Arrange — heartbeat enabled should be true even without explicit defaults block
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-hb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            var configPath = Path.Combine(tempHome, "config.json");
            await File.WriteAllTextAsync(configPath, """{"version":1,"agents":{}}""");

            var cmds = new AgentCommands();

            await cmds.ExecuteAddAsync(
                id: "alpha",
                provider: "openai",
                model: "gpt-4o",
                enabled: true,
                configPath: configPath,
                verbose: false,
                cancellationToken: CancellationToken.None);

            var json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            var heartbeat = doc.RootElement.GetProperty("agents").GetProperty("alpha").GetProperty("heartbeat");
            heartbeat.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }
}
