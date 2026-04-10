using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Tests;

public sealed class FileAgentWorkspaceManagerTests : IDisposable
{
    private readonly string _homePath;
    private readonly FileAgentWorkspaceManager _workspaceManager;

    public FileAgentWorkspaceManagerTests()
    {
        _homePath = Path.Combine(Path.GetTempPath(), "botnexus-workspace-tests", Guid.NewGuid().ToString("N"));
        _workspaceManager = new FileAgentWorkspaceManager(new BotNexusHome(_homePath));
    }

    [Fact]
    public async Task LoadWorkspaceAsync_WhenMissing_CreatesWorkspaceAndReturnsEmptyFiles()
    {
        var workspace = await _workspaceManager.LoadWorkspaceAsync("farnsworth");

        workspace.AgentName.Should().Be("farnsworth");
        workspace.Soul.Should().Contain("# Soul");
        workspace.Identity.Should().Contain("# Identity");
        workspace.User.Should().Contain("# User");
        workspace.Memory.Should().BeEmpty();

        var workspacePath = _workspaceManager.GetWorkspacePath("farnsworth");
        File.Exists(Path.Combine(workspacePath, "AGENTS.md")).Should().BeTrue();
        File.Exists(Path.Combine(workspacePath, "SOUL.md")).Should().BeTrue();
        File.Exists(Path.Combine(workspacePath, "TOOLS.md")).Should().BeTrue();
        File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).Should().BeTrue();
        File.Exists(Path.Combine(workspacePath, "IDENTITY.md")).Should().BeTrue();
        File.Exists(Path.Combine(workspacePath, "USER.md")).Should().BeTrue();
        File.Exists(Path.Combine(workspacePath, "MEMORY.md")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveMemoryAsync_AppendsMemoryFile()
    {
        await _workspaceManager.SaveMemoryAsync("farnsworth", "first line");
        await _workspaceManager.SaveMemoryAsync("farnsworth", "second line");

        var memoryPath = Path.Combine(_workspaceManager.GetWorkspacePath("farnsworth"), "MEMORY.md");
        var content = await File.ReadAllTextAsync(memoryPath);

        content.Should().Contain("first line");
        content.Should().Contain("second line");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (!Directory.Exists(_homePath))
            return;

        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(_homePath, recursive: true);
                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(50);
            }
            catch
            {
                break;
            }
        }
    }
}
