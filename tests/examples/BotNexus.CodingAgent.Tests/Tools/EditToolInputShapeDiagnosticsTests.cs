using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Issue #2690: <c>edit</c> is the highest-volume erroring tool in the fleet and every dominant
/// failure shape is an avoidable input-construction error. These tests pin the three classes the
/// issue measured:
/// <list type="number">
/// <item>ANSI/control escape sequences pasted into <c>oldText</c> from shell output still match the
/// clean file text, and the result says the escapes were normalised.</item>
/// <item>A non-unique <c>oldText</c> failure reports the match count <em>and</em> a closest-text
/// excerpt, matching the 0-match diagnostic that already did so.</item>
/// <item>A stringified <c>edits</c> array is rejected with a message that names the
/// stringification, and is never applied to the file.</item>
/// </list>
/// </summary>
public sealed class EditToolInputShapeDiagnosticsTests
{
    private const string Esc = "\u001B";

    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "edit-2690");
    private readonly MockFileSystem _fileSystem = new();
    private readonly EditTool _tool;

    public EditToolInputShapeDiagnosticsTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new EditTool(_tempDirectory, _fileSystem);
    }

    private async Task<string> WriteFileAsync(string fileName, string content)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        await _fileSystem.File.WriteAllTextAsync(filePath, content);
        return filePath;
    }

    private Task<AgentToolResult> EditAsync(string fileName, string oldText, string newText)
    {
        return _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
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
        });
    }

    // --- Acceptance criterion 1: ANSI escapes in oldText ------------------------------------

    [Fact]
    public async Task ExecuteAsync_WhenOldTextCarriesAnsiEscapes_MatchesCleanFileTextAndApplies()
    {
        const string content = "alpha\nvar total = ComputeTotal(items);\nomega";
        var filePath = await WriteFileAsync("ansi-apply.txt", content);

        // The shape a caller produces by pasting coloured shell output into oldText.
        var oldText = $"var {Esc}[32mtotal{Esc}[0m = ComputeTotal(items);";

        await EditAsync("ansi-apply.txt", oldText, "var total = ComputeTotal(all);");

        var updated = await _fileSystem.File.ReadAllTextAsync(filePath);
        updated.ShouldContain("ComputeTotal(all)");
        updated.ShouldNotContain(Esc);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOldTextCarriesAnsiEscapes_ResultStatesEscapesWereNormalized()
    {
        await WriteFileAsync("ansi-message.txt", "alpha\nvar total = ComputeTotal(items);\nomega");

        var oldText = $"var {Esc}[32mtotal{Esc}[0m = ComputeTotal(items);";
        var result = await EditAsync("ansi-message.txt", oldText, "var total = ComputeTotal(all);");

        var text = string.Join("\n", result.Content.Select(c => c.Value));
        text.ShouldContain("escape", Case.Insensitive);
        text.ShouldContain("normal", Case.Insensitive);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOldTextHasNoAnsiEscapes_ResultDoesNotClaimNormalization()
    {
        await WriteFileAsync("ansi-clean.txt", "alpha\nvar total = ComputeTotal(items);\nomega");

        var result = await EditAsync(
            "ansi-clean.txt",
            "var total = ComputeTotal(items);",
            "var total = ComputeTotal(all);");

        var text = string.Join("\n", result.Content.Select(c => c.Value));
        text.ShouldNotContain("escape sequence");
    }

    // --- Acceptance criterion 2: closest-text excerpt on the ambiguous case ------------------

    [Fact]
    public async Task ExecuteAsync_WhenExactMatchIsAmbiguous_ReportsCountAndClosestTextExcerpt()
    {
        const string content = "log(\"x\");\nfiller\nlog(\"x\");\n";
        await WriteFileAsync("ambiguous-exact.txt", content);

        var action = () => EditAsync("ambiguous-exact.txt", "log(\"x\");", "log(\"y\");");
        var ex = await action.ShouldThrowAsync<InvalidOperationException>();

        ex.Message.ShouldContain("found 2");
        // The 0-match diagnostic already gives a "closest text ... at line N" excerpt; the
        // ambiguous case must give the same anchoring so the caller can widen oldText.
        ex.Message.ShouldContain("closest");
        ex.Message.ShouldContain("log(\"x\");");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFuzzyMatchIsAmbiguous_ReportsCountAndClosestTextExcerpt()
    {
        // Differs from the file only by indentation, so the exact pass finds 0 and the fuzzy
        // pass finds 2.
        const string content = "    value = compute();\nfiller\n        value = compute();\n";
        await WriteFileAsync("ambiguous-fuzzy.txt", content);

        var action = () => EditAsync("ambiguous-fuzzy.txt", "value = compute();", "value = compute(2);");
        var ex = await action.ShouldThrowAsync<InvalidOperationException>();

        ex.Message.ShouldContain("found 2");
        ex.Message.ShouldContain("closest");
        ex.Message.ShouldContain("value = compute();");
    }

    // --- Acceptance criteria 3 and 5: stringified edits stays rejected -----------------------

    // NOTE (issue #2759): the two tests below previously asserted that a WELL-FORMED stringified
    // 'edits' array was REJECTED. That #2690 decision has been deliberately superseded, not
    // weakened: forensics measured 37 hard failures/week from exactly this shape (including a cron
    // that never once wrote its checkpoint), and a string whose content parses cleanly to an array
    // of {oldText,newText} objects is a lossless unwrap with nothing to guess at - the same
    // coercion #1562 already performs at the validator seam. The safety property the original
    // tests existed to protect (never fabricate an edit from a payload we had to guess at) is
    // preserved and strengthened below: a MALFORMED stringified payload is still rejected and the
    // file is still byte-identical. The pin has moved from "reject all strings" to "unwrap only
    // what parses exactly; reject and never mutate otherwise".

    [Fact]
    public async Task ExecuteAsync_WhenEditsIsWellFormedJsonString_AppliesTheEdit()
    {
        var filePath = await WriteFileAsync("stringified.txt", "alpha\nbeta\n");

        var stringified = JsonDocument
            .Parse("\"[{\\\"oldText\\\":\\\"beta\\\",\\\"newText\\\":\\\"gamma\\\"}]\"")
            .RootElement.Clone();

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "stringified.txt",
            ["edits"] = stringified
        });

        var after = await _fileSystem.File.ReadAllTextAsync(filePath);
        after.ShouldBe("alpha\ngamma\n");
    }

    [Fact]
    public async Task ExecuteAsync_WhenEditsIsMalformedJsonString_ThrowsAndDoesNotApplyTheEdit()
    {
        const string content = "alpha\nbeta\n";
        var filePath = await WriteFileAsync("stringified-negative.txt", content);

        // Truncated mid-object: it cannot be recovered without guessing.
        var stringified = JsonDocument
            .Parse("\"[{\\\"oldText\\\":\\\"beta\\\",\\\"newText\\\"\"")
            .RootElement.Clone();

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "stringified-negative.txt",
            ["edits"] = stringified
        });

        var ex = await action.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldContain("edits");
        ex.Message.ShouldContain("JSON");

        // #2415 precedent: guessing at a malformed payload risks fabricating an edit against a
        // user's file, so the file must be byte-identical.
        var after = await _fileSystem.File.ReadAllTextAsync(filePath);
        after.ShouldBe(content);
    }
}
