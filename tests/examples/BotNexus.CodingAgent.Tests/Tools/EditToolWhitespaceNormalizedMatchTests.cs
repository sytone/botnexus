using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Tests for issue #2421. The #1555 diagnostic already proved the tool can locate the intended
/// text and know the only difference is leading/trailing whitespace or invisible characters
/// (ANSI escapes, NBSP, zero-width, CR, em-dash encoding). Rather than forcing the caller into a
/// full extra re-read round trip for a match it has already found, the existing normalized
/// matcher now also folds leading indentation, ANSI escape sequences and zero-width characters,
/// and applies the edit against the file's real text when - and only when - the normalized
/// comparison yields exactly one candidate. Ambiguity and genuine content drift must still fail.
/// </summary>
public sealed class EditToolWhitespaceNormalizedMatchTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "edit-normalized-match");
    private readonly MockFileSystem _fileSystem = new();
    private readonly EditTool _tool;

    public EditToolWhitespaceNormalizedMatchTests()
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

    [Fact]
    public async Task ExecuteAsync_WhenOldTextHasExtraLeadingIndentation_AppliesEdit()
    {
        const string content = "class C\n{\nreturn compute(value);\n}";

        var updated = await ApplyAsync("leading-indent.txt", content, "\treturn compute(value);", "return compute(other);");

        updated.ShouldBe("class C\n{\nreturn compute(other);\n}");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFileHasIndentationOldTextLacks_AppliesEditAndPreservesIndent()
    {
        const string content = "class C\n{\n    return compute(value);\n}";

        var updated = await ApplyAsync("file-indent.txt", content, "return compute(value);", "return compute(other);");

        // The indentation belongs to the file, not the matched span, so it survives untouched.
        updated.ShouldBe("class C\n{\n    return compute(other);\n}");
    }

    [Fact]
    public async Task ExecuteAsync_WhenOldTextHasTrailingWhitespace_AppliesEdit()
    {
        const string content = "alpha\nbeta\ngamma";

        var updated = await ApplyAsync("trailing.txt", content, "beta   ", "delta");

        updated.ShouldBe("alpha\ndelta\ngamma");
    }

    [Fact]
    public async Task ExecuteAsync_WhenOldTextCarriesAnsiEscapeCodes_AppliesEdit()
    {
        const string content = "before\nWrite-ScanLog \"done\"\nafter";
        // Classic pasted-shell-output failure: a colour code wraps the anchor text.
        const string oldText = "\u001b[32mWrite-ScanLog \"done\"\u001b[0m";

        var updated = await ApplyAsync("ansi.txt", content, oldText, "Write-ScanLog \"finished\"");

        updated.ShouldBe("before\nWrite-ScanLog \"finished\"\nafter");
    }

    [Fact]
    public async Task ExecuteAsync_WhenOldTextCarriesNonBreakingSpace_AppliesEdit()
    {
        const string content = "before\nlet total = 1\nafter";
        const string oldText = "let\u00A0total = 1";

        var updated = await ApplyAsync("nbsp.txt", content, oldText, "let total = 2");

        updated.ShouldBe("before\nlet total = 2\nafter");
    }

    [Fact]
    public async Task ExecuteAsync_WhenOldTextCarriesZeroWidthCharacters_AppliesEdit()
    {
        const string content = "before\nconst value = 42;\nafter";
        // Zero-width space and zero-width non-joiner smuggled in by a copy/paste.
        const string oldText = "const\u200B value\u200C = 42;";

        var updated = await ApplyAsync("zero-width.txt", content, oldText, "const value = 43;");

        updated.ShouldBe("before\nconst value = 43;\nafter");
    }

    [Fact]
    public async Task ExecuteAsync_WhenOldTextUsesCarriageReturns_AppliesEdit()
    {
        const string content = "alpha\nbeta\ngamma";

        var updated = await ApplyAsync("crlf.txt", content, "alpha\r\nbeta", "one\ntwo");

        updated.ShouldBe("one\ntwo\ngamma");
    }

    [Fact]
    public async Task ExecuteAsync_WhenOldTextUsesEmDashInsteadOfHyphen_AppliesEdit()
    {
        const string content = "intro\n### 4e - Open PR\noutro";
        const string oldText = "### 4e \u2014 Open PR";

        var updated = await ApplyAsync("emdash.txt", content, oldText, "### 4f - Open PR");

        updated.ShouldBe("intro\n### 4f - Open PR\noutro");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNormalizationYieldsMultipleCandidates_StillFails()
    {
        // Both lines normalize to the same text once indentation is folded. Ambiguity is a hard
        // stop: the tool must never silently pick one.
        const string content = "if (a)\n    return compute(value);\nelse\nreturn compute(value);\n";

        var ex = await ExpectFailureAsync("ambiguous.txt", content, "\treturn compute(value);", "CHANGED");

        ex.Message.ShouldContain("Expected exactly one match");
        ex.Message.ShouldContain("found 2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenExactTextIsAmbiguous_StillFails()
    {
        const string content = "return x;\nreturn x;\n";

        var ex = await ExpectFailureAsync("exact-ambiguous.txt", content, "return x;", "CHANGED");

        ex.Message.ShouldContain("found 2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenWordsGenuinelyDiffer_StillFails()
    {
        // Normalization must not degrade into a general fuzzy match: a real content difference
        // is stale-read drift and must still be reported, with the #1555 near-match diagnostic.
        const string content = "line one\nthe quick brown fox\nline three";

        var ex = await ExpectFailureAsync("content-drift.txt", content, "the quick brown lazy fox", "CHANGED");

        ex.Message.ShouldContain("found 0");
        ex.Message.ShouldContain("closest");
    }

    [Fact]
    public async Task ExecuteAsync_WhenInteriorWhitespaceDiffers_StillFails()
    {
        // Interior whitespace collapsing is deliberately out of scope - it is a riskier class
        // than leading/trailing trim plus invisible-character stripping.
        const string content = "before\nvar total = 1;\nafter";

        var ex = await ExpectFailureAsync("interior.txt", content, "var      total = 1;", "CHANGED");

        ex.Message.ShouldContain("found 0");
    }

    [Fact]
    public async Task ExecuteAsync_WhenTextMatchesExactly_AppliesEditUnchanged()
    {
        // The exact path must remain byte-identical in behaviour and take priority.
        const string content = "alpha\n    beta\ngamma";

        var updated = await ApplyAsync("exact.txt", content, "    beta", "    delta");

        updated.ShouldBe("alpha\n    delta\ngamma");
    }

    [Fact]
    public async Task ExecuteAsync_WhenExactMatchWouldBeAmbiguousUnderNormalization_PrefersExactMatch()
    {
        // "    beta" appears once exactly, but folding indentation would make it ambiguous with
        // the unindented "beta". The exact match wins, so the edit still applies deterministically.
        const string content = "beta\n    beta\ngamma";

        var updated = await ApplyAsync("exact-priority.txt", content, "    beta", "    delta");

        updated.ShouldBe("beta\n    delta\ngamma");
    }
}
