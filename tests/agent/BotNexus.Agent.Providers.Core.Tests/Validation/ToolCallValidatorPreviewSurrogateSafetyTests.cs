using System.Text.Json;
using BotNexus.Agent.Providers.Core.Validation;

namespace BotNexus.Agent.Providers.Core.Tests.Validation;

/// <summary>
/// #3171: <c>ToolCallValidator.DescribeValue</c> elided a model-supplied string argument at a
/// fixed 40 UTF-16 code units with a raw range slice, which can cut between a high and a low
/// surrogate. This is an <em>error-reporting</em> path, so a mangled preview degrades exactly the
/// diagnostic it exists to provide - and the message is handed straight back to the model.
/// </summary>
public class ToolCallValidatorPreviewSurrogateSafetyTests
{
    /// <summary>The validator's own <c>PreviewLength</c>, mirrored: it is a private detail.</summary>
    private const int PreviewLength = 40;

    /// <summary>U+1F600 GRINNING FACE - two UTF-16 code units.</summary>
    private const string Grinning = "\U0001F600";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A schema whose <c>edits</c> is an array, so a string value fails and is described.</summary>
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
    public void Validate_WhenAstralCharacterStraddlesThePreviewBoundary_EmitsNoLoneSurrogate()
    {
        // The emoji begins at index PreviewLength - 1, so a raw slice at PreviewLength retains its
        // high surrogate and discards its low surrogate - the exact defect shape.
        var value = new string('x', PreviewLength - 1) + Grinning + new string('y', 200);
        char.IsHighSurrogate(value[PreviewLength - 1]).ShouldBeTrue();
        char.IsLowSurrogate(value[PreviewLength]).ShouldBeTrue();

        var error = DescribeStringArgument(value);

        HasUnpairedSurrogate(error).ShouldBeFalse(
            "#3171: the elided argument preview must not contain a lone surrogate.");
        error.Length.ShouldBeLessThan(value.Length);
        error.ShouldContain("preview only");
        error.ShouldContain($"the full value is {value.Length} characters");
    }

    [Fact]
    public void Validate_WhenValueIsExactlyThePreviewLength_IsShownInFullWithNoPreviewClause()
    {
        var value = new string('x', PreviewLength);

        var error = DescribeStringArgument(value);

        error.ShouldContain($"received string \"{value}\"");
        error.ShouldNotContain("preview only");
    }

    [Fact]
    public void Validate_WhenValueIsUnderThePreviewLengthWithEmoji_IsShownInFullUnmodified()
    {
        var value = "rename " + Grinning + " it";

        var error = DescribeStringArgument(value);

        error.ShouldContain($"received string \"{value}\"");
        error.ShouldNotContain("preview only");
        HasUnpairedSurrogate(error).ShouldBeFalse();
    }

    /// <summary>
    /// Feeds <paramref name="value"/> as the <c>edits</c> argument against a schema expecting an
    /// array, and returns the resulting validation error - the string the model actually reads.
    /// </summary>
    private static string DescribeStringArgument(string value)
    {
        var arguments = Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["path"] = "a.json",
            ["edits"] = value
        }));

        var (isValid, errors) = ToolCallValidator.Validate(arguments, EditSchema(), out _);

        isValid.ShouldBeFalse();
        return errors.ShouldHaveSingleItem();
    }

    /// <summary>Scans for a surrogate that is not part of a well-formed pair.</summary>
    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    return true;
                i++;
                continue;
            }

            if (char.IsLowSurrogate(value[i]))
                return true;
        }

        return false;
    }
}
