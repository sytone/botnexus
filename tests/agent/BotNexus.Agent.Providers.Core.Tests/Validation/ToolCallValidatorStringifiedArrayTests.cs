using System.Text.Json;
using BotNexus.Agent.Providers.Core.Validation;

namespace BotNexus.Agent.Providers.Core.Tests.Validation;

/// <summary>
/// Issue #2690: schema-shape errors were 87 of the 449 measured <c>edit</c> failures - a
/// stringified <c>edits</c> array and a missing <c>path</c>.
/// <para>
/// Note on scope: a <em>well-formed</em> stringified array is deliberately coerced at this layer
/// by issue #1738 / #2415 (see <c>ToolCallValidatorEditDiagnosticsTests</c>), so it never reaches
/// an error message here. What remains is the string that is <em>not</em> coerced - malformed or
/// oversized - which must name the stringification so the caller knows the wrapper, not just the
/// type, is the problem. The reject-not-coerce guarantee for a well-formed string is pinned in
/// <c>EditToolInputShapeDiagnosticsTests</c> against the tool itself.
/// </para>
/// </summary>
public class ToolCallValidatorStringifiedArrayTests
{
    private static JsonElement EditSchema() => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "edits": { "type": "array", "items": { "type": "object" } }
          },
          "required": ["path", "edits"]
        }
        """).RootElement.Clone();

    [Fact]
    public void Validate_WhenStringifiedArrayIsMalformed_NamesStringificationAndKeepsParserReason()
    {
        // The #2415 shape: a JSON string that does not parse, so it is never coerced.
        var arguments = JsonDocument.Parse("""
            { "path": "a.cs", "edits": "[{\"oldText\">\"a\"}]" }
            """).RootElement.Clone();

        var result = ToolCallValidator.Validate(arguments, EditSchema());

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        // The parser reason from #2415 must survive.
        error.ShouldContain("is not valid JSON");
        // New for #2690: name the stringification itself as the shape problem.
        error.ShouldContain("stringif", Case.Insensitive);
        error.ShouldContain("Send 'edits' as a JSON array");
    }

    [Fact]
    public void Validate_WhenPlainStringForArray_DoesNotClaimStringification()
    {
        // "rename it" is not a stringified array at all - it is simply the wrong value. The
        // stringification wording would be misleading here.
        var arguments = JsonDocument.Parse("""{ "path": "a.cs", "edits": "rename it" }""")
            .RootElement.Clone();

        var result = ToolCallValidator.Validate(arguments, EditSchema());

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.ShouldContain("Property 'edits' must be of type array");
        error.ShouldNotContain("stringif", Case.Insensitive);
    }

    [Fact]
    public void Validate_WhenRequiredPathMissing_NamesPathAndShowsMinimalValidPayload()
    {
        var arguments = JsonDocument.Parse("""
            { "edits": [{ "oldText": "a", "newText": "b" }] }
            """).RootElement.Clone();

        var result = ToolCallValidator.Validate(arguments, EditSchema());

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.ShouldContain("Missing required property 'path'");
        // The #2415 clauses must survive.
        error.ShouldContain("You supplied: edits.");
        error.ShouldContain("This tool's required: path, edits.");
        // Acceptance criterion 4: a minimal valid payload, not just a list of names.
        error.ShouldContain("Minimal valid payload:");
        error.ShouldContain("\"path\"");
        error.ShouldContain("\"edits\"");
    }
}
