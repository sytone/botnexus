using System.Text;
using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class WriteToolTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "write");
    private readonly MockFileSystem _fileSystem = new();
    private readonly WriteTool _tool;

    public WriteToolTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new WriteTool(_tempDirectory, _fileSystem);
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
        _fileSystem.File.Exists(fullPath).ShouldBeTrue();
        (await _fileSystem.File.ReadAllTextAsync(fullPath)).ShouldBe("hello world");
        result.Content[0].Value.ShouldContain("Wrote 'new.txt'");
    }

    [Fact]
    public async Task ExecuteAsync_OverwritesExistingFile()
    {
        var fullPath = Path.Combine(_tempDirectory, "existing.txt");
        await _fileSystem.File.WriteAllTextAsync(fullPath, "old");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "existing.txt",
            ["content"] = "updated"
        });

        (await _fileSystem.File.ReadAllTextAsync(fullPath)).ShouldBe("updated");
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
        _fileSystem.File.Exists(fullPath).ShouldBeTrue();
        (await _fileSystem.File.ReadAllTextAsync(fullPath)).ShouldBe("data");
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
        result.Content[0].Value.ShouldContain($"({expectedBytes} bytes)");
    }

    [Fact]
    public async Task ExecuteAsync_WrittenFile_DoesNotStartWithUtf8Bom()
    {
        var fullPath = Path.Combine(_tempDirectory, "no-bom.txt");
        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "no-bom.txt",
            ["content"] = "plain text"
        });

        var bytes = await _fileSystem.File.ReadAllBytesAsync(fullPath);
        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF });
    }
}
