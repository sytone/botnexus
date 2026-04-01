using System.Text;
using BotNexus.Agent;
using BotNexus.Core.Configuration;
using FluentAssertions;

namespace BotNexus.Tests.Unit.Tests;

[Collection("BotNexusHomeEnvVar")]
public sealed class AgentWorkspaceTests : IDisposable
{
    private const string HomeOverrideEnvVar = "BOTNEXUS_HOME";
    private readonly string? _originalHomeOverride;
    private readonly string _testHomePath;
    private readonly AgentWorkspace _workspace;

    public AgentWorkspaceTests()
    {
        _originalHomeOverride = Environment.GetEnvironmentVariable(HomeOverrideEnvVar);
        _testHomePath = Path.Combine(Path.GetTempPath(), $"botnexus-workspace-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(HomeOverrideEnvVar, _testHomePath);

        BotNexusHome.Initialize();
        _workspace = new AgentWorkspace("bender");
    }

    [Fact]
    public async Task InitializeAsync_CreatesDirectoryStructureAndStubFiles()
    {
        await _workspace.InitializeAsync();

        Directory.Exists(_workspace.WorkspacePath).Should().BeTrue();
        Directory.Exists(Path.Combine(_workspace.WorkspacePath, "memory")).Should().BeTrue();
        Directory.Exists(Path.Combine(_workspace.WorkspacePath, "memory", "daily")).Should().BeTrue();

        _workspace.FileExists("SOUL.md").Should().BeTrue();
        _workspace.FileExists("IDENTITY.md").Should().BeTrue();
        _workspace.FileExists("USER.md").Should().BeTrue();
        _workspace.FileExists("MEMORY.md").Should().BeTrue();
        _workspace.FileExists("HEARTBEAT.md").Should().BeTrue();
        _workspace.FileExists("AGENTS.md").Should().BeFalse();
        _workspace.FileExists("TOOLS.md").Should().BeFalse();
    }

    [Fact]
    public async Task FileOperations_ReadWriteAppendListAndExists_WorkAsExpected()
    {
        await _workspace.InitializeAsync();
        await _workspace.WriteFileAsync("NOTES.md", "hello");
        await _workspace.AppendFileAsync("NOTES.md", " world");

        var content = await _workspace.ReadFileAsync("NOTES.md");
        var missing = await _workspace.ReadFileAsync("MISSING.md");
        var files = await _workspace.ListFilesAsync();

        content.Should().Be("hello world");
        missing.Should().BeNull();
        _workspace.FileExists("NOTES.md").Should().BeTrue();
        files.Should().Contain("NOTES.md");
        files.Should().OnlyContain(name => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WriteFileAsync_UsesUtf8Encoding()
    {
        await _workspace.InitializeAsync();
        await _workspace.WriteFileAsync("UTF8.md", "✓ café");

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_workspace.WorkspacePath, "UTF8.md"));
        Encoding.UTF8.GetString(bytes).Should().Be("✓ café");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeOverrideEnvVar, _originalHomeOverride);
        if (Directory.Exists(_testHomePath))
            Directory.Delete(_testHomePath, recursive: true);
    }
}
