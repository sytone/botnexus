using System.Text;
using BotNexus.Tools;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Security;

public sealed class FileToolSecurityTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-file-security-{Guid.NewGuid():N}");
    private readonly ReadTool _readTool;
    private readonly WriteTool _writeTool;
    private readonly EditTool _editTool;
    private readonly GlobTool _globTool;

    public FileToolSecurityTests()
    {
        Directory.CreateDirectory(_workingDirectory);
        _readTool = new ReadTool(_workingDirectory);
        _writeTool = new WriteTool(_workingDirectory);
        _editTool = new EditTool(_workingDirectory);
        _globTool = new GlobTool(_workingDirectory);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadTool_PathTraversal_IsBlocked()
    {
        var act = () => _readTool.ExecuteAsync("t1", new Dictionary<string, object?> { ["path"] = "..\\..\\etc\\passwd" });
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WriteTool_PathTraversal_IsBlocked()
    {
        var act = () => _writeTool.ExecuteAsync("t1", new Dictionary<string, object?> { ["path"] = "..\\outside\\malicious.txt", ["content"] = "x" });
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task EditTool_PathOutsideWorkspace_IsBlocked()
    {
        var externalPath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(externalPath, "content");
        try
        {
            var act = () => _editTool.ExecuteAsync("t1", new Dictionary<string, object?>
            {
                ["path"] = externalPath,
                ["edits"] = new object[]
                {
                    new Dictionary<string, object?> { ["oldText"] = "content", ["newText"] = "updated" }
                }
            });
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            if (File.Exists(externalPath))
            {
                File.Delete(externalPath);
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AbsolutePathEscape_IsBlocked()
    {
        var escaped = OperatingSystem.IsWindows() ? @"C:\Windows\System32\config\SAM" : "/etc/passwd";
        var act = () => _readTool.ExecuteAsync("t1", new Dictionary<string, object?> { ["path"] = escaped });
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PathWithNullByte_IsRejected()
    {
        var act = () => _writeTool.ExecuteAsync("t1", new Dictionary<string, object?> { ["path"] = "file\0.txt", ["content"] = "x" });
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task UnicodeNormalizationPathVariant_ResolvesToExistingFile_CurrentBehavior()
    {
        const string composed = "café.txt";
        var decomposed = "cafe\u0301.txt";
        await File.WriteAllTextAsync(Path.Combine(_workingDirectory, composed), "ok");

        var act = () => _readTool.ExecuteAsync("t1", new Dictionary<string, object?> { ["path"] = decomposed });
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GlobTraversalPattern_RemainsConfinedToWorkspace()
    {
        await File.WriteAllTextAsync(Path.Combine(_workingDirectory, "safe.txt"), "ok");
        var act = () => _globTool.ExecuteAsync("t1", new Dictionary<string, object?> { ["pattern"] = "**/../../../etc/*" });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VeryLongFilePath_ReturnsErrorNotCrash()
    {
        var veryLongPath = $"{new string('a', 32_768)}.txt";
        var act = () => _writeTool.ExecuteAsync("t1", new Dictionary<string, object?> { ["path"] = veryLongPath, ["content"] = "x" });
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task WriteTool_VeryLargeContent_IsCurrentlyAllowed()
    {
        var largeContent = new string('x', 5 * 1024 * 1024);
        var result = await _writeTool.ExecuteAsync("t1", new Dictionary<string, object?> { ["path"] = "large.txt", ["content"] = largeContent });

        var written = await File.ReadAllTextAsync(Path.Combine(_workingDirectory, "large.txt"), Encoding.UTF8);
        written.Length.Should().Be(largeContent.Length);
        result.Content[0].Value.Should().Contain("Wrote 'large.txt'");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
