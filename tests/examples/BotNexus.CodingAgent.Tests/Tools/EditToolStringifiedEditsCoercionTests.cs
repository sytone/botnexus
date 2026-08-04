using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Validation;
using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Issue #2759: <c>edit</c> rejected <c>edits</c> supplied as a JSON-encoded string - 37 hard
/// failures/week across 7 agents, 18 of them one cron that therefore never wrote its checkpoint.
/// <para>
/// Root-cause finding, established in source and pinned here: the #1562/#1738 coercion seam in
/// <see cref="ToolCallValidator"/> DOES fire for <c>edits</c> (the issue's hypothesis that
/// <c>edit</c> validates ahead of the seam is REFUTED - see
/// <c>Validate_StringifiedEdits_IsCoercedByTheSeam_BeforeTheToolEverSeesIt</c>). The failures came
/// from the paths that reach <see cref="EditTool"/> WITHOUT passing through the seam (direct
/// dispatch), where the tool's own <c>ParseEdits</c> rejected the string outright. The fix applies
/// the same lossless unwrap at the tool boundary, so both routes now behave identically.
/// </para>
/// </summary>
public sealed class EditToolStringifiedEditsCoercionTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "edit-2759");
    private readonly MockFileSystem _fileSystem = new();
    private readonly EditTool _tool;

    public EditToolStringifiedEditsCoercionTests()
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

    private static JsonElement JsonString(string value) =>
        JsonSerializer.SerializeToElement(value).Clone();

    // --- AC1: a JSON-array string with a valid path applies the edit -------------------------

    /// <summary>
    /// AC1 and AC5: this is the test a mutation reverting the string-coercion path must redden.
    /// Removing <c>TryParseStringifiedEdits</c> from <c>EditTool.ParseEdits</c> makes this throw
    /// <see cref="ArgumentException"/> instead of applying the edit.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenEditsIsJsonArrayString_AppliesTheEdit()
    {
        var filePath = await WriteFileAsync("ac1.json", "{\n  \"lastRunUtc\": \"2026-08-02\"\n}\n");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "ac1.json",
            ["edits"] = JsonString(
                "[{\"oldText\":\"\\\"lastRunUtc\\\": \\\"2026-08-02\\\"\"," +
                "\"newText\":\"\\\"lastRunUtc\\\": \\\"2026-08-03\\\"\"}]")
        });

        string.Join("\n", result.Content.Select(c => c.Value)).ShouldContain("Successfully replaced");
        var after = await _fileSystem.File.ReadAllTextAsync(filePath);
        after.ShouldBe("{\n  \"lastRunUtc\": \"2026-08-03\"\n}\n");
    }

    [Fact]
    public async Task ExecuteAsync_WhenEditsIsJsonArrayStringWithMultipleEntries_AppliesAllOfThem()
    {
        var filePath = await WriteFileAsync("ac1-multi.txt", "alpha\nbeta\ngamma\n");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "ac1-multi.txt",
            ["edits"] = JsonString(
                "[{\"oldText\":\"alpha\",\"newText\":\"ALPHA\"}," +
                "{\"oldText\":\"gamma\",\"newText\":\"GAMMA\"}]")
        });

        var after = await _fileSystem.File.ReadAllTextAsync(filePath);
        after.ShouldBe("ALPHA\nbeta\nGAMMA\n");
    }

    [Fact]
    public async Task ExecuteAsync_WhenEditsIsRawClrStringHoldingJsonArray_AppliesTheEdit()
    {
        // Some dispatch paths hand the tool a raw CLR string rather than a JsonElement; both must
        // behave identically or the fix would be route-dependent.
        var filePath = await WriteFileAsync("ac1-clr.txt", "alpha\nbeta\n");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "ac1-clr.txt",
            ["edits"] = "[{\"oldText\":\"beta\",\"newText\":\"gamma\"}]"
        });

        var after = await _fileSystem.File.ReadAllTextAsync(filePath);
        after.ShouldBe("alpha\ngamma\n");
    }

    // --- AC2: honest parse-failure diagnostics ----------------------------------------------

    [Fact]
    public async Task ExecuteAsync_WhenEditsStringIsNotValidJson_ReportsOffsetAndFullValueLength()
    {
        var filePath = await WriteFileAsync("ac2.txt", "alpha\nbeta\n");

        // Well-formed prefix, truncated before the closing brace/bracket - the exact shape the
        // forensics captured, and the shape whose old diagnostic misattributed the cause to the
        // error message's own preview truncation.
        const string malformed = "[{\"oldText\":\"beta\",\"newText\":\"gamma\"";

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "ac2.txt",
            ["edits"] = JsonString(malformed)
        });

        var ex = await action.ShouldThrowAsync<ArgumentException>();

        // The parse offset, so the caller can fix the actual syntax error.
        ex.Message.ShouldContain("position");
        // The FULL value length, so the caller can confirm the value arrived whole.
        ex.Message.ShouldContain(malformed.Length.ToString());
        // And an explicit statement that truncation was NOT the cause.
        ex.Message.ShouldContain("not truncated");

        // Nothing was applied.
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("alpha\nbeta\n");
    }

    [Fact]
    public async Task ExecuteAsync_WhenEditsStringParsesButEntriesAreWrongShape_ThrowsAndDoesNotApply()
    {
        var filePath = await WriteFileAsync("ac2-shape.txt", "alpha\nbeta\n");

        // Parses as a JSON array, but the entries are not {oldText,newText} objects. There is
        // nothing to unwrap losslessly, so this must reject rather than guess.
        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "ac2-shape.txt",
            ["edits"] = JsonString("[\"beta\",\"gamma\"]")
        });

        await action.ShouldThrowAsync<ArgumentException>();
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("alpha\nbeta\n");
    }

    // --- AC3: the #1562 coercion seam is exercised, not bypassed -----------------------------

    /// <summary>
    /// AC3, pinned by name: <c>edit</c>'s own published parameter schema, run through the #1562
    /// coercion seam (<see cref="ToolCallValidator.Validate(JsonElement, JsonElement, out JsonElement)"/>),
    /// coerces a stringified <c>edits</c> into a real JSON array and validates. This REFUTES the
    /// issue's hypothesis that <c>edit</c> validates ahead of the seam: when the seam is on the
    /// path it already does the right thing.
    /// </summary>
    [Fact]
    public void Validate_StringifiedEdits_IsCoercedByTheSeam_BeforeTheToolEverSeesIt()
    {
        var schema = _tool.Definition.Parameters;
        var arguments = JsonDocument.Parse("""
            { "path": "a.txt", "edits": "[{\"oldText\":\"a\",\"newText\":\"b\"}]" }
            """).RootElement.Clone();

        var (isValid, errors) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        errors.ShouldBeEmpty();

        var edits = coerced.GetProperty("edits");
        edits.ValueKind.ShouldBe(JsonValueKind.Array);
        edits.GetArrayLength().ShouldBe(1);
        edits[0].GetProperty("oldText").GetString().ShouldBe("a");
        edits[0].GetProperty("newText").GetString().ShouldBe("b");
    }

    /// <summary>
    /// AC3, the other half: the coerced arguments the seam produces dispatch cleanly into the
    /// tool. This is what makes the seam "exercised" end-to-end rather than merely correct in
    /// isolation.
    /// </summary>
    [Fact]
    public async Task Validate_ThenExecute_SeamCoercedArguments_ApplyTheEdit()
    {
        var filePath = await WriteFileAsync("ac3.txt", "alpha\nbeta\n");

        var schema = _tool.Definition.Parameters;
        var arguments = JsonDocument.Parse("""
            { "path": "ac3.txt", "edits": "[{\"oldText\":\"beta\",\"newText\":\"gamma\"}]" }
            """).RootElement.Clone();

        var (isValid, _) = ToolCallValidator.Validate(arguments, schema, out var coerced);
        isValid.ShouldBeTrue();

        var dispatched = coerced.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

        await _tool.ExecuteAsync("test-call", dispatched);

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("alpha\ngamma\n");
    }

    // --- AC6: a well-formed array is byte-identical to before --------------------------------

    /// <summary>
    /// AC6: the array path is untouched. The stringified and array forms of the same payload must
    /// produce byte-identical file content, proving the coercion is lossless rather than a
    /// separate, subtly different code path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ArrayAndStringifiedForms_ProduceByteIdenticalResults()
    {
        const string content = "alpha\r\nbeta\r\ngamma\n";
        var arrayPath = await WriteFileAsync("ac6-array.txt", content);
        var stringPath = await WriteFileAsync("ac6-string.txt", content);

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "ac6-array.txt",
            ["edits"] = JsonDocument.Parse("""[{"oldText":"beta","newText":"BETA"}]""").RootElement.Clone()
        });

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "ac6-string.txt",
            ["edits"] = JsonString("[{\"oldText\":\"beta\",\"newText\":\"BETA\"}]")
        });

        var arrayBytes = await _fileSystem.File.ReadAllBytesAsync(arrayPath);
        var stringBytes = await _fileSystem.File.ReadAllBytesAsync(stringPath);
        stringBytes.ShouldBe(arrayBytes);
    }

    [Fact]
    public async Task ExecuteAsync_WhenEditsStringIsEmptyArray_StillRejects()
    {
        var filePath = await WriteFileAsync("ac6-empty.txt", "alpha\n");

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "ac6-empty.txt",
            ["edits"] = JsonString("[]")
        });

        await action.ShouldThrowAsync<ArgumentException>();
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("alpha\n");
    }
}
