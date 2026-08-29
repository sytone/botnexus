using System.Text;
using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Covers the append affordance added for issue #3571. The defect these guard against is structural:
/// agents maintaining append-only logs had to fake an append with an anchored <c>edit</c>, and in a log
/// the anchor is boilerplate that recurs by design, so the edit failed with <c>found N</c> more often
/// every day. Each test names the acceptance clause it proves.
/// </summary>
public sealed class WriteToolAppendTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "write-append");
    private readonly MockFileSystem _fileSystem = new();
    private readonly WriteTool _tool;

    public WriteToolAppendTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new WriteTool(_tempDirectory, _fileSystem);
    }

    /// <summary>Clause 1: a single call appends to an existing file with no anchor of any kind.</summary>
    [Fact]
    public async Task ExecuteAsync_Append_AddsToEndOfExistingFileWithoutAnAnchor()
    {
        var fullPath = Path.Combine(_tempDirectory, "log.md");
        await _fileSystem.File.WriteAllTextAsync(fullPath, "existing\n");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "log.md",
            ["content"] = "appended\n",
            ["append"] = true
        });

        (await _fileSystem.File.ReadAllTextAsync(fullPath)).ShouldBe("existing\nappended\n");
        result.Content[0].Value.ShouldContain("Appended");
        result.Content[0].Value.ShouldContain("log.md");
    }

    /// <summary>
    /// Clause 2 and clause 6: the file carries eleven identical anchor lines — the exact worst case
    /// measured in the corpus, where an anchored edit failed with "found 11" — and the append still
    /// succeeds, because it consults no anchor at all.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Append_SucceedsWhenTheAppendedTextRecursElevenTimes()
    {
        const string boilerplate = "**Noise candidates:** none new.\n";
        var fullPath = Path.Combine(_tempDirectory, "repetitive.md");
        await _fileSystem.File.WriteAllTextAsync(fullPath, string.Concat(Enumerable.Repeat(boilerplate, 11)));

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "repetitive.md",
            ["content"] = boilerplate,
            ["append"] = true
        });

        var text = await _fileSystem.File.ReadAllTextAsync(fullPath);
        CountOccurrences(text, boilerplate).ShouldBe(12);
    }

    /// <summary>Clause 3: appending to a path that does not exist creates it (and its parents).</summary>
    [Fact]
    public async Task ExecuteAsync_Append_CreatesMissingFileAndParentDirectories()
    {
        var relative = Path.Combine("logs", "nested", "new.md");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = relative,
            ["content"] = "first entry\n",
            ["append"] = true
        });

        var fullPath = Path.Combine(_tempDirectory, relative);
        _fileSystem.File.Exists(fullPath).ShouldBeTrue();
        (await _fileSystem.File.ReadAllTextAsync(fullPath)).ShouldBe("first entry\n");
    }

    /// <summary>
    /// Clause 4: the appended bytes are exactly the supplied text. This is the non-vacuous form of the
    /// claim — the original prefix must be byte-identical and the tail must equal the content exactly,
    /// so neither re-emission nor a silently injected separator can pass.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Append_WritesExactlyTheSuppliedBytesAndNothingElse()
    {
        const string original = "line one\nline two";
        const string addition = "no separator was added";
        var fullPath = Path.Combine(_tempDirectory, "bytes.md");
        await _fileSystem.File.WriteAllTextAsync(fullPath, original);

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "bytes.md",
            ["content"] = addition,
            ["append"] = true
        });

        (await _fileSystem.File.ReadAllTextAsync(fullPath)).ShouldBe(original + addition);
        result.Content[0].Value.ShouldContain($"{Encoding.UTF8.GetByteCount(addition)} bytes");
    }

    /// <summary>Append must not regress the default: absent or false, the tool still overwrites.</summary>
    [Fact]
    public async Task ExecuteAsync_AppendFalseOrAbsent_StillOverwritesTheWholeFile()
    {
        var fullPath = Path.Combine(_tempDirectory, "overwrite.md");
        await _fileSystem.File.WriteAllTextAsync(fullPath, "old content");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "overwrite.md",
            ["content"] = "new",
            ["append"] = false
        });

        (await _fileSystem.File.ReadAllTextAsync(fullPath)).ShouldBe("new");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "overwrite.md",
            ["content"] = "newer"
        });

        (await _fileSystem.File.ReadAllTextAsync(fullPath)).ShouldBe("newer");
    }

    /// <summary>
    /// Providers serialize booleans inconsistently; a string "true" must still append rather than
    /// silently overwriting the caller's log, which would be data loss rather than a failed call.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AppendAsStringTrue_IsTreatedAsAppend()
    {
        var fullPath = Path.Combine(_tempDirectory, "coerce.md");
        await _fileSystem.File.WriteAllTextAsync(fullPath, "kept\n");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "coerce.md",
            ["content"] = "added\n",
            ["append"] = "true"
        });

        (await _fileSystem.File.ReadAllTextAsync(fullPath)).ShouldBe("kept\nadded\n");
    }

    /// <summary>The append flag must survive argument preparation, or execution never sees it.</summary>
    [Fact]
    public async Task PrepareArgumentsAsync_PreservesTheAppendFlag()
    {
        var prepared = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["path"] = "a.md",
            ["content"] = "x",
            ["append"] = true
        });

        prepared["append"].ShouldBe(true);
    }

    /// <summary>The schema must advertise append, or no agent will ever discover the affordance.</summary>
    [Fact]
    public void Definition_AdvertisesTheAppendParameter()
    {
        var schema = _tool.Definition.Parameters.ToString();

        schema.ShouldContain("append");
        _tool.Definition.Description.ShouldContain("append");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
