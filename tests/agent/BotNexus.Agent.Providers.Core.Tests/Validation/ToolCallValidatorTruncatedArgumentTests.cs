using System.Text.Json;
using BotNexus.Agent.Providers.Core.Validation;

namespace BotNexus.Agent.Providers.Core.Tests.Validation;

/// <summary>
/// Issue #2759. The issue's stated root cause — that <c>edit</c> validates AHEAD of the #1562
/// coercion seam — is REFUTED. <see cref="ToolCallValidator.Validate(JsonElement, JsonElement, out JsonElement)"/>
/// runs <c>CoerceArguments</c> before any validation, and <c>edit</c>'s schema declares
/// <c>edits</c> as a plain top-level <c>"type": "array"</c>, so the coercion branch is entered
/// and a well-formed JSON-array string is already accepted. These tests PIN that (AC1/AC3/AC5)
/// rather than change it, and cover the diagnostic work that genuinely remained (AC2).
/// </summary>
public class ToolCallValidatorTruncatedArgumentTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>The verbatim <c>edit</c> schema shape from <c>EditTool.cs</c>.</summary>
    private static JsonElement EditSchema() => Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "expectedHash": { "type": "string" },
            "edits": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "oldText": { "type": "string" },
                  "newText": { "type": "string" }
                },
                "required": ["oldText", "newText"]
              }
            }
          },
          "required": ["path", "edits"]
        }
        """);

    /// <summary>
    /// AC1/AC3/AC5. The seam is exercised, not bypassed: a JSON-array STRING for <c>edits</c>
    /// reaches <c>TryCoerceValue</c> and is parsed into a real array before validation runs.
    /// Reverting the string-coercion branch reddens this test by name.
    /// </summary>
    [Fact]
    public void Validate_WhenEditsIsWellFormedJsonArrayString_CoercionSeamParsesItBeforeValidating()
    {
        var arguments = Parse("""
            {
              "path": "playbook/teams-mostly-harmless-checkpoint.json",
              "edits": "[{\"oldText\":\"\\\"lastRunUtc\\\": \\\"2026-08-01\\\"\",\"newText\":\"\\\"lastRunUtc\\\": \\\"2026-08-03\\\"\"}]"
            }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out var coerced);

        isValid.ShouldBeTrue();
        errors.ShouldBeEmpty();

        var edits = coerced.GetProperty("edits");
        edits.ValueKind.ShouldBe(JsonValueKind.Array);
        edits.GetArrayLength().ShouldBe(1);
        edits[0].GetProperty("oldText").GetString().ShouldBe("\"lastRunUtc\": \"2026-08-01\"");
        edits[0].GetProperty("newText").GetString().ShouldBe("\"lastRunUtc\": \"2026-08-03\"");
    }

    /// <summary>
    /// AC2. The real cause of the residual weekly failures: the payload arrives INCOMPLETE. The
    /// error must name the full value length and say the value was cut short, so the model does
    /// not "fix" quoting that was never wrong.
    /// </summary>
    [Fact]
    public void Validate_WhenEditsStringIsTruncated_ReportsFullLengthAndNamesTruncationAsTheCause()
    {
        // The verbatim captured shape: a valid array literal that simply stops mid-string.
        var truncated = "[{\"oldText\":\" \\\"lastRunUtc\\\": \\\"2026-08.";
        var arguments = Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["path"] = "a.json",
            ["edits"] = truncated
        }));

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out _);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("is not valid JSON");
        // The FULL length, derived from the payload rather than restated as a literal.
        error.ShouldContain($"of {truncated.Length} characters");
        error.ShouldContain("cut short in transit");
        error.ShouldContain("Re-send the WHOLE value");
    }

    /// <summary>
    /// AC2 (the other half). A payload that is mis-typed in the MIDDLE is not truncation, and must
    /// not be described as such — the two failures have opposite remedies.
    /// </summary>
    [Fact]
    public void Validate_WhenEditsStringIsMalformedMidValue_DoesNotClaimTruncation()
    {
        var arguments = Parse("""
            { "path": "a.json", "edits": "[{\"oldText\">\"a\",\"newText\":\"b\"}]" }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out _);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("is not valid JSON");
        error.ShouldContain("characters).");
        error.ShouldNotContain("cut short in transit");
    }

    /// <summary>
    /// AC2. An elided display preview must be labelled as a preview and carry the full length,
    /// so "…" in the message is never mistaken for the value itself ending there.
    /// </summary>
    [Fact]
    public void Validate_WhenLongStringIsElidedInPreview_LabelsItAPreviewAndStatesTheFullLength()
    {
        var longValue = new string('x', 500);
        var arguments = Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["path"] = "a.json",
            ["edits"] = longValue
        }));

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out _);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("preview only");
        error.ShouldContain($"the full value is {longValue.Length} characters");
    }

    /// <summary>
    /// AC6. A short string value's description is unchanged — no length clause is bolted onto a
    /// value that was shown in full.
    /// </summary>
    [Fact]
    public void Validate_WhenShortStringIsShownInFull_OmitsThePreviewClause()
    {
        var arguments = Parse("""{ "path": "a.json", "edits": "rename it" }""");

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out _);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("received string \"rename it\"");
        error.ShouldNotContain("preview only");
    }

    /// <summary>
    /// AC6. A well-formed ARRAY for <c>edits</c> is untouched by every change above.
    /// </summary>
    [Fact]
    public void Validate_WhenEditsIsAlreadyAnArray_IsAcceptedUnchanged()
    {
        var arguments = Parse("""
            { "path": "a.json", "edits": [ { "oldText": "a", "newText": "b" } ] }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out var coerced);

        isValid.ShouldBeTrue();
        errors.ShouldBeEmpty();
        coerced.GetRawText().ShouldBe(arguments.GetRawText());
    }
}
