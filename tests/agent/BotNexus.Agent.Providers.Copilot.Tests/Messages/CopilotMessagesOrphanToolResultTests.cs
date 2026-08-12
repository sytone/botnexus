using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// #3014. The Copilot messages API (Anthropic-shaped) rejects the whole request with HTTP 400 when a
/// <c>tool_result</c> block references a <c>tool_use</c> id absent from the transcript. Overflow
/// compaction can strand exactly that shape, and this converter previously appended the orphan block
/// unconditionally. The drop now lives in the shared <c>MessageTransformer.TransformMessages</c> seam
/// that <c>CopilotMessagesMessageConverter.ConvertMessages</c> already calls, so these tests exercise
/// the converter's own entry point - the issue is about the protection being inherited HERE, not
/// about the shared helper working in isolation.
/// </summary>
public class CopilotMessagesOrphanToolResultTests
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static LlmModel Model() => new(
        Id: "claude-sonnet-4",
        Name: "claude-sonnet-4",
        Api: "copilot-messages",
        Provider: "github-copilot",
        BaseUrl: "https://api.enterprise.githubcopilot.com",
        Reasoning: true,
        Input: ["text", "image"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 200000,
        MaxTokens: 16384);

    private static AssistantMessage Assistant(params ContentBlock[] content) => new(
        Content: content,
        Api: "copilot-messages",
        Provider: "github-copilot",
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

        var result = CopilotMessagesMessageConverter.ConvertMessages(messages, Model());

        ToolResultBlocks(result).ShouldBeEmpty();
    }

    [Fact]
    public void ConvertMessages_PairedToolResult_StillEmitsItsBlock()
    {
        // Non-vacuity guard: a converter that dropped every tool result would satisfy the test above
        // while destroying the feature.
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("go"), Ts),
            Assistant(new ToolCallContent("tc-ok", "do_thing", new Dictionary<string, object?>())),
            new ToolResultMessage("tc-ok", "do_thing", [new TextContent("done")], false, Ts),
        };

        var result = CopilotMessagesMessageConverter.ConvertMessages(messages, Model());

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

        var result = CopilotMessagesMessageConverter.ConvertMessages(messages, Model());

        ToolResultBlocks(result).Select(b => b["tool_use_id"]).ShouldBe(["tc-ok"]);
    }
}
