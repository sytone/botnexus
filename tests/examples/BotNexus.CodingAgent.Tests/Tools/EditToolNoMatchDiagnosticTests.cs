using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Tests for the actionable 0-match diagnostic added for issue #1555. When an
/// <c>edit</c>'s <c>oldText</c> matches nothing (after the existing fuzzy pass), the tool
/// now returns the nearest line in the file and, when that line is identical once
/// leading/trailing whitespace is ignored, says the difference is indentation/invisible
/// characters and to re-read the file — instead of a bare "found 0" with no hint about why
/// or what almost matched.
/// </summary>
public sealed class EditToolNoMatchDiagnosticTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "edit-diag");
    private readonly MockFileSystem _fileSystem = new();
    private readonly EditTool _tool;

    public EditToolNoMatchDiagnosticTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new EditTool(_tempDirectory, _fileSystem);
    }

    private async Task<InvalidOperationException> RunNoMatchAsync(string fileName, string fileContent, string oldText)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        await _fileSystem.File.WriteAllTextAsync(filePath, fileContent);

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = fileName,
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = oldText,
                    ["newText"] = "REPLACEMENT"
                }
            }
        });

        return await action.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoMatch_StillReportsFoundZero()
    {
        // Back-compat: the "found 0" token must remain in the message.
        var ex = await RunNoMatchAsync("plain-miss.txt", "the quick brown fox", "totally absent text");

        ex.Message.ShouldContain("found 0");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoMatch_IncludesNearestLineAndLineNumber()
    {
        const string content = "line one\nthe quick brown fox\nline three";
        // Same words, but an extra word breaks the exact + fuzzy match.
        var ex = await RunNoMatchAsync("nearest.txt", content, "the quick brown lazy fox");

        ex.Message.ShouldContain("found 0");
        ex.Message.ShouldContain("closest");
        ex.Message.ShouldContain("line 2");
        ex.Message.ShouldContain("the quick brown fox");
    }

    [Fact]
    public async Task ExecuteAsync_WhenBlockMissesOnLaterLine_NamesTheFirstDifferingLineNotWhitespace()
    {
        // Issue #2421 changed the single-line leading-indent case: it now *applies* instead of
        // erroring, so this test was migrated to keep covering the miss diagnostic on a block that
        // genuinely still misses. Issue #2907 then corrected WHAT it may claim: the anchor line
        // differs from the file only by indentation, but line 2 of oldText (`return second();`)
        // has no counterpart at all - the file's line 4 is `}`. Asserting a whitespace-only cause
        // here was an unverified claim that pointed the caller at the wrong line. The diagnostic
        // must now name the first line that ACTUALLY differs and stay silent about whitespace.
        const string content = "class C\n{\n    return compute(value);\n}";
        var ex = await RunNoMatchAsync("indent.txt", content, "return compute(value);\nreturn second();");

        ex.Message.ShouldContain("found 0");
        ex.Message.ShouldContain("line 3");
        ex.Message.ShouldContain("read");
        // The real drift is on line 2 of oldText, and the message must say so.
        ex.Message.ShouldContain("return second();");
        ex.Message.Contains("first line that actually differs", StringComparison.Ordinal)
            .ShouldBeTrue($"message should name the first differing line but was: {ex.Message}");
        // It must NOT assert a whitespace-only cause it has not verified.
        ex.Message.ShouldNotContain("differs only in leading/trailing whitespace");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultiLineOldTextMisses_AnchorsOnFirstNonEmptyLine()
    {
        const string content = "header\npublic void Run()\n{\n    DoWork();\n}";
        // First line of oldText is present (indent-shifted); the block as a whole misses.
        const string oldText = "public void Run()\n{\n    DoDifferentWork();\n}";
        var ex = await RunNoMatchAsync("block.txt", content, oldText);

        ex.Message.ShouldContain("found 0");
        ex.Message.ShouldContain("closest");
        ex.Message.ShouldContain("public void Run()");
    }
}
