using BotNexus.Core.Configuration;
using FluentAssertions;

namespace BotNexus.Tests.Unit.Tests;

[Collection("BotNexusHomeEnvVar")]
public sealed class BotNexusHomeTests : IDisposable
{
    private const string HomeOverrideEnvVar = "BOTNEXUS_HOME";
    private readonly string? _originalHomeOverride;
    private readonly string _testHomePath;

    public BotNexusHomeTests()
    {
        _originalHomeOverride = Environment.GetEnvironmentVariable(HomeOverrideEnvVar);
        _testHomePath = Path.Combine(Path.GetTempPath(), $"botnexus-home-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(HomeOverrideEnvVar, _testHomePath);
    }

    [Fact]
    public void Initialize_CreatesAgentsDirectory()
    {
        BotNexusHome.Initialize();

        Directory.Exists(Path.Combine(_testHomePath, "agents")).Should().BeTrue();
    }

    [Fact]
    public void InitializeAgentWorkspace_CreatesWorkspaceMemoryDirectories()
    {
        var agentWorkspacePath = BotNexusHome.GetAgentWorkspacePath("farnsworth");

        BotNexusHome.InitializeAgentWorkspace("farnsworth");

        Directory.Exists(agentWorkspacePath).Should().BeTrue();
        Directory.Exists(Path.Combine(agentWorkspacePath, "memory")).Should().BeTrue();
        Directory.Exists(Path.Combine(agentWorkspacePath, "memory", "daily")).Should().BeTrue();
    }

    [Fact]
    public void AgentsPath_ResolvesUnderHomePath()
    {
        BotNexusHome.AgentsPath.Should().Be(Path.Combine(_testHomePath, "agents"));
    }

    [Fact]
    public void Initialize_CreatesMinimalDefaultConfigWithoutProviders()
    {
        BotNexusHome.Initialize();

        var configPath = Path.Combine(_testHomePath, "config.json");
        var configContent = File.ReadAllText(configPath);

        configContent.Should().Contain("\"Providers\": {}", "first-run config should not force provider setup");
        configContent.Should().NotContain("\"copilot\"", "providers should be user configured");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeOverrideEnvVar, _originalHomeOverride);
        if (Directory.Exists(_testHomePath))
            Directory.Delete(_testHomePath, recursive: true);
    }
}
