using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Utilities;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Utilities;

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

        result.Should().HaveCount(1);
        result[0].Should().BeOfType<UserMessage>();
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
        assistantResult.Should().NotBeNull();
        assistantResult!.Content[0].Should().BeOfType<TextContent>();
        var text = (TextContent)assistantResult.Content[0];
        text.Text.Should().Be("deep thought");
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
        assistantResult!.Content[0].Should().BeOfType<ThinkingContent>();
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
            id => "normalized-" + id);

        var assistantResult = result[0] as AssistantMessage;
        var normalizedTc = assistantResult!.Content[0] as ToolCallContent;
        normalizedTc!.Id.Should().Be("normalized-tc-original");

        var toolResultMsg = result[1] as ToolResultMessage;
        toolResultMsg!.ToolCallId.Should().Be("normalized-tc-original");
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
        result.Should().HaveCount(3);
        var synthetic = result[1] as ToolResultMessage;
        synthetic.Should().NotBeNull();
        synthetic!.ToolCallId.Should().Be("tc-orphan");
        synthetic.IsError.Should().BeTrue();
    }

    [Fact]
    public void ErroredAssistantMessages_Skipped()
    {
        var errored = MakeAssistant([new TextContent("error")], reason: StopReason.Error);
        var messages = new Message[] { errored, MakeUser("retry") };
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages(messages, model);

        result.Should().HaveCount(1);
        result[0].Should().BeOfType<UserMessage>();
    }

    [Fact]
    public void AbortedAssistantMessages_Skipped()
    {
        var aborted = MakeAssistant([new TextContent("abort")], reason: StopReason.Aborted);
        var messages = new Message[] { aborted, MakeUser("retry") };
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages(messages, model);

        result.Should().HaveCount(1);
        result[0].Should().BeOfType<UserMessage>();
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
            id => id.Replace("!", "").Replace("@", "").Replace("#", ""));

        var trMsg = result[1] as ToolResultMessage;
        trMsg!.ToolCallId.Should().Be("abc");
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
        transformedAssistant.Content.Should().BeEmpty();
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
        transformedAssistant.Content.Should().ContainSingle();
        transformedAssistant.Content[0].Should().BeOfType<ThinkingContent>();
        ((ThinkingContent)transformedAssistant.Content[0]).Redacted.Should().BeTrue();
    }

    [Fact]
    public void OrphanToolResultMessages_ArePreservedWithoutFailure()
    {
        var orphan = new ToolResultMessage("missing-id", "test", [new TextContent("ok")], false, Ts);
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages([orphan, MakeUser("continue")], model);

        result.Should().HaveCount(2);
        result[0].Should().Be(orphan);
        result[1].Should().BeOfType<UserMessage>();
    }

    [Fact]
    public void SystemLikeMessages_KeepOriginalPosition()
    {
        var system = new SystemLikeMessage(Ts + 1);
        var assistant = MakeAssistant([new TextContent("ack")]);
        var model = MakeModel();

        var result = MessageTransformer.TransformMessages([MakeUser("hi"), system, assistant, MakeUser("next")], model);

        result.Should().HaveCount(4);
        result[1].Should().Be(system);
        result[2].Should().BeOfType<AssistantMessage>();
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
        toolCall.ThoughtSignature.Should().BeNull();
    }
}
