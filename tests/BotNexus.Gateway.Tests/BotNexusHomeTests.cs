using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class BotNexusHomeTests
{
    private const string HomePath = @"C:\botnexus-home";

    [Fact]
    public void Initialize_CreatesRequiredDirectoriesIncludingAgents()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);

        home.Initialize();

        fs.Directory.Exists(Path.Combine(HomePath, "extensions")).Should().BeTrue();
        fs.Directory.Exists(Path.Combine(HomePath, "tokens")).Should().BeTrue();
        fs.Directory.Exists(Path.Combine(HomePath, "sessions")).Should().BeTrue();
        fs.Directory.Exists(Path.Combine(HomePath, "logs")).Should().BeTrue();
        fs.Directory.Exists(Path.Combine(HomePath, "agents")).Should().BeTrue();
    }

    [Fact]
    public void GetAgentDirectory_CreatesWorkspaceAndScaffoldFiles()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);

        var path = home.GetAgentDirectory("farnsworth");
        var workspacePath = Path.Combine(path, "workspace");

        path.Should().Be(Path.Combine(HomePath, "agents", "farnsworth"));
        fs.Directory.Exists(workspacePath).Should().BeTrue();
        fs.Directory.Exists(Path.Combine(path, "data", "sessions")).Should().BeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "AGENTS.md")).Should().BeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "SOUL.md")).Should().BeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "TOOLS.md")).Should().BeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).Should().BeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "IDENTITY.md")).Should().BeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "USER.md")).Should().BeTrue();
        fs.File.ReadAllText(Path.Combine(workspacePath, "AGENTS.md")).Should().Contain("# Agents");
    }

    [Fact]
    public void GetAgentDirectory_WhenLegacyLayoutExists_MigratesFilesToWorkspace()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);
        var agentPath = Path.Combine(HomePath, "agents", "farnsworth");
        fs.Directory.CreateDirectory(agentPath);
        fs.File.WriteAllText(Path.Combine(agentPath, "SOUL.md"), "legacy soul");
        fs.File.WriteAllText(Path.Combine(agentPath, "IDENTITY.md"), "legacy identity");
        fs.File.WriteAllText(Path.Combine(agentPath, "USER.md"), "legacy user");
        fs.File.WriteAllText(Path.Combine(agentPath, "AGENTS.md"), "legacy agents");
        fs.File.WriteAllText(Path.Combine(agentPath, "TOOLS.md"), "legacy tools");
        fs.File.WriteAllText(Path.Combine(agentPath, "BOOTSTRAP.md"), "legacy bootstrap");
        fs.File.WriteAllText(Path.Combine(agentPath, "MEMORY.md"), "legacy memory");

        var path = home.GetAgentDirectory("farnsworth");
        var workspacePath = Path.Combine(path, "workspace");

        fs.Directory.Exists(workspacePath).Should().BeTrue();
        fs.Directory.Exists(Path.Combine(path, "data", "sessions")).Should().BeTrue();
        fs.File.Exists(Path.Combine(path, "AGENTS.md")).Should().BeFalse();
        fs.File.Exists(Path.Combine(path, "SOUL.md")).Should().BeFalse();
        fs.File.Exists(Path.Combine(path, "TOOLS.md")).Should().BeFalse();
        fs.File.Exists(Path.Combine(path, "BOOTSTRAP.md")).Should().BeFalse();
        fs.File.Exists(Path.Combine(path, "IDENTITY.md")).Should().BeFalse();
        fs.File.Exists(Path.Combine(path, "USER.md")).Should().BeFalse();
        fs.File.Exists(Path.Combine(path, "MEMORY.md")).Should().BeFalse();
        fs.File.ReadAllText(Path.Combine(workspacePath, "AGENTS.md")).Should().Be("legacy agents");
        fs.File.ReadAllText(Path.Combine(workspacePath, "SOUL.md")).Should().Be("legacy soul");
        fs.File.ReadAllText(Path.Combine(workspacePath, "TOOLS.md")).Should().Be("legacy tools");
        fs.File.ReadAllText(Path.Combine(workspacePath, "BOOTSTRAP.md")).Should().Be("legacy bootstrap");
        fs.File.ReadAllText(Path.Combine(workspacePath, "IDENTITY.md")).Should().Be("legacy identity");
        fs.File.ReadAllText(Path.Combine(workspacePath, "USER.md")).Should().Be("legacy user");
        fs.File.ReadAllText(Path.Combine(workspacePath, "MEMORY.md")).Should().Be("legacy memory");
    }
}
