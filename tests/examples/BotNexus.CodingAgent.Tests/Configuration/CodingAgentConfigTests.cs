using System.Reflection;
using BotNexus.CodingAgent;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Configuration;

public sealed class CodingAgentConfigTests
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), "repo");

    [Fact]
    public void CreateDefaults_HasExpectedDefaultValues()
    {
        var createDefaults = typeof(CodingAgentConfig).GetMethod("CreateDefaults", BindingFlags.NonPublic | BindingFlags.Static);
        createDefaults.ShouldNotBeNull();

        var config = createDefaults!.Invoke(null, [Path.GetFullPath(_workingDirectory)]) as CodingAgentConfig;

        config.ShouldNotBeNull();
        config!.ConfigDirectory.ShouldBe(Path.Combine(_workingDirectory, ".botnexus-agent"));
        config.SessionsDirectory.ShouldBe(Path.Combine(_workingDirectory, ".botnexus-agent", "sessions"));
        config.ExtensionsDirectory.ShouldBe(Path.Combine(_workingDirectory, ".botnexus-agent", "extensions"));
        config.SkillsDirectory.ShouldBe(Path.Combine(_workingDirectory, ".botnexus-agent", "skills"));
        config.MaxToolIterations.ShouldBe(40);
        config.MaxContextTokens.ShouldBe(100000);
        config.AllowedCommands.ShouldBeEmpty();
        config.BlockedPaths.ShouldBeEmpty();
    }

    [Fact]
    public void Load_WhenLocalConfigExists_AppliesOverrides()
    {
        var configDirectory = Path.Combine(_workingDirectory, ".botnexus-agent");
        _fileSystem.Directory.CreateDirectory(configDirectory);
        var localConfigPath = Path.Combine(configDirectory, "config.json");
        _fileSystem.File.WriteAllText(localConfigPath, """
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

        var config = CodingAgentConfig.Load(_fileSystem, _workingDirectory);

        config.Model.ShouldBe("gpt-test");
        config.Provider.ShouldBe("openai");
        config.ApiKey.ShouldBe("test-key");
        config.MaxToolIterations.ShouldBe(12);
        config.MaxContextTokens.ShouldBe(4096);
        config.AllowedCommands.ShouldBe(new[] { "dotnet", "git" });
        config.BlockedPaths.ShouldBe(new[] { "secrets" });
        config.Custom.ShouldContainKey("mode");
        config.Custom["mode"]?.ToString().ShouldBe("strict");
    }

    [Fact]
    public void EnsureDirectories_CreatesExpectedDirectories()
    {
        CodingAgentConfig.EnsureDirectories(_fileSystem, _workingDirectory);

        _fileSystem.Directory.Exists(Path.Combine(_workingDirectory, ".botnexus-agent")).ShouldBeTrue();
        _fileSystem.Directory.Exists(Path.Combine(_workingDirectory, ".botnexus-agent", "sessions")).ShouldBeTrue();
        _fileSystem.Directory.Exists(Path.Combine(_workingDirectory, ".botnexus-agent", "extensions")).ShouldBeTrue();
        _fileSystem.Directory.Exists(Path.Combine(_workingDirectory, ".botnexus-agent", "skills")).ShouldBeTrue();
    }
}
