using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// #3014. Third of the three converters that inherit the orphan-tool-result drop from the shared
/// <c>MessageTransformer.TransformMessages</c> seam. The Chat Completions API rejects a
/// <c>role: "tool"</c> message whose <c>tool_call_id</c> has no preceding assistant
/// <c>tool_calls</c> entry, so the same overflow-compaction shape wedges this provider family too.
/// These tests drive <c>CompletionsMessageConverter.Convert</c> - the converter's real entry point -
/// rather than the shared helper directly.
/// </summary>
public class CompletionsOrphanToolResultTests
{
    private const long Ts = 1_700_000_000_000L;

    private static LlmModel Model() => new(
        Id: "gpt-4o",
        Name: "GPT-4o",
        Api: "openai-completions",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 16384);

    private static AssistantMessage Assistant(params ContentBlock[] content) => new(
        Content: content,
        Api: "openai-completions",
        Provider: "openai",
        ModelId: "gpt-4o",
        Usage: Usage.Empty(),
        StopReason: StopReason.ToolUse,
        ErrorMessage: null,
        ResponseId: null,
        Timestamp: Ts);

    private static List<string> ToolRoleCallIds(System.Text.Json.Nodes.JsonArray result) =>
        result
            .Where(n => n!["role"]?.GetValue<string>() == "tool")
            .Select(n => n!["tool_call_id"]!.GetValue<string>())
            .ToList();

    [Fact]
    public void Convert_OrphanToolResult_EmitsNoToolRoleMessage()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hi"), Ts),
            new ToolResultMessage("tc-missing", "do_thing", [new TextContent("stranded")], false, Ts),
        };

        var result = CompletionsMessageConverter.Convert(null, Model(), messages, new OpenAICompletionsCompat());

        ToolRoleCallIds(result).ShouldBeEmpty();
    }

    [Fact]
    public void Convert_PairedToolResult_StillEmitsItsToolRoleMessage()
    {
        // Non-vacuity guard for the drop.
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("go"), Ts),
            Assistant(new ToolCallContent("tc-ok", "do_thing", new Dictionary<string, object?>())),
            new ToolResultMessage("tc-ok", "do_thing", [new TextContent("done")], false, Ts),
        };

        var result = CompletionsMessageConverter.Convert(null, Model(), messages, new OpenAICompletionsCompat());

        ToolRoleCallIds(result).ShouldBe(["tc-ok"]);
    }

    [Fact]
    public void Convert_MixedOrphanAndPaired_DropsOnlyTheOrphan()
    {
        var messages = new Message[]
        {
            new ToolResultMessage("tc-cut", "do_thing", [new TextContent("stranded")], false, Ts),
            Assistant(new ToolCallContent("tc-ok", "do_thing", new Dictionary<string, object?>())),
            new ToolResultMessage("tc-ok", "do_thing", [new TextContent("done")], false, Ts),
        };

        var result = CompletionsMessageConverter.Convert(null, Model(), messages, new OpenAICompletionsCompat());

        ToolRoleCallIds(result).ShouldBe(["tc-ok"]);
    }
}
