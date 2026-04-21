using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests;

public sealed class BotNexusHomeTests
{
    private static readonly string HomePath = Path.Combine(Path.GetTempPath(), "botnexus-home");

    [Fact]
    public void Initialize_CreatesRequiredDirectoriesIncludingAgents()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);

        home.Initialize();

        fs.Directory.Exists(Path.Combine(HomePath, "extensions")).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(HomePath, "tokens")).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(HomePath, "sessions")).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(HomePath, "logs")).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(HomePath, "agents")).ShouldBeTrue();
    }

    [Fact]
    public void GetAgentDirectory_CreatesWorkspaceAndScaffoldFiles()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);

        var path = home.GetAgentDirectory("farnsworth");
        var workspacePath = Path.Combine(path, "workspace");

        path.ShouldBe(Path.Combine(HomePath, "agents", "farnsworth"));
        fs.Directory.Exists(workspacePath).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(path, "data", "sessions")).ShouldBeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "AGENTS.md")).ShouldBeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "SOUL.md")).ShouldBeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "TOOLS.md")).ShouldBeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).ShouldBeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "IDENTITY.md")).ShouldBeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "USER.md")).ShouldBeTrue();
        fs.File.ReadAllText(Path.Combine(workspacePath, "AGENTS.md")).ShouldContain("# Agents");
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

        fs.Directory.Exists(workspacePath).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(path, "data", "sessions")).ShouldBeTrue();
        fs.File.Exists(Path.Combine(path, "AGENTS.md")).ShouldBeFalse();
        fs.File.Exists(Path.Combine(path, "SOUL.md")).ShouldBeFalse();
        fs.File.Exists(Path.Combine(path, "TOOLS.md")).ShouldBeFalse();
        fs.File.Exists(Path.Combine(path, "BOOTSTRAP.md")).ShouldBeFalse();
        fs.File.Exists(Path.Combine(path, "IDENTITY.md")).ShouldBeFalse();
        fs.File.Exists(Path.Combine(path, "USER.md")).ShouldBeFalse();
        fs.File.Exists(Path.Combine(path, "MEMORY.md")).ShouldBeFalse();
        fs.File.ReadAllText(Path.Combine(workspacePath, "AGENTS.md")).ShouldBe("legacy agents");
        fs.File.ReadAllText(Path.Combine(workspacePath, "SOUL.md")).ShouldBe("legacy soul");
        fs.File.ReadAllText(Path.Combine(workspacePath, "TOOLS.md")).ShouldBe("legacy tools");
        fs.File.ReadAllText(Path.Combine(workspacePath, "BOOTSTRAP.md")).ShouldBe("legacy bootstrap");
        fs.File.ReadAllText(Path.Combine(workspacePath, "IDENTITY.md")).ShouldBe("legacy identity");
        fs.File.ReadAllText(Path.Combine(workspacePath, "USER.md")).ShouldBe("legacy user");
        fs.File.ReadAllText(Path.Combine(workspacePath, "MEMORY.md")).ShouldBe("legacy memory");
    }
}
