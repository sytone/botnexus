using System.Text.Json;
using BotNexus.Agent.Providers.Core.Validation;

namespace BotNexus.Agent.Providers.Core.Tests.Validation;

/// <summary>
/// Regression coverage for the two <c>edit</c> rows of issue #2415. Both are cases where the
/// validator's rejection message is technically true but gives the model nothing actionable, so
/// the retry is blind and the same call fails again. These tests pin the diagnostic content, not
/// just the accept/reject verdict.
/// </summary>
public class ToolCallValidatorEditDiagnosticsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement EditSchema() => Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "edits": { "type": "array", "items": { "type": "object" } }
          },
          "required": ["path", "edits"]
        }
        """);

    [Fact]
    public void Validate_WhenWellFormedJsonArrayString_ParsesEditsIntoRealArray()
    {
        // The verbatim shape from issue #2415: 'edits' arrived as a JSON string rather than an
        // array. When it deserialises cleanly it must be accepted as the array it encodes.
        var arguments = Parse("""
            { "path": "a.txt", "edits": "[{\"oldText\":\"a\",\"newText\":\"b\"}]" }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out var coerced);

        isValid.ShouldBeTrue();
        errors.ShouldBeEmpty();
        var edits = coerced.GetProperty("edits");
        edits.ValueKind.ShouldBe(JsonValueKind.Array);
        edits.GetArrayLength().ShouldBe(1);
        edits[0].GetProperty("oldText").GetString().ShouldBe("a");
    }

    [Fact]
    public void Validate_WhenMalformedJsonArrayString_ExplainsWhyItDidNotParse()
    {
        // The captured payload used '>' where ':' belongs, so a retry-deserialise still fails.
        // The message must say WHY (and where) instead of truncating the payload into the text
        // and restating a requirement the model believes it already met.
        var arguments = Parse("""
            { "path": "a.txt", "edits": "[{\"oldText\">\"a\"}]" }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out var coerced);

        isValid.ShouldBeFalse();
        coerced.GetProperty("edits").ValueKind.ShouldBe(JsonValueKind.String);
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("'edits'");
        error.ShouldContain("was a string and is not valid JSON");
        error.ShouldContain("position");
    }

    [Fact]
    public void Validate_WhenNonJsonStringForObjectArray_DoesNotClaimAParseFailure()
    {
        // A plain scalar string for an object-item array never looked like JSON, so the parse
        // diagnostic must not fire; the plain type error is the correct message here.
        var arguments = Parse("""{ "path": "a.txt", "edits": "rename it" }""");

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out _);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("must be of type array");
        error.ShouldNotContain("is not valid JSON");
    }

    [Fact]
    public void Validate_WhenRequiredPropertyMissing_NamesSuppliedSiblingsAndRequiredSignature()
    {
        // Issue #2415: 'Missing required property path' with a well-formed 'edits' array cost
        // 6 failures across 4 agents. Naming what WAS supplied and restating the required
        // signature makes the retry one-shot.
        var arguments = Parse("""{ "edits": [ { "oldText": "a", "newText": "b" } ] }""");

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out _);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("Missing required property 'path'");
        error.ShouldContain("supplied: edits");
        error.ShouldContain("required: path, edits");
    }

    [Fact]
    public void Validate_WhenRequiredPropertyMissingAndNothingSupplied_OmitsTheSuppliedClause()
    {
        // With no sibling properties there is nothing to name; the message must not emit an
        // empty 'supplied:' clause.
        var arguments = Parse("{ }");

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out _);

        isValid.ShouldBeFalse();
        errors.Length.ShouldBe(2);
        errors.ShouldAllBe(e => !e.Contains("supplied:"));
        errors[0].ShouldContain("Missing required property 'path'");
        errors[0].ShouldContain("required: path, edits");
    }
}
