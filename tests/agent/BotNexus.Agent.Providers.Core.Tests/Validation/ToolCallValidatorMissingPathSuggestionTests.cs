using System.Text.Json;
using BotNexus.Agent.Providers.Core.Validation;

namespace BotNexus.Agent.Providers.Core.Tests.Validation;

/// <summary>
/// Issue #3711. <c>edit</c> is called with a well-formed <c>edits</c> array and no <c>path</c>
/// roughly 20 times a week, and every one discards a fully-formed multi-line payload that must
/// be regenerated — the most token-expensive failure shape in the corpus.
/// </summary>
/// <remarks>
/// The premise that #2759 fixed this is <b>refuted</b>: #2759's AC4 ("list candidate paths from
/// the turn's reads") was explicitly scoped out of its implementation brief and recorded as a
/// known residual, then swept closed by PR #2831 which only touched truncation reporting. So this
/// is an <i>incomplete</i> fix, never a regression — nothing to restore.
/// <para>
/// The safety property is the important half. Naming a probable target must never become
/// <i>using</i> one: silently applying an edit to a file the caller did not name is a
/// destructive-write hazard, so the suggestion is diagnostic text only and the call still fails.
/// </para>
/// </remarks>
public class ToolCallValidatorMissingPathSuggestionTests
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

    private static JsonElement EditsOnly() => Parse("""
        { "edits": [ { "oldText": "a", "newText": "b" } ] }
        """);

    /// <summary>
    /// AC2: when the session has a most-recently-read file, the rejection names it as a
    /// suggested target so the correction is one edit rather than a re-derivation.
    /// </summary>
    [Fact]
    public void Validate_WhenPathMissingAndASessionReadExists_NamesThatFileAsASuggestedTarget()
    {
        var context = new ToolCallValidationContext(MostRecentlyReadPath: "src/gateway/BotNexus.Tools/EditTool.cs");

        var (isValid, errors) = ToolCallValidator.Validate(EditsOnly(), EditSchema(), out _, context);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("Missing required property 'path'");
        error.ShouldContain("src/gateway/BotNexus.Tools/EditTool.cs");
        error.ShouldContain("most recently read");
    }

    /// <summary>
    /// AC3, stated as the property that matters: the suggestion is advisory text, never an
    /// applied value. The call still fails and the coerced arguments handed downstream must not
    /// have acquired a 'path' the caller never supplied.
    /// </summary>
    [Fact]
    public void Validate_WhenPathMissing_NeverSubstitutesTheReadPathIntoTheArguments()
    {
        var context = new ToolCallValidationContext(MostRecentlyReadPath: "docs/observability.md");

        var (isValid, _) = ToolCallValidator.Validate(EditsOnly(), EditSchema(), out var coerced, context);

        isValid.ShouldBeFalse();
        coerced.TryGetProperty("path", out _).ShouldBeFalse();
    }

    /// <summary>
    /// The suggestion must state that it will not be used automatically, so a model reading the
    /// error cannot conclude the tool already retargeted the edit for it.
    /// </summary>
    [Fact]
    public void Validate_WhenPathMissing_SuggestionSaysItIsNotAppliedAutomatically()
    {
        var context = new ToolCallValidationContext(MostRecentlyReadPath: "a.txt");

        var (_, errors) = ToolCallValidator.Validate(EditsOnly(), EditSchema(), out _, context);

        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("not applied automatically");
    }

    /// <summary>
    /// "when one exists" — with no read on record the message must stay exactly as it was, not
    /// emit an empty or speculative suggestion clause.
    /// </summary>
    [Fact]
    public void Validate_WhenNoSessionReadExists_OmitsTheSuggestionClauseEntirely()
    {
        var (isValid, errors) = ToolCallValidator.Validate(EditsOnly(), EditSchema(), out _, validationContext: null);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("Missing required property 'path'");
        error.ShouldNotContain("most recently read");
        error.ShouldNotContain("not applied automatically");
    }

    /// <summary>
    /// A whitespace-only path is not a usable target and must be treated as "no read on record"
    /// rather than producing a suggestion naming nothing.
    /// </summary>
    [Fact]
    public void Validate_WhenRecordedReadPathIsBlank_OmitsTheSuggestionClause()
    {
        var context = new ToolCallValidationContext(MostRecentlyReadPath: "   ");

        var (_, errors) = ToolCallValidator.Validate(EditsOnly(), EditSchema(), out _, context);

        errors.ShouldHaveSingleItem().ShouldNotContain("most recently read");
    }

    /// <summary>
    /// The suggestion is scoped to a missing <c>path</c>. A different missing property must not
    /// acquire a file-target suggestion that has nothing to do with it.
    /// </summary>
    [Fact]
    public void Validate_WhenADifferentRequiredPropertyIsMissing_DoesNotSuggestTheReadPath()
    {
        var schema = Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string" },
                "content": { "type": "string" }
              },
              "required": ["path", "content"]
            }
            """);
        var arguments = Parse("""{ "path": "a.txt" }""");
        var context = new ToolCallValidationContext(MostRecentlyReadPath: "b.txt");

        var (isValid, errors) = ToolCallValidator.Validate(arguments, schema, out _, context);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("Missing required property 'content'");
        error.ShouldNotContain("most recently read");
    }

    /// <summary>
    /// The pre-existing #2415/#2690 diagnostic halves (supplied siblings, required signature,
    /// minimal payload skeleton) must survive alongside the new clause — the suggestion is
    /// additive, not a replacement.
    /// </summary>
    [Fact]
    public void Validate_WhenPathMissingWithSuggestion_RetainsTheExistingDiagnosticHalves()
    {
        var context = new ToolCallValidationContext(MostRecentlyReadPath: "a.txt");

        var (_, errors) = ToolCallValidator.Validate(EditsOnly(), EditSchema(), out _, context);

        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("You supplied: edits");
        error.ShouldContain("This tool's required: path, edits");
        error.ShouldContain("Minimal valid payload");
    }
}
