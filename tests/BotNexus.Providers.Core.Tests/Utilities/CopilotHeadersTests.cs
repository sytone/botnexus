using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Utilities;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Utilities;

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

        CopilotHeaders.InferInitiator(messages).Should().Be("user");
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

        CopilotHeaders.InferInitiator(messages).Should().Be("agent");
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

        CopilotHeaders.InferInitiator(messages).Should().Be("agent");
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

        CopilotHeaders.HasVisionInput(messages).Should().BeTrue();
    }

    [Fact]
    public void HasVisionInput_False_WhenNoImages()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("just text"), Ts)
        };

        CopilotHeaders.HasVisionInput(messages).Should().BeFalse();
    }

    [Fact]
    public void BuildDynamicHeaders_IncludesXInitiator()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello"), Ts)
        };

        var headers = CopilotHeaders.BuildDynamicHeaders(messages, hasImages: false);

        headers.Should().ContainKey("X-Initiator").WhoseValue.Should().Be("user");
    }

    [Fact]
    public void BuildDynamicHeaders_IncludesCopilotVisionRequest_WhenImagesPresent()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello"), Ts)
        };

        var headers = CopilotHeaders.BuildDynamicHeaders(messages, hasImages: true);

        headers.Should().ContainKey("Copilot-Vision-Request").WhoseValue.Should().Be("true");
    }

    [Fact]
    public void BuildDynamicHeaders_OmitsCopilotVisionRequest_WhenNoImages()
    {
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello"), Ts)
        };

        var headers = CopilotHeaders.BuildDynamicHeaders(messages, hasImages: false);

        headers.Should().NotContainKey("Copilot-Vision-Request");
    }
}
