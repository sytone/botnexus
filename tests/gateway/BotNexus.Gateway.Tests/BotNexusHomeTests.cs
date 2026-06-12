using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests;

public sealed class BotNexusHomeTests
{
    private static readonly string HomePath = Path.Combine(Path.GetTempPath(), "botnexus-home");
    private static readonly string DataDirPath = Path.Combine(Path.GetTempPath(), "botnexus-data");

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
    public void GetAgentDirectory_ScaffoldedBootstrap_HasFirstRunInstructions()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);

        var path = home.GetAgentDirectory("newbie");
        var bootstrapContent = fs.File.ReadAllText(Path.Combine(path, "workspace", "BOOTSTRAP.md"));

        // BOOTSTRAP.md must have first-run ritual content — not just a heading stub
        bootstrapContent.ShouldContain("first");
        bootstrapContent.ShouldContain("SOUL.md");
        bootstrapContent.ShouldContain("IDENTITY.md");
        bootstrapContent.ShouldContain("deleted");
    }

    [Fact]
    public void GetAgentDirectory_ScaffoldedTemplates_AreNotEmpty()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);

        var path = home.GetAgentDirectory("newbie");
        var workspace = Path.Combine(path, "workspace");

        // All scaffold files must have content (not just empty files)
        foreach (var file in new[] { "SOUL.md", "IDENTITY.md", "TOOLS.md", "USER.md", "AGENTS.md" })
        {
            var content = fs.File.ReadAllText(Path.Combine(workspace, file));
            content.Length.ShouldBeGreaterThan(10, $"{file} should not be empty");
        }
    }

    [Fact]
    public void GetAgentDirectory_ScaffoldedAgentsTemplate_IncludesMinimalMemoryInstructions()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);

        var path = home.GetAgentDirectory("newbie");
        var agentsContent = fs.File.ReadAllText(Path.Combine(path, "workspace", "AGENTS.md"));

        agentsContent.ShouldContain("memory/YYYY-MM-DD.md");
        agentsContent.ShouldContain("MEMORY.md");
    }

    [Fact]
    public void Initialize_WithSeparateDataDir_CreatesDirectoriesInDataPath()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath, DataDirPath);

        home.Initialize();

        fs.Directory.Exists(Path.Combine(DataDirPath, "extensions")).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(DataDirPath, "tokens")).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(DataDirPath, "sessions")).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(DataDirPath, "logs")).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(DataDirPath, "agents")).ShouldBeTrue();
        fs.Directory.Exists(Path.Combine(DataDirPath, "backups")).ShouldBeTrue();
        // RootPath directories should NOT be created
        fs.Directory.Exists(Path.Combine(HomePath, "extensions")).ShouldBeFalse();
    }

    [Fact]
    public void Initialize_WithSeparateDataDir_DoesNotCreateRootPathWhenItExists()
    {
        var fs = new MockFileSystem();
        // Pre-create root to simulate read-only mount
        fs.Directory.CreateDirectory(HomePath);
        var home = new BotNexusHome(fs, HomePath, DataDirPath);

        home.Initialize();

        // Should not throw and should not try to create subdirs in RootPath
        fs.Directory.Exists(DataDirPath).ShouldBeTrue();
    }

    [Fact]
    public void DataPath_DefaultsToRootPath_WhenNoOverrideSet()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath, dataPath: null);

        home.DataPath.ShouldBe(home.RootPath);
    }

    [Fact]
    public void DataPath_UsesOverride_WhenProvided()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath, DataDirPath);

        home.DataPath.ShouldBe(Path.GetFullPath(DataDirPath));
    }

    [Fact]
    public void AgentsPath_UsesDataPath_WhenSeparateDataDir()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath, DataDirPath);

        home.AgentsPath.ShouldBe(Path.Combine(Path.GetFullPath(DataDirPath), "agents"));
    }

    [Fact]
    public void GetAgentDirectory_WithSeparateDataDir_CreatesAgentInDataPath()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath, DataDirPath);

        var path = home.GetAgentDirectory("test-agent");

        path.ShouldStartWith(Path.GetFullPath(DataDirPath));
        fs.Directory.Exists(Path.Combine(path, "workspace")).ShouldBeTrue();
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
