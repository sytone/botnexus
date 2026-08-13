using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Tests for issue #2907. #2421 shipped whitespace normalisation that is already line-wise, so a
/// multi-line <c>oldText</c> whose interior trailing whitespace or line endings drift does match.
/// What did NOT hold is the failure path: the no-match diagnostic anchored on the FIRST line of
/// <c>oldText</c> only, and asserted "It differs only in leading/trailing whitespace or invisible
/// characters" whenever that single line trimmed-equal - an unverified claim that actively sent
/// callers to the wrong line when the real difference was on line 2 or later.
///
/// These tests pin both clauses: the multi-line normalised match APPLIES against original bytes,
/// and when it does not match the error names the FIRST LINE THAT ACTUALLY DIFFERS and only
/// claims a whitespace-only difference when that is true for every line.
/// </summary>
public sealed class EditToolMultiLineNormalizedMatchTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "edit-multiline-match");
    private readonly MockFileSystem _fileSystem = new();
    private readonly EditTool _tool;

    public EditToolMultiLineNormalizedMatchTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new EditTool(_tempDirectory, _fileSystem);
    }

    private async Task<string> ApplyAsync(string fileName, string fileContent, string oldText, string newText)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        await _fileSystem.File.WriteAllTextAsync(filePath, fileContent);

        var result = await _tool.ExecuteAsync("test-call", BuildArguments(fileName, oldText, newText));

        result.Content[0].Value.ShouldContain("Successfully replaced");
        return await _fileSystem.File.ReadAllTextAsync(filePath);
    }

    private async Task<InvalidOperationException> ExpectFailureAsync(
        string fileName,
        string fileContent,
        string oldText,
        string newText)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        await _fileSystem.File.WriteAllTextAsync(filePath, fileContent);

        var action = () => _tool.ExecuteAsync("test-call", BuildArguments(fileName, oldText, newText));
        var ex = await action.ShouldThrowAsync<InvalidOperationException>();

        // A failed edit must never mutate the file.
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe(fileContent);
        return ex;
    }

    private static Dictionary<string, object?> BuildArguments(string fileName, string oldText, string newText)
    {
        return new Dictionary<string, object?>
        {
            ["path"] = fileName,
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = oldText,
                    ["newText"] = newText
                }
            }
        };
    }

    // ---------------------------------------------------------------------------------------
    // Clause 1 - multi-line normalised match applies against the ORIGINAL bytes.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_WhenMultiLineOldTextHasInteriorTrailingWhitespace_AppliesEdit()
    {
        // The file carries trailing spaces on the FIRST line of the block; oldText does not.
        // This is the exact shape from the issue's surviving repro.
        const string content = "intro\n**Refreshed:** 2026-07-31   \n**Source:** origin/main\noutro";
        const string oldText = "**Refreshed:** 2026-07-31\n**Source:** origin/main";

        var updated = await ApplyAsync("interior-trailing.md", content, oldText, "**Refreshed:** now\n**Source:** local");

        updated.ShouldBe("intro\n**Refreshed:** now\n**Source:** local\noutro");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultiLineOldTextUsesCrlfAndFileUsesLf_AppliesEdit()
    {
        const string content = "intro\nalpha\nbeta\noutro";
        const string oldText = "alpha\r\nbeta";

        var updated = await ApplyAsync("crlf-block.txt", content, oldText, "one\ntwo");

        updated.ShouldBe("intro\none\ntwo\noutro");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultiLineOldTextCombinesTrailingWhitespaceAndCrlf_AppliesEdit()
    {
        // AC1 verbatim: trailing spaces on line 1 AND a \r\n line ending on line 2.
        const string content = "intro\nalpha\nbeta\noutro";
        const string oldText = "alpha   \r\nbeta";

        var updated = await ApplyAsync("ac1.txt", content, oldText, "one\ntwo");

        updated.ShouldBe("intro\none\ntwo\noutro");
    }

    [Fact]
    public async Task ExecuteAsync_WhenThreeLineOldTextCarriesBomAndNonBreakingSpace_AppliesEdit()
    {
        const string content = "head\nlet a = 1\nlet b = 2\nlet c = 3\ntail";
        const string oldText = "let\u00A0a = 1\n\uFEFFlet b = 2\nlet c = 3";

        var updated = await ApplyAsync("three-line.txt", content, oldText, "let a = 9\nlet b = 9\nlet c = 9");

        updated.ShouldBe("head\nlet a = 9\nlet b = 9\nlet c = 9\ntail");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultiLineMatchApplies_PreservesFileIndentationOutsideTheSpan()
    {
        // The matched span is located by normalised comparison but replaced against ORIGINAL bytes,
        // so the file's own leading indentation is outside the span and survives untouched.
        const string content = "class C\n{\n    var a = 1;\n    var b = 2;\n}";
        const string oldText = "var a = 1;\nvar b = 2;";

        var updated = await ApplyAsync("indent-block.cs", content, oldText, "var a = 9;\n    var b = 9;");

        updated.ShouldBe("class C\n{\n    var a = 9;\n    var b = 9;\n}");
    }

    // ---------------------------------------------------------------------------------------
    // Clause 2 - the error names the FIRST LINE THAT ACTUALLY DIFFERS and never asserts an
    // unverified whitespace-only difference.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_WhenSecondLineGenuinelyDiffers_NamesTheFirstDifferingLine()
    {
        const string content = "intro\nalpha\nbeta\noutro";
        const string oldText = "alpha\nBRAVO";

        var ex = await ExpectFailureAsync("second-line-diff.txt", content, oldText, "CHANGED");

        ex.Message.ShouldContain("found 0");
        // The first line matches, so the diagnostic must point at line 2 of oldText.
        ex.Message.Contains("first line that actually differs", StringComparison.Ordinal)
            .ShouldBeTrue($"message should name the first differing line but was: {ex.Message}");
        ex.Message.ShouldContain("oldText line 2");
        ex.Message.ShouldContain("BRAVO");
        ex.Message.ShouldContain("beta");
        // It must NOT claim a whitespace-only difference it has not verified.
        ex.Message.ShouldNotContain("differs only in leading/trailing whitespace");
    }

    [Fact]
    public async Task ExecuteAsync_WhenThirdLineGenuinelyDiffers_NamesThatLineNotTheFirst()
    {
        const string content = "head\nlet a = 1\nlet b = 2\nlet c = 3\ntail";
        const string oldText = "let a = 1\nlet b = 2\nlet ZZZ = 3";

        var ex = await ExpectFailureAsync("third-line-diff.txt", content, oldText, "CHANGED");

        ex.Message.ShouldContain("found 0");
        ex.Message.ShouldContain("ZZZ");
        ex.Message.ShouldNotContain("differs only in leading/trailing whitespace");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultiLineNormalizationYieldsMultipleCandidates_StillFails()
    {
        // Two blocks that BOTH only match after normalisation (each carries different trailing
        // whitespace), so no exact match can break the tie. Ambiguity is a hard stop: the tool
        // must never silently pick one.
        const string content = "alpha   \nbeta\nmiddle\nalpha\t\nbeta\n";
        const string oldText = "alpha\nbeta";

        var ex = await ExpectFailureAsync("ambiguous-block.txt", content, oldText, "CHANGED");

        ex.Message.ShouldContain("Expected exactly one match");
        ex.Message.ShouldContain("found 2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSingleLineDiffersOnlyByInvisibleCharacters_StillReportsWhitespaceCause()
    {
        // Regression pin for the #2421/#1555 behaviour: the diagnostic must still locate the
        // closest line and refuse the edit. A hard tab vs spaces is INTERIOR whitespace, which
        // normalisation deliberately does not fold, so this is a genuine difference on line 1 -
        // and #2907 means the message now names that line instead of asserting a
        // leading/trailing-whitespace cause that does not apply here.
        const string content = "before\nvar\ttotal = 1;\nafter";
        const string oldText = "var        total = 1;";

        var ex = await ExpectFailureAsync("interior-ws.txt", content, oldText, "CHANGED");

        ex.Message.ShouldContain("found 0");
        ex.Message.ShouldContain("closest");
    }
}
