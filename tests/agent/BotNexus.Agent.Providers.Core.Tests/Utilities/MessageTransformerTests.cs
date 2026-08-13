using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

public class MessageTransformerTests
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private sealed record SystemLikeMessage(long Timestamp) : Message(Timestamp);

    private static LlmModel MakeModel(string provider = "anthropic", string api = "anthropic-messages") => new(
        Id: "test-model",
        Name: "Test",
        Api: api,
        Provider: provider,
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 4096,
        MaxTokens: 1024);

    private static UserMessage MakeUser(string text) =>
        new(new UserMessageContent(text), Ts);

    private static AssistantMessage MakeAssistant(
        IReadOnlyList<ContentBlock> content,
        string provider = "anthropic",
        string api = "anthropic-messages",
        StopReason reason = StopReason.Stop) => new(
        Content: content,
        Api: api,
        Provider: provider,
        ModelId: "test-model",
        Usage: Usage.Empty(),
        StopReason: reason,
        ErrorMessage: null,
        ResponseId: null,
        Timestamp: Ts);

    [Fact]
    public void UserMessages_PassThroughUnchanged()
    {
        var messages = new Message[] { MakeUser("hello") };
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages(messages, model);

        result.Count().ShouldBe(1);
        result[0].ShouldBeOfType<UserMessage>();
    }

    [Fact]
    public void ThinkingBlocks_ConvertedToText_WhenSwitchingProviders()
    {
        var assistant = MakeAssistant(
            [new ThinkingContent("deep thought")],
            provider: "openai",
            api: "openai-completions");
        var messages = new Message[] { assistant, MakeUser("follow up") };
        var model = MakeModel("anthropic", "anthropic-messages");

        var result = MessageTransformer.TransformMessages(messages, model);

        var assistantResult = result[0] as AssistantMessage;
        assistantResult.ShouldNotBeNull();
        assistantResult!.Content[0].ShouldBeOfType<TextContent>();
        var text = (TextContent)assistantResult.Content[0];
        text.Text.ShouldBe("deep thought");
    }

    [Fact]
    public void ThinkingBlocks_Preserved_ForSameProvider()
    {
        var assistant = MakeAssistant(
            [new ThinkingContent("deep thought")],
            provider: "anthropic",
            api: "anthropic-messages");
        var messages = new Message[] { assistant };
        var model = MakeModel("anthropic", "anthropic-messages");

        var result = MessageTransformer.TransformMessages(messages, model);

        var assistantResult = result[0] as AssistantMessage;
        assistantResult!.Content[0].ShouldBeOfType<ThinkingContent>();
    }

    [Fact]
    public void ToolCallIds_NormalizedViaCallback()
    {
        var tc = new ToolCallContent("tc-original", "tool", new Dictionary<string, object?>());
        var assistant = MakeAssistant([tc]);
        var toolResult = new ToolResultMessage("tc-original", "tool", [new TextContent("ok")], false, Ts);
        var messages = new Message[] { assistant, toolResult };
        var model = MakeModel("anthropic", "anthropic-messages") with { Id = "different-model" };

        var result = MessageTransformer.TransformMessages(messages, model,
            (id, _, _) => "normalized-" + id);

        var assistantResult = result[0] as AssistantMessage;
        var normalizedTc = assistantResult!.Content[0] as ToolCallContent;
        normalizedTc!.Id.ShouldBe("normalized-tc-original");

        var toolResultMsg = result[1] as ToolResultMessage;
        toolResultMsg!.ToolCallId.ShouldBe("normalized-tc-original");
    }

    [Fact]
    public void OrphanedToolCalls_GetSyntheticResults()
    {
        var tc = new ToolCallContent("tc-orphan", "tool", new Dictionary<string, object?>());
        var assistant = MakeAssistant([tc]);
        var messages = new Message[] { assistant, MakeUser("next turn") };
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages(messages, model);

        // Should have: assistant, synthetic tool result, user message
        result.Count().ShouldBe(3);
        var synthetic = result[1] as ToolResultMessage;
        synthetic.ShouldNotBeNull();
        synthetic!.ToolCallId.ShouldBe("tc-orphan");
        synthetic.IsError.ShouldBeTrue();
    }

    [Fact]
    public void ErroredAssistantMessages_Skipped()
    {
        var errored = MakeAssistant([new TextContent("error")], reason: StopReason.Error);
        var messages = new Message[] { errored, MakeUser("retry") };
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages(messages, model);

        result.Count().ShouldBe(1);
        result[0].ShouldBeOfType<UserMessage>();
    }

    [Fact]
    public void AbortedAssistantMessages_Skipped()
    {
        var aborted = MakeAssistant([new TextContent("abort")], reason: StopReason.Aborted);
        var messages = new Message[] { aborted, MakeUser("retry") };
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages(messages, model);

        result.Count().ShouldBe(1);
        result[0].ShouldBeOfType<UserMessage>();
    }

    [Fact]
    public void ToolResult_ToolCallId_NormalizedToMatchTransformedToolCalls()
    {
        var tc = new ToolCallContent("abc!@#", "tool", new Dictionary<string, object?>());
        var assistant = MakeAssistant([tc]);
        var toolResult = new ToolResultMessage("abc!@#", "tool", [new TextContent("done")], false, Ts);
        var messages = new Message[] { assistant, toolResult };
        var model = MakeModel("anthropic", "anthropic-messages") with { Id = "different-model" };

        var result = MessageTransformer.TransformMessages(messages, model,
            (id, _, _) => id.Replace("!", "").Replace("@", "").Replace("#", ""));

        var trMsg = result[1] as ToolResultMessage;
        trMsg!.ToolCallId.ShouldBe("abc");
    }

    [Fact]
    public void ToolCallIds_NormalizerReceivesModelAndSource()
    {
        var tc = new ToolCallContent("tc-original", "tool", new Dictionary<string, object?>());
        var assistant = MakeAssistant([tc], provider: "openai", api: "openai-completions");
        var targetModel = MakeModel("anthropic", "anthropic-messages");
        LlmModel? seenModel = null;
        string? seenSource = null;

        _ = MessageTransformer.TransformMessages([assistant], targetModel, (id, model, source) =>
        {
            seenModel = model;
            seenSource = source;
            return id;
        });

        seenModel.ShouldNotBeNull();
        seenModel!.Provider.ShouldBe("openai");
        seenModel.Api.ShouldBe("openai-completions");
        seenSource.ShouldBe("anthropic");
    }

    [Fact]
    public void TransformMessages_WhenNormalizerNull_PreservesDefaultBehavior()
    {
        var tc = new ToolCallContent("tc-1", "tool", new Dictionary<string, object?>());
        var assistant = MakeAssistant([tc], provider: "openai", api: "openai-completions");
        var toolResult = new ToolResultMessage("tc-1", "tool", [new TextContent("ok")], false, Ts);
        var messages = new Message[] { assistant, toolResult };
        var targetModel = MakeModel("anthropic", "anthropic-messages");

        var baseline = MessageTransformer.TransformMessages(messages, targetModel);
        var withNullNormalizer = MessageTransformer.TransformMessages(messages, targetModel, null);

        withNullNormalizer.Count.ShouldBe(baseline.Count);
        for (var i = 0; i < baseline.Count; i++)
            withNullNormalizer[i].GetType().ShouldBe(baseline[i].GetType());
    }

    [Fact]
    public void RedactedThinking_Dropped_WhenSwitchingProviders()
    {
        var assistant = MakeAssistant(
            [new ThinkingContent("encrypted", Redacted: true)],
            provider: "openai",
            api: "openai-completions");
        var messages = new Message[] { assistant };
        var model = MakeModel("anthropic", "anthropic-messages");

        var result = MessageTransformer.TransformMessages(messages, model);

        var transformedAssistant = (AssistantMessage)result[0];
        transformedAssistant.Content.ShouldBeEmpty();
    }

    [Fact]
    public void RedactedThinking_Preserved_ForSameProvider()
    {
        var assistant = MakeAssistant(
            [new ThinkingContent("encrypted", Redacted: true)],
            provider: "anthropic",
            api: "anthropic-messages");
        var model = MakeModel("anthropic", "anthropic-messages");

        var result = MessageTransformer.TransformMessages([assistant], model);

        var transformedAssistant = (AssistantMessage)result[0];
        transformedAssistant.Content.ShouldHaveSingleItem();
        transformedAssistant.Content[0].ShouldBeOfType<ThinkingContent>();
        ((ThinkingContent)transformedAssistant.Content[0]).Redacted.ShouldBe(true);
    }

    [Fact]
    public void OrphanToolResultMessages_AreDroppedWithoutFailure()
    {
        // #3014 reverses this case's expectation. It previously asserted the orphan was PRESERVED;
        // that shape is exactly what makes Anthropic and the Copilot messages API return a hard 400.
        // The original intent - transforming an orphan must not throw, and the surrounding transcript
        // must survive intact - is preserved and strengthened: the orphan is now dropped instead.
        var orphan = new ToolResultMessage("missing-id", "test", [new TextContent("ok")], false, Ts);
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages([orphan, MakeUser("continue")], model);

        result.Count().ShouldBe(1);
        result.ShouldNotContain(orphan);
        result[0].ShouldBeOfType<UserMessage>();
    }

    [Fact]
    public void OrphanToolResult_IsDropped_WhenLeadingTruncatedTranscript()
    {
        // The #3014 shape produced by overflow compaction: the retained tail begins with a tool
        // result whose originating assistant tool call was cut away, followed by a legitimate
        // paired turn. Only the orphan is dropped.
        var paired = new ToolCallContent("tc-kept", "tool", new Dictionary<string, object?>());
        var messages = new Message[]
        {
            new ToolResultMessage("tc-dropped", "tool", [new TextContent("stranded")], false, Ts),
            MakeAssistant([paired], reason: StopReason.ToolUse),
            new ToolResultMessage("tc-kept", "tool", [new TextContent("kept")], false, Ts),
        };

        var result = MessageTransformer.TransformMessages(messages, MakeModel());

        result.OfType<ToolResultMessage>().Select(r => r.ToolCallId).ShouldBe(["tc-kept"]);
        result.Count.ShouldBe(2);
    }

    [Fact]
    public void OrphanToolResult_DropIsKeyedOnBaseCallId_NotTheCompositeId()
    {
        // The Responses API packs "call_id|item_id". Pairing must use the segment before the pipe,
        // otherwise every composite-id result would look orphaned and be dropped wholesale.
        var call = new ToolCallContent("call_x|fc_1", "tool", new Dictionary<string, object?>());
        var messages = new Message[]
        {
            MakeAssistant([call], reason: StopReason.ToolUse),
            new ToolResultMessage("call_x|fc_9", "tool", [new TextContent("ok")], false, Ts),
        };

        var result = MessageTransformer.TransformMessages(messages, MakeModel());

        result.OfType<ToolResultMessage>().ShouldHaveSingleItem()
            .ToolCallId.ShouldBe("call_x|fc_9");
    }

    [Fact]
    public void OrphanToolResult_DropSurvivesToolCallIdNormalization()
    {
        // The pairing set is populated from the POST-normalization tool call ids, and the tool result
        // id is rewritten by the same map earlier in the pass. A result that pairs before
        // normalization must still pair after it, or normalization itself would manufacture orphans.
        var call = new ToolCallContent("abc!@#", "tool", new Dictionary<string, object?>());
        var messages = new Message[]
        {
            MakeAssistant([call], provider: "openai", api: "openai-completions", reason: StopReason.ToolUse),
            new ToolResultMessage("abc!@#", "tool", [new TextContent("done")], false, Ts),
        };

        var result = MessageTransformer.TransformMessages(
            messages,
            MakeModel("anthropic", "anthropic-messages"),
            (id, _, _) => id.Replace("!", "").Replace("@", "").Replace("#", ""));

        result.OfType<ToolResultMessage>().ShouldHaveSingleItem().ToolCallId.ShouldBe("abc");
    }

    [Fact]
    public void OrphanToolResult_FromSkippedErroredAssistant_IsDropped()
    {
        // Sad path: the assistant turn that issued the call is skipped for StopReason.Error, so its
        // tool call never reaches the output. Its result is then an orphan and must go too - keeping
        // it would emit a tool_result with no originating call, the exact 400 shape.
        var call = new ToolCallContent("tc-errored", "tool", new Dictionary<string, object?>());
        var messages = new Message[]
        {
            MakeUser("go"),
            MakeAssistant([call], reason: StopReason.Error),
            new ToolResultMessage("tc-errored", "tool", [new TextContent("ignored")], false, Ts),
        };

        var result = MessageTransformer.TransformMessages(messages, MakeModel());

        result.OfType<ToolResultMessage>().ShouldBeEmpty();
        result.OfType<AssistantMessage>().ShouldBeEmpty();
        result.ShouldHaveSingleItem().ShouldBeOfType<UserMessage>();
    }

    [Fact]
    public void OrphanToolResult_ForwardReferenceToLaterCall_IsDropped()
    {
        // A tool result may only pair with a call that PRECEDES it. A result naming a call that only
        // appears later in the transcript is still invalid on the wire and must be dropped.
        var call = new ToolCallContent("tc-late", "tool", new Dictionary<string, object?>());
        var messages = new Message[]
        {
            new ToolResultMessage("tc-late", "tool", [new TextContent("early")], false, Ts),
            MakeAssistant([call], reason: StopReason.ToolUse),
        };

        var result = MessageTransformer.TransformMessages(messages, MakeModel());

        result.OfType<ToolResultMessage>().ShouldBeEmpty();
    }

    [Fact]
    public void PairedToolResults_AreUnaffectedByTheOrphanDrop()
    {
        // Non-vacuity guard for the drop: a well-formed call/result pair must pass through untouched,
        // so a mutation that dropped every tool result would redden here rather than pass silently.
        var call = new ToolCallContent("tc-ok", "tool", new Dictionary<string, object?>());
        var messages = new Message[]
        {
            MakeUser("go"),
            MakeAssistant([call], reason: StopReason.ToolUse),
            new ToolResultMessage("tc-ok", "tool", [new TextContent("done")], false, Ts),
        };

        var result = MessageTransformer.TransformMessages(messages, MakeModel());

        result.Count.ShouldBe(3);
        result.OfType<ToolResultMessage>().ShouldHaveSingleItem().ToolCallId.ShouldBe("tc-ok");
    }

    [Fact]
    public void SystemLikeMessages_KeepOriginalPosition()
    {
        var system = new SystemLikeMessage(Ts + 1);
        var assistant = MakeAssistant([new TextContent("ack")]);
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages([MakeUser("hi"), system, assistant, MakeUser("next")], model);

        result.Count().ShouldBe(4);
        result[1].ShouldBe(system);
        result[2].ShouldBeOfType<AssistantMessage>();
    }

    [Fact]
    public void ToolCallThoughtSignature_Removed_WhenSwitchingProviders()
    {
        var assistant = MakeAssistant(
            [new ToolCallContent("tc-1", "test", new Dictionary<string, object?>(), ThoughtSignature: "sig")],
            provider: "openai",
            api: "openai-completions");
        var model = MakeModel("anthropic", "anthropic-messages");

        var result = MessageTransformer.TransformMessages([assistant], model);

        var transformedAssistant = (AssistantMessage)result[0];
        var toolCall = transformedAssistant.Content.OfType<ToolCallContent>().Single();
        toolCall.ThoughtSignature.ShouldBeNull();
    }
}
