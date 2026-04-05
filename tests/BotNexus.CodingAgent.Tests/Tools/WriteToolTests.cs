using System.Text;
using BotNexus.CodingAgent.Tools;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class WriteToolTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-writetool-{Guid.NewGuid():N}");
    private readonly WriteTool _tool;

    public WriteToolTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _tool = new WriteTool(_tempDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_WritesNewFile()
    {
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "new.txt",
            ["content"] = "hello world"
        });

        var fullPath = Path.Combine(_tempDirectory, "new.txt");
        File.Exists(fullPath).Should().BeTrue();
        (await File.ReadAllTextAsync(fullPath)).Should().Be("hello world");
        result.Content[0].Value.Should().Contain("Wrote 'new.txt'");
    }

    [Fact]
    public async Task ExecuteAsync_OverwritesExistingFile()
    {
        var fullPath = Path.Combine(_tempDirectory, "existing.txt");
        await File.WriteAllTextAsync(fullPath, "old");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "existing.txt",
            ["content"] = "updated"
        });

        (await File.ReadAllTextAsync(fullPath)).Should().Be("updated");
    }

    [Fact]
    public async Task ExecuteAsync_CreatesParentDirectories()
    {
        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = Path.Combine("deep", "nested", "file.txt"),
            ["content"] = "data"
        });

        var fullPath = Path.Combine(_tempDirectory, "deep", "nested", "file.txt");
        File.Exists(fullPath).Should().BeTrue();
        (await File.ReadAllTextAsync(fullPath)).Should().Be("data");
    }

    [Fact]
    public async Task ExecuteAsync_ReportsWrittenByteCount()
    {
        var content = "abc";

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "bytes.txt",
            ["content"] = content
        });

        var expectedBytes = Encoding.UTF8.GetByteCount(content);
        result.Content[0].Value.Should().Contain($"({expectedBytes} bytes)");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
