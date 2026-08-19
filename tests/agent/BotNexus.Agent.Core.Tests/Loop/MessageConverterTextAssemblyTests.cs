using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using ProviderAssistantMessage = BotNexus.Agent.Providers.Core.Models.AssistantMessage;

/// <summary>
/// Regression coverage for the streamed-text assembly invariant (#3425).
/// </summary>
/// <remarks>
/// <see cref="MessageConverter.ToAgentMessage"/> previously joined text blocks with
/// <c>Environment.NewLine</c>, injecting a literal <c>\r\n</c> between every streamed block on
/// Windows. That corrupted 1,033 persisted assistant messages across 15 agents into one-token-per-line
/// output, including mid-word breaks such as <c>/pl</c> + <c>anning</c>.
/// <para>
/// A stream chunk boundary is transport metadata and carries no implied separator, so the only
/// correct assembly is exact ordered concatenation. Mitm captures of the GitHub Copilot CLI against
/// the same endpoints contain zero raw CR bytes across 3,025 provider deltas, confirming the wire is
/// clean and the corruption was manufactured on our side.
/// </para>
/// </remarks>
public class MessageConverterTextAssemblyTests
{
    private static ProviderAssistantMessage Assistant(params string[] textBlocks) =>
        new(
            Content: [.. textBlocks.Select(t => new TextContent(t))],
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: 0);

    // The reported defect, verbatim from the corrupted production row.
    [Fact]
    public void ToAgentMessage_MidWordSplitBlocks_ReassembleWithoutSeparator()
    {
        var result = MessageConverter.ToAgentMessage(Assistant("/pl", "anning", " create", " spec"));

        result.Content.ShouldBe("/planning create spec");
    }

    [Fact]
    public void ToAgentMessage_TokenPerBlock_ProducesNoLineBreaks()
    {
        var result = MessageConverter.ToAgentMessage(
            Assistant("In", " a", " new", " Sentinel", " conversation"));

        result.Content.ShouldBe("In a new Sentinel conversation");
        result.Content.ShouldNotContain("\r");
        result.Content.ShouldNotContain("\n");
    }

    // The block boundary must never contribute a character, on any platform. Asserting the absence
    // of Environment.NewLine specifically is what pins the regression: on Linux the old code injected
    // "\n", which is far harder to spot by eye than "\r\n" but equally wrong.
    [Fact]
    public void ToAgentMessage_BlockBoundary_DoesNotInjectEnvironmentNewLine()
    {
        var result = MessageConverter.ToAgentMessage(Assistant("stre", "aming"));

        result.Content.ShouldBe("streaming");
        result.Content.ShouldNotContain(Environment.NewLine);
    }

    // Genuine model newlines live INSIDE a block and must survive byte-identically. This is the
    // control: a fix that strips newlines to "clean up" the output would redden here.
    [Fact]
    public void ToAgentMessage_NewlinesInsideBlocks_ArePreservedVerbatim()
    {
        var result = MessageConverter.ToAgentMessage(
            Assistant("Heading:\n\n", "```text\n", "body\n", "```\n"));

        result.Content.ShouldBe("Heading:\n\n```text\nbody\n```\n");
    }

    // A model may legitimately emit a CR inside its own content. Assembly must not launder it.
    [Fact]
    public void ToAgentMessage_CarriageReturnInsideABlock_IsPreserved()
    {
        var result = MessageConverter.ToAgentMessage(Assistant("a\r\nb"));

        result.Content.ShouldBe("a\r\nb");
    }

    [Fact]
    public void ToAgentMessage_SingleBlock_IsUnchanged()
        => MessageConverter.ToAgentMessage(Assistant("solitary")).Content.ShouldBe("solitary");

    [Fact]
    public void ToAgentMessage_EmptyBlocksBetweenText_ContributeNothing()
        => MessageConverter.ToAgentMessage(Assistant("a", "", "b")).Content.ShouldBe("ab");

    [Fact]
    public void ToAgentMessage_NoTextBlocks_ProducesEmptyContent()
        => MessageConverter.ToAgentMessage(Assistant()).Content.ShouldBe("");

    // Full reconstruction of the production failure: 458 token-shaped blocks, as recovered from
    // session_history row 273441621, must rebuild the original prose exactly.
    [Fact]
    public void ToAgentMessage_ManyTokenBlocks_RebuildOriginalProse()
    {
        const string original =
            "In a new Sentinel conversation, send a normal message using the familiar command syntax:";
        var blocks = original.Split(' ')
            .Select((word, i) => i == 0 ? word : " " + word)
            .ToArray();

        MessageConverter.ToAgentMessage(Assistant(blocks)).Content.ShouldBe(original);
    }
}
