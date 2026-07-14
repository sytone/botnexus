using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Tests for the remediation diagnostics added in issue #1736. Issue #1555 hardened only the
/// 0-match case; this extends the same self-correction pattern to the OTHER failure shapes so
/// the model can fix its edit instead of retrying blind:
/// <list type="bullet">
/// <item>ambiguous match (found N greater than 1) -> the message reports the 1-based line numbers
/// of every match so the caller can add disambiguating context.</item>
/// <item>overlapping edits -> the message reports which two resolved edits overlap, their indices,
/// and their line ranges.</item>
/// <item>malformed entry (not an object / missing oldText) -> the message includes a correct-shape
/// example so the caller can re-issue a valid edits payload.</item>
/// </list>
/// Every assertion also pins the original message prefix so the back-compat tooling/tests that
/// match on "found N" / "must not overlap" / "must include oldText" keep passing.
/// </summary>
public sealed class EditToolRemediationDiagnosticTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "edit-remediation");
    private readonly MockFileSystem _fileSystem = new();
    private readonly EditTool _tool;

    public EditToolRemediationDiagnosticTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new EditTool(_tempDirectory, _fileSystem);
    }

    private async Task<InvalidOperationException> RunInvalidAsync(
        string fileName,
        string fileContent,
        params (string OldText, string NewText)[] edits)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        await _fileSystem.File.WriteAllTextAsync(filePath, fileContent);

        var editObjects = edits
            .Select(edit => (object)new Dictionary<string, object?>
            {
                ["oldText"] = edit.OldText,
                ["newText"] = edit.NewText
            })
            .ToArray();

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = fileName,
            ["edits"] = editObjects
        });

        return await action.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenExactMatchAmbiguous_ReportsMatchLineNumbers()
    {
        // "repeat" appears on line 2 and line 4 (exact, unambiguous count = 2).
        const string content = "alpha\nrepeat\nbeta\nrepeat\ngamma";
        var ex = await RunInvalidAsync("ambiguous-exact.txt", content, ("repeat", "once"));

        // Back-compat prefix preserved.
        ex.Message.ShouldContain("found 2");
        // New: 1-based line numbers of both match sites.
        ex.Message.ShouldContain("lines");
        ex.Message.ShouldContain("2");
        ex.Message.ShouldContain("4");
        // New: actionable guidance to disambiguate.
        ex.Message.ShouldContain("context");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFuzzyMatchAmbiguous_ReportsMatchLineNumbers()
    {
        // Straight-quote oldText misses exactly (file uses curly quotes) so the count == 0 exact
        // pass falls through to the fuzzy pass, which normalizes curly -> straight and finds two.
        const string content = "say \u201Chi\u201D\nsay \u201Chi\u201D";
        var ex = await RunInvalidAsync("ambiguous-fuzzy.txt", content, ("say \"hi\"", "say bye"));

        ex.Message.ShouldContain("found 2");
        ex.Message.ShouldContain("lines");
        ex.Message.ShouldContain("1");
        ex.Message.ShouldContain("2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenEditsOverlap_ReportsOverlappingEditIndicesAndRanges()
    {
        // Both oldText values are unique single matches but their resolved ranges overlap across
        // multiple lines: "AAAA\nBBBB\nCC" -> [0,12) (lines 1-3) and "BB\nCCCC\nDDDD" -> [7,19)
        // (lines 2-4).
        const string content = "AAAA\nBBBB\nCCCC\nDDDD";
        var ex = await RunInvalidAsync(
            "overlap.txt",
            content,
            ("AAAA\nBBBB\nCC", "X"),
            ("BB\nCCCC\nDDDD", "Y"));

        // Back-compat prefix preserved.
        ex.Message.ShouldContain("must not overlap");
        // New: identifies which two edits collide.
        ex.Message.ShouldContain("overlaps");
        ex.Message.ShouldContain("edits[0]");
        ex.Message.ShouldContain("edits[1]");
        // New: line ranges so the caller can see where (multi-line ranges use "lines X-Y").
        ex.Message.ShouldContain("lines");
    }

    [Fact]
    public async Task ExecuteAsync_WhenEntryMissingOldText_IncludesCorrectShapeExample()
    {
        var filePath = Path.Combine(_tempDirectory, "shape-missing-oldtext.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "content");

        // A JsonElement edits array whose only entry has no oldText -> "must include oldText".
        using var doc = System.Text.Json.JsonDocument.Parse("""{"edits": [{"newText": "x"}]}""");
        var editsElement = doc.RootElement.GetProperty("edits").Clone();

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "shape-missing-oldtext.txt",
            ["edits"] = editsElement
        });

        var ex = await action.ShouldThrowAsync<ArgumentException>();
        // Back-compat prefix preserved.
        ex.Message.ShouldContain("must include oldText");
        // New: a correct-shape example so the caller can re-issue a valid payload.
        ex.Message.ShouldContain("Expected shape:");
        ex.Message.ShouldContain("oldText");
        ex.Message.ShouldContain("newText");
    }

    [Fact]
    public async Task ExecuteAsync_WhenEntryNotAnObject_IncludesCorrectShapeExample()
    {
        var filePath = Path.Combine(_tempDirectory, "shape-not-object.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "content");

        // A JsonElement edits array whose entry is a bare string, not an object.
        using var doc = System.Text.Json.JsonDocument.Parse("""{"edits": ["not-an-object"]}""");
        var editsElement = doc.RootElement.GetProperty("edits").Clone();

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "shape-not-object.txt",
            ["edits"] = editsElement
        });

        var ex = await action.ShouldThrowAsync<ArgumentException>();
        // Back-compat prefix preserved.
        ex.Message.ShouldContain("must be an object");
        // New: a correct-shape example.
        ex.Message.ShouldContain("Expected shape:");
        ex.Message.ShouldContain("oldText");
        ex.Message.ShouldContain("newText");
    }
}
