using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// #3014. The Anthropic messages API rejects the whole request with HTTP 400 when a
/// <c>tool_result</c> block references a <c>tool_use</c> id that is not present in the transcript.
/// Overflow compaction can strand exactly that shape, and this converter previously appended the
/// orphan block unconditionally. The drop now lives in the shared
/// <c>MessageTransformer.TransformMessages</c> seam that <c>AnthropicMessageConverter.ConvertMessages</c>
/// already calls, so these tests exercise the converter's public entry point rather than the seam
/// directly - the point of the issue is that the protection is inherited here, not that the helper
/// works in isolation.
/// </summary>
public class AnthropicOrphanToolResultTests
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static AssistantMessage Assistant(params ContentBlock[] content) => new(
        Content: content,
        Api: "anthropic-messages",
        Provider: "anthropic",
        ModelId: "claude-sonnet-4",
        Usage: Usage.Empty(),
        StopReason: StopReason.ToolUse,
        ErrorMessage: null,
        ResponseId: null,
        Timestamp: Ts);

    private static IEnumerable<Dictionary<string, object?>> ToolResultBlocks(
        List<Dictionary<string, object?>> messages) =>
        messages
            .Where(m => m["content"] is List<object>)
            .SelectMany(m => (List<object>)m["content"]!)
            .OfType<Dictionary<string, object?>>()
            .Where(b => b.TryGetValue("type", out var t) && (string?)t == "tool_result");

    [Fact]
    public void ConvertMessages_OrphanToolResult_EmitsNoToolResultBlock()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hi"), Ts),
            new ToolResultMessage("tc-missing", "do_thing", [new TextContent("stranded")], false, Ts),
        };

        var result = AnthropicMessageConverter.ConvertMessages(
            messages, TestHelpers.MakeModel(), isOAuthToken: false);

        ToolResultBlocks(result).ShouldBeEmpty();
    }

    [Fact]
    public void ConvertMessages_PairedToolResult_StillEmitsItsBlock()
    {
        // Non-vacuity guard: a converter that dropped every tool result would satisfy the test above
        // while destroying the feature. This pins the positive case at the same entry point.
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("go"), Ts),
            Assistant(new ToolCallContent("tc-ok", "do_thing", new Dictionary<string, object?>())),
            new ToolResultMessage("tc-ok", "do_thing", [new TextContent("done")], false, Ts),
        };

        var result = AnthropicMessageConverter.ConvertMessages(
            messages, TestHelpers.MakeModel(), isOAuthToken: false);

        var block = ToolResultBlocks(result).ShouldHaveSingleItem();
        block["tool_use_id"].ShouldBe("tc-ok");
    }

    [Fact]
    public void ConvertMessages_MixedOrphanAndPaired_DropsOnlyTheOrphan()
    {
        // The overflow-compaction shape: a stranded leading result followed by a legitimate turn.
        var messages = new Message[]
        {
            new ToolResultMessage("tc-cut", "do_thing", [new TextContent("stranded")], false, Ts),
            Assistant(new ToolCallContent("tc-ok", "do_thing", new Dictionary<string, object?>())),
            new ToolResultMessage("tc-ok", "do_thing", [new TextContent("done")], false, Ts),
        };

        var result = AnthropicMessageConverter.ConvertMessages(
            messages, TestHelpers.MakeModel(), isOAuthToken: false);

        ToolResultBlocks(result).Select(b => b["tool_use_id"]).ShouldBe(["tc-ok"]);
    }
}
