using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

public class CopilotHeadersTests
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void InferInitiator_ReturnsUser_WhenLastMessageIsUser()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello"), Ts)
        };

        CopilotHeaders.InferInitiator(messages).ShouldBe("user");
    }

    [Fact]
    public void InferInitiator_ReturnsAgent_WhenLastMessageIsAssistant()
    {
        var messages = new Message[]
        {
            new AssistantMessage(
                Content: [new TextContent("hi")],
                Api: "test", Provider: "test", ModelId: "test",
                Usage: Usage.Empty(), StopReason: StopReason.Stop,
                ErrorMessage: null, ResponseId: null, Timestamp: Ts)
        };

        CopilotHeaders.InferInitiator(messages).ShouldBe("agent");
    }

    [Fact]
    public void InferInitiator_SkipsToolResults_FindsLastNonToolMessage()
    {
        var messages = new Message[]
        {
            new AssistantMessage(
                Content: [new ToolCallContent("tc1", "tool", new Dictionary<string, object?>())],
                Api: "test", Provider: "test", ModelId: "test",
                Usage: Usage.Empty(), StopReason: StopReason.ToolUse,
                ErrorMessage: null, ResponseId: null, Timestamp: Ts),
            new ToolResultMessage("tc1", "tool", [new TextContent("result")], false, Ts)
        };

        CopilotHeaders.InferInitiator(messages).ShouldBe("agent");
    }

    [Fact]
    public void HasVisionInput_True_WhenUserMessageHasImages()
    {
        var blocks = new ContentBlock[]
        {
            new TextContent("look at this"),
            new ImageContent("base64data", "image/png")
        };
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent(blocks), Ts)
        };

        CopilotHeaders.HasVisionInput(messages).ShouldBeTrue();
    }

    [Fact]
    public void HasVisionInput_False_WhenNoImages()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("just text"), Ts)
        };

        CopilotHeaders.HasVisionInput(messages).ShouldBeFalse();
    }

    [Fact]
    public void BuildDynamicHeaders_IncludesXInitiator()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello"), Ts)
        };

        var headers = CopilotHeaders.BuildDynamicHeaders(messages, hasImages: false);

        headers.ShouldContainKey("X-Initiator");
        headers["X-Initiator"].ShouldBe("user");
    }

    [Fact]
    public void BuildDynamicHeaders_IncludesCopilotVisionRequest_WhenImagesPresent()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello"), Ts)
        };

        var headers = CopilotHeaders.BuildDynamicHeaders(messages, hasImages: true);

        headers.ShouldContainKey("Copilot-Vision-Request");
        headers["Copilot-Vision-Request"].ShouldBe("true");
    }

    [Fact]
    public void BuildDynamicHeaders_OmitsCopilotVisionRequest_WhenNoImages()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello"), Ts)
        };

        var headers = CopilotHeaders.BuildDynamicHeaders(messages, hasImages: false);

        headers.ShouldNotContainKey("Copilot-Vision-Request");
    }
}
