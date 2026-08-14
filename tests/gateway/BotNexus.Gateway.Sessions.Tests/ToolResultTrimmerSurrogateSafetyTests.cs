using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using Shouldly;

namespace BotNexus.Gateway.Sessions.Tests;

/// <summary>
/// #3187: <c>ToolResultTrimmer</c> bounded its tombstone preview with a raw UTF-16 range slice at
/// <c>TombstonePreviewChars</c>. The tombstone <em>replaces</em> the original tool result in
/// session history and the full content is discarded, so a cut that split a surrogate pair left the
/// mangled preview as the only surviving copy - unrepairable, per the #2883 argument. Tool output
/// is arbitrary text and routinely contains emoji, so the boundary is reachable in practice.
/// </summary>
public sealed class ToolResultTrimmerSurrogateSafetyTests
{
    /// <summary>U+1F600 GRINNING FACE - two UTF-16 code units, the smallest astral test case.</summary>
    private const string Grinning = "\U0001F600";

    private const int PreviewChars = 200;

    private static ToolResultTrimmingOptions Options() =>
        new() { TombstonePreviewChars = PreviewChars, MinContentLengthChars = 50 };

    private static SessionEntry User() => new() { Role = MessageRole.User, Content = "u" };

    private static SessionEntry Assistant() => new() { Role = MessageRole.Assistant, Content = "a" };

    private static SessionEntry ToolResult(string content) => new()
    {
        Role = MessageRole.Tool,
        Content = content,
        ToolName = "read",
        ToolCallId = Guid.NewGuid().ToString()
    };

    [Fact]
    public void CreateTombstone_AstralCharacterStraddlingTheLimit_PersistsNoLoneSurrogate()
    {
        // The emoji starts at index PreviewChars - 1, so a raw slice at PreviewChars keeps its high
        // surrogate and drops its low surrogate. This is the exact defect shape.
        var content = new string('a', PreviewChars - 1) + Grinning + new string('b', 500);
        char.IsHighSurrogate(content[PreviewChars - 1]).ShouldBeTrue();
        char.IsLowSurrogate(content[PreviewChars]).ShouldBeTrue();

        var tombstone = Trim(content);

        tombstone.Content.ShouldStartWith(ToolResultTrimmer.TombstoneMarker);
        HasUnpairedSurrogate(tombstone.Content).ShouldBeFalse(
            "#3187: the tombstone preview persisted into session history must not contain a lone surrogate.");
        tombstone.Content.Length.ShouldBeLessThan(content.Length);
    }

    [Fact]
    public void CreateTombstone_ContentAtTheLimit_PreviewIsTheWholeContentUnchanged()
    {
        var content = new string('a', PreviewChars);

        var tombstone = Trim(content);

        tombstone.Content.ShouldStartWith(ToolResultTrimmer.TombstoneMarker);
        tombstone.Content.ShouldEndWith(content + "…");
        HasUnpairedSurrogate(tombstone.Content).ShouldBeFalse();
    }

    /// <summary>
    /// Drives the real pipeline: a large tool result aged past the threshold becomes a tombstone.
    /// Exercising <c>Trim</c> rather than reaching for the private helper keeps the test bound to
    /// the behaviour that actually persists.
    /// </summary>
    private static SessionEntry Trim(string content)
    {
        var trimmer = new ToolResultTrimmer(Options());
        var entries = new List<SessionEntry>
        {
            User(), ToolResult(content),
            User(), Assistant(),
            User(), Assistant(),
            User()
        };

        var result = trimmer.Trim(entries);
        var tombstone = result[1];
        tombstone.Content.ShouldStartWith(
            ToolResultTrimmer.TombstoneMarker,
            Case.Sensitive,
            "fixture must actually trigger trimming, or the assertions below are vacuous");
        return tombstone;
    }

    /// <summary>
    /// Scans for a surrogate that is not part of a well-formed pair. This is the direct expression
    /// of the invariant; a substring check would pass on a string that also contained an unpaired
    /// surrogate elsewhere.
    /// </summary>
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
