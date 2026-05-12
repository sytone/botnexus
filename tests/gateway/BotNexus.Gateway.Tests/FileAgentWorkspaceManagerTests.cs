using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class FileAgentWorkspaceManagerTests : IDisposable
{
    private readonly string _homePath;
    private readonly FileAgentWorkspaceManager _workspaceManager;
    private readonly MockFileSystem _fileSystem;

    public FileAgentWorkspaceManagerTests()
    {
        _homePath = Path.Combine(Path.GetTempPath(), "botnexus", "workspace-tests");
        _fileSystem = new MockFileSystem();
        _workspaceManager = new FileAgentWorkspaceManager(new BotNexusHome(_fileSystem, _homePath), _fileSystem);
    }

    [Fact]
    public async Task LoadWorkspaceAsync_WhenMissing_CreatesWorkspaceAndReturnsEmptyFiles()
    {
        var workspace = await _workspaceManager.LoadWorkspaceAsync("farnsworth");

        workspace.AgentName.ShouldBe("farnsworth");
        workspace.Soul.ShouldContain("# Soul");
        workspace.Identity.ShouldContain("# Identity");
        workspace.User.ShouldContain("# User");
        workspace.Memory.ShouldBeEmpty();

        var workspacePath = _workspaceManager.GetWorkspacePath("farnsworth");
        _fileSystem.File.Exists(Path.Combine(workspacePath, "AGENTS.md")).ShouldBeTrue();
        _fileSystem.File.Exists(Path.Combine(workspacePath, "SOUL.md")).ShouldBeTrue();
        _fileSystem.File.Exists(Path.Combine(workspacePath, "TOOLS.md")).ShouldBeTrue();
        _fileSystem.File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).ShouldBeTrue();
        _fileSystem.File.Exists(Path.Combine(workspacePath, "IDENTITY.md")).ShouldBeTrue();
        _fileSystem.File.Exists(Path.Combine(workspacePath, "USER.md")).ShouldBeTrue();
        _fileSystem.File.Exists(Path.Combine(workspacePath, "MEMORY.md")).ShouldBeFalse();
    }

    [Fact]
    public async Task SaveMemoryAsync_LegacyOverload_AppendsToTodaysDailyMemoryFile()
    {
        await _workspaceManager.SaveMemoryAsync("farnsworth", "first line");
        await _workspaceManager.SaveMemoryAsync("farnsworth", "second line");

        var workspacePath = _workspaceManager.GetWorkspacePath("farnsworth");
        var dailyMemoryPath = Path.Combine(workspacePath, "memory", $"{DateTime.UtcNow:yyyy-MM-dd}.md");
        var content = await _fileSystem.File.ReadAllTextAsync(dailyMemoryPath);

        content.ShouldContain("first line");
        content.ShouldContain("second line");
        _fileSystem.File.Exists(Path.Combine(workspacePath, "MEMORY.md")).ShouldBeFalse();
    }

    [Fact]
    public async Task SaveMemoryAsync_WithRelativeMemoryPath_AppendsToRequestedMarkdownFile()
    {
        var overload = typeof(FileAgentWorkspaceManager).GetMethod(
            nameof(FileAgentWorkspaceManager.SaveMemoryAsync),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(string), typeof(string), typeof(string), typeof(CancellationToken)],
            modifiers: null);

        overload.ShouldNotBeNull("Wave 1 requires memory_save(file_path, content) support.");

        var task = (Task?)overload!.Invoke(
            _workspaceManager,
            ["farnsworth", @"memory\handoff.md", "first handoff entry", CancellationToken.None]);

        task.ShouldNotBeNull();
        await task!;

        var handoffPath = Path.Combine(_workspaceManager.GetWorkspacePath("farnsworth"), "memory", "handoff.md");
        var content = await _fileSystem.File.ReadAllTextAsync(handoffPath);
        content.ShouldContain("first handoff entry");
    }

    [Fact]
    public async Task SaveMemoryAsync_WithMemoryPathOverride_AppendsToOverrideLocation()
    {
        await _workspaceManager.SaveMemoryAsync(
            "farnsworth",
            filePath: null,
            content: "override entry",
            memoryPathOverride: @"journals\notes.md",
            cancellationToken: CancellationToken.None);

        var workspacePath = _workspaceManager.GetWorkspacePath("farnsworth");
        var overrideFilePath = Path.Combine(workspacePath, "journals", "notes.md");
        _fileSystem.File.Exists(overrideFilePath).ShouldBeTrue();
        var content = await _fileSystem.File.ReadAllTextAsync(overrideFilePath);
        content.ShouldContain("override entry");
    }

    [Fact]
    public void GetWorkspacePath_ForSubAgent_UsesTempWorkspaceRoot()
    {
        var subAgentName = "farnsworth--subagent--general--abc123";

        var workspacePath = _workspaceManager.GetWorkspacePath(subAgentName);
        var normalizedHome = Path.GetFullPath(_homePath);

        workspacePath.Replace('\\', '/').ShouldContain("botnexus-subagent-workspaces");
        Path.GetFullPath(workspacePath).StartsWith(normalizedHome, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [Fact]
    public void TryCleanupWorkspace_ForSubAgent_RemovesOwnedTempWorkspace()
    {
        var subAgentName = "farnsworth--subagent--general--cleanup";
        var workspacePath = _workspaceManager.GetWorkspacePath(subAgentName);
        _fileSystem.Directory.CreateDirectory(workspacePath);
        _fileSystem.File.WriteAllText(Path.Combine(workspacePath, "scratch.txt"), "test");

        var cleaned = _workspaceManager.TryCleanupWorkspace(subAgentName);

        cleaned.ShouldBeTrue();
        _fileSystem.Directory.Exists(Path.GetDirectoryName(workspacePath)!).ShouldBeFalse();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (_fileSystem.Directory.Exists(_homePath))
            _fileSystem.Directory.Delete(_homePath, recursive: true);
    }
}
