using System.Reflection;
using BotNexus.CodingAgent;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Configuration;

public sealed class CodingAgentConfigTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-config-{Guid.NewGuid():N}");

    public CodingAgentConfigTests()
    {
        Directory.CreateDirectory(_workingDirectory);
    }

    [Fact]
    public void CreateDefaults_HasExpectedDefaultValues()
    {
        var createDefaults = typeof(CodingAgentConfig).GetMethod("CreateDefaults", BindingFlags.NonPublic | BindingFlags.Static);
        createDefaults.Should().NotBeNull();

        var config = createDefaults!.Invoke(null, [Path.GetFullPath(_workingDirectory)]) as CodingAgentConfig;

        config.Should().NotBeNull();
        config!.ConfigDirectory.Should().Be(Path.Combine(_workingDirectory, ".botnexus-agent"));
        config.SessionsDirectory.Should().Be(Path.Combine(_workingDirectory, ".botnexus-agent", "sessions"));
        config.ExtensionsDirectory.Should().Be(Path.Combine(_workingDirectory, ".botnexus-agent", "extensions"));
        config.SkillsDirectory.Should().Be(Path.Combine(_workingDirectory, ".botnexus-agent", "skills"));
        config.MaxToolIterations.Should().Be(40);
        config.MaxContextTokens.Should().Be(100000);
        config.AllowedCommands.Should().BeEmpty();
        config.BlockedPaths.Should().BeEmpty();
    }

    [Fact]
    public void Load_WhenLocalConfigExists_AppliesOverrides()
    {
        var configDirectory = Path.Combine(_workingDirectory, ".botnexus-agent");
        Directory.CreateDirectory(configDirectory);
        var localConfigPath = Path.Combine(configDirectory, "config.json");
        File.WriteAllText(localConfigPath, """
        {
          "model": "gpt-test",
          "provider": "openai",
          "apiKey": "test-key",
          "maxToolIterations": 12,
          "maxContextTokens": 4096,
          "allowedCommands": ["dotnet", "git"],
          "blockedPaths": ["secrets"],
          "custom": { "mode": "strict" }
        }
        """);

        var config = CodingAgentConfig.Load(_workingDirectory);

        config.Model.Should().Be("gpt-test");
        config.Provider.Should().Be("openai");
        config.ApiKey.Should().Be("test-key");
        config.MaxToolIterations.Should().Be(12);
        config.MaxContextTokens.Should().Be(4096);
        config.AllowedCommands.Should().Equal("dotnet", "git");
        config.BlockedPaths.Should().Equal("secrets");
        config.Custom.Should().ContainKey("mode");
        config.Custom["mode"]?.ToString().Should().Be("strict");
    }

    [Fact]
    public void EnsureDirectories_CreatesExpectedDirectories()
    {
        CodingAgentConfig.EnsureDirectories(_workingDirectory);

        Directory.Exists(Path.Combine(_workingDirectory, ".botnexus-agent")).Should().BeTrue();
        Directory.Exists(Path.Combine(_workingDirectory, ".botnexus-agent", "sessions")).Should().BeTrue();
        Directory.Exists(Path.Combine(_workingDirectory, ".botnexus-agent", "extensions")).Should().BeTrue();
        Directory.Exists(Path.Combine(_workingDirectory, ".botnexus-agent", "skills")).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
