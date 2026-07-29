using System.Text.Json;
using BotNexus.Agent.Providers.Core.Validation;

namespace BotNexus.Agent.Providers.Core.Tests.Validation;

/// <summary>
/// Covers the misspelled-property suggestion appended to the
/// "not defined in the schema" rejection (issue #2408, clause 1).
/// </summary>
public class ToolCallValidatorSuggestionTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Validate_WhenUnknownPropertyIsNearMissOfDeclaredProperty_SuggestsIt()
    {
        var arguments = Parse("""{ "pathh": "a.txt" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "path": { "type": "string" } },
              "additionalProperties": false
            }
            """);

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.ShouldBe("Property 'pathh' is not defined in the schema. Did you mean 'path'?");
    }

    [Fact]
    public void Validate_WhenUnknownPropertyHasNoCloseMatch_LeavesMessageUnchanged()
    {
        var arguments = Parse("""{ "elephant": "a.txt" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "path": { "type": "string" } },
              "additionalProperties": false
            }
            """);

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.ShouldBe("Property 'elephant' is not defined in the schema.");
    }

    [Fact]
    public void Validate_WhenUnknownPropertyDiffersOnlyByCase_SuggestsDeclaredCasing()
    {
        var arguments = Parse("""{ "Path": "a.txt" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "path": { "type": "string" } },
              "additionalProperties": false
            }
            """);

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.ShouldBe("Property 'Path' is not defined in the schema. Did you mean 'path'?");
    }

    [Fact]
    public void Validate_WhenTwoCandidatesTie_PicksOrdinalFirstDeterministically()
    {
        // 'ax' is declared first but 'aa' sorts first by ordinal; both are edit distance 1 from 'ab'.
        var arguments = Parse("""{ "ab": 1 }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "ax": { "type": "integer" }, "aa": { "type": "integer" } },
              "additionalProperties": false
            }
            """);

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.ShouldBe("Property 'ab' is not defined in the schema. Did you mean 'aa'?");
    }

    [Fact]
    public void Validate_WhenSchemaDeclaresNoProperties_EmitsUnchangedMessage()
    {
        var arguments = Parse("""{ "path": "a.txt" }""");
        var schema = Parse("""
            {
              "type": "object",
              "additionalProperties": false
            }
            """);

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.ShouldBe("Property 'path' is not defined in the schema.");
    }

    [Fact]
    public void Validate_WhenCallIsValid_PassesWithNoErrors()
    {
        var arguments = Parse("""{ "path": "a.txt" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "path": { "type": "string" } },
              "required": ["path"],
              "additionalProperties": false
            }
            """);

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }
}
