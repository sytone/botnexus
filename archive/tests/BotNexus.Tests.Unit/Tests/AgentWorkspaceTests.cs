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
    public async Task InitializeAsync_CreatesDirectoryStructure()
    {
        await _workspace.InitializeAsync();

        Directory.Exists(_workspace.WorkspacePath).Should().BeTrue();
        Directory.Exists(Path.Combine(_workspace.WorkspacePath, "memory")).Should().BeTrue();
        Directory.Exists(Path.Combine(_workspace.WorkspacePath, "memory", "daily")).Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_CreatesExpectedStubFiles()
    {
        await _workspace.InitializeAsync();

        _workspace.FileExists("SOUL.md").Should().BeTrue();
        _workspace.FileExists("IDENTITY.md").Should().BeTrue();
        _workspace.FileExists("USER.md").Should().BeTrue();
        _workspace.FileExists("MEMORY.md").Should().BeTrue();
        _workspace.FileExists("HEARTBEAT.md").Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_CreatesAgentsAndToolsStubs()
    {
        await _workspace.InitializeAsync();

        _workspace.FileExists("AGENTS.md").Should().BeTrue();
        _workspace.FileExists("TOOLS.md").Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndDoesNotOverwriteExistingFiles()
    {
        await _workspace.InitializeAsync();

        await _workspace.WriteFileAsync("SOUL.md", "custom soul");
        await _workspace.WriteFileAsync("MEMORY.md", "custom memory");
        await _workspace.InitializeAsync();

        (await _workspace.ReadFileAsync("SOUL.md")).Should().Be("custom soul");
        (await _workspace.ReadFileAsync("MEMORY.md")).Should().Be("custom memory");
    }

    [Fact]
    public async Task ReadFileAsync_ReturnsContent_ForExistingFile()
    {
        await _workspace.WriteFileAsync("NOTES.md", "hello world");

        var content = await _workspace.ReadFileAsync("NOTES.md");

        content.Should().Be("hello world");
    }

    [Fact]
    public async Task ReadFileAsync_ReturnsNull_ForMissingFile()
    {
        var missing = await _workspace.ReadFileAsync("MISSING.md");

        missing.Should().BeNull();
    }

    [Fact]
    public async Task WriteFileAsync_CreatesNewFile()
    {
        await _workspace.WriteFileAsync("NOTES.md", "created");

        _workspace.FileExists("NOTES.md").Should().BeTrue();
        (await _workspace.ReadFileAsync("NOTES.md")).Should().Be("created");
    }

    [Fact]
    public async Task WriteFileAsync_OverwritesExistingFile()
    {
        await _workspace.WriteFileAsync("NOTES.md", "old");
        await _workspace.WriteFileAsync("NOTES.md", "new");

        (await _workspace.ReadFileAsync("NOTES.md")).Should().Be("new");
    }

    [Fact]
    public async Task AppendFileAsync_AppendsToExistingFile()
    {
        await _workspace.WriteFileAsync("NOTES.md", "hello");
        await _workspace.AppendFileAsync("NOTES.md", " world");

        (await _workspace.ReadFileAsync("NOTES.md")).Should().Be("hello world");
    }

    [Fact]
    public async Task AppendFileAsync_CreatesFileIfMissing()
    {
        await _workspace.AppendFileAsync("NEW.md", "first");

        _workspace.FileExists("NEW.md").Should().BeTrue();
        (await _workspace.ReadFileAsync("NEW.md")).Should().Be("first");
    }

    [Fact]
    public async Task ListFilesAsync_ReturnsMarkdownFilesOnly()
    {
        await _workspace.WriteFileAsync("A.md", "a");
        await _workspace.WriteFileAsync("B.MD", "b");
        await File.WriteAllTextAsync(Path.Combine(_workspace.WorkspacePath, "notes.txt"), "ignore");

        var files = await _workspace.ListFilesAsync();

        files.Should().Contain("A.md");
        files.Should().Contain("B.MD");
        files.Should().OnlyContain(name => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FileExists_ReturnsCorrectValues()
    {
        _workspace.FileExists("EXISTS.md").Should().BeFalse();
        await _workspace.WriteFileAsync("EXISTS.md", "x");
        _workspace.FileExists("EXISTS.md").Should().BeTrue();
        _workspace.FileExists("MISSING.md").Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_CreatesHelpfulPlaceholderContentInStubFiles()
    {
        await _workspace.InitializeAsync();

        var soul = await _workspace.ReadFileAsync("SOUL.md");
        var identity = await _workspace.ReadFileAsync("IDENTITY.md");
        var user = await _workspace.ReadFileAsync("USER.md");
        var heartbeat = await _workspace.ReadFileAsync("HEARTBEAT.md");
        var memory = await _workspace.ReadFileAsync("MEMORY.md");

        soul.Should().NotBeNullOrWhiteSpace();
        identity.Should().NotBeNullOrWhiteSpace();
        user.Should().NotBeNullOrWhiteSpace();
        heartbeat.Should().NotBeNullOrWhiteSpace();
        memory.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConcurrentReadWriteOperations_DoNotCorruptData()
    {
        await _workspace.WriteFileAsync("CONCURRENT.md", string.Empty);

        var appendTasks = Enumerable.Range(1, 100)
            .Select(i => _workspace.AppendFileAsync("CONCURRENT.md", $"line-{i:D3}{Environment.NewLine}"));

        var readTasks = Enumerable.Range(1, 40)
            .Select(_ => _workspace.ReadFileAsync("CONCURRENT.md"));

        await Task.WhenAll(appendTasks.Concat(readTasks));

        var finalContent = await _workspace.ReadFileAsync("CONCURRENT.md");
        finalContent.Should().NotBeNull();

        var lines = finalContent!
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(100);
        lines.Should().OnlyHaveUniqueItems();
        lines.Should().Contain("line-001");
        lines.Should().Contain("line-100");
    }

    [Fact]
    public async Task FileOperations_UseUtf8EncodingWithSpecialCharacters()
    {
        var text = "✓ café — こんにちは 👋";
        await _workspace.WriteFileAsync("UTF8.md", text);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_workspace.WorkspacePath, "UTF8.md"));
        Encoding.UTF8.GetString(bytes).Should().Be(text);
        (await _workspace.ReadFileAsync("UTF8.md")).Should().Be(text);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeOverrideEnvVar, _originalHomeOverride);
        if (Directory.Exists(_testHomePath))
            Directory.Delete(_testHomePath, recursive: true);
    }
}
