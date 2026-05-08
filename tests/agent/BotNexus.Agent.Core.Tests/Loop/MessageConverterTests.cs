using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using ProviderUserMessage = BotNexus.Agent.Providers.Core.Models.UserMessage;
using ProviderAssistantMessage = BotNexus.Agent.Providers.Core.Models.AssistantMessage;
using ProviderToolResultMessage = BotNexus.Agent.Providers.Core.Models.ToolResultMessage;

public class MessageConverterTests
{
    [Fact]
    public void ToProviderMessages_ConvertsUserMessage()
    {
        IReadOnlyList<AgentMessage> messages = [new AgentUserMessage("hello")];

        var result = MessageConverter.ToProviderMessages(messages);

        result.ShouldHaveSingleItem();
        result[0].ShouldBeOfType<ProviderUserMessage>()
            .Content.Text.ShouldBe("hello");
    }

    [Fact]
    public void ToProviderMessages_ConvertsAssistantMessage()
    {
        var agentMessage = new AssistantAgentMessage(
            Content: "assistant text",
            ToolCalls: [new ToolCallContent("tool-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" })],
            FinishReason: StopReason.ToolUse,
            Usage: new AgentUsage(10, 5));

        var result = MessageConverter.ToProviderMessages([agentMessage]);

        result.ShouldHaveSingleItem();
        var providerMessage = result[0].ShouldBeOfType<ProviderAssistantMessage>();
        providerMessage.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("assistant text");
        providerMessage.Content.OfType<ToolCallContent>().ShouldHaveSingleItem().Id.ShouldBe("tool-1");
        providerMessage.StopReason.ShouldBe(StopReason.ToolUse);
        providerMessage.Usage.Input.ShouldBe(10);
        providerMessage.Usage.Output.ShouldBe(5);
    }

    [Fact]
    public void ToProviderMessages_ConvertsToolResultMessage()
    {
        var toolMessage = new ToolResultAgentMessage(
            ToolCallId: "call-1",
            ToolName: "calculate",
            Result: new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "2")]),
            IsError: false);

        var result = MessageConverter.ToProviderMessages([toolMessage]);

        result.ShouldHaveSingleItem();
        var providerToolResult = result[0].ShouldBeOfType<ProviderToolResultMessage>();
        providerToolResult.ToolCallId.ShouldBe("call-1");
        providerToolResult.ToolName.ShouldBe("calculate");
        providerToolResult.IsError.ShouldBeFalse();
        providerToolResult.Content.ShouldHaveSingleItem()
            .ShouldBeOfType<TextContent>().Text.ShouldBe("2");
    }

    [Fact]
    public void AssistantMessage_CanRoundTripThroughProviderConversion()
    {
        var original = new AssistantAgentMessage(
            Content: "Round trip",
            ContentBlocks:
            [
                new TextContent("Round trip"),
                new ThinkingContent("internal reasoning", "sig-1"),
                new ToolCallContent("tool-7", "get_current_time", new Dictionary<string, object?>())
            ],
            ToolCalls: [new ToolCallContent("tool-7", "get_current_time", new Dictionary<string, object?>())],
            FinishReason: StopReason.ToolUse,
            Usage: new AgentUsage(3, 4),
            ErrorMessage: null,
            Timestamp: DateTimeOffset.UtcNow);

        var provider = MessageConverter.ToProviderMessages([original]).Single().ShouldBeOfType<ProviderAssistantMessage>();
        var roundTripped = MessageConverter.ToAgentMessage(provider);

        roundTripped.Content.ShouldBe("Round trip");
        roundTripped.FinishReason.ShouldBe(StopReason.ToolUse);
        roundTripped.Usage.ShouldNotBeNull();
        roundTripped.Usage!.InputTokens.ShouldBe(3);
        roundTripped.Usage.OutputTokens.ShouldBe(4);
        roundTripped.ToolCalls.ShouldNotBeNull();
        roundTripped.ToolCalls!.ShouldHaveSingleItem().Id.ShouldBe("tool-7");
        roundTripped.ContentBlocks.ShouldNotBeNull();
        roundTripped.ContentBlocks!.OfType<ThinkingContent>().ShouldHaveSingleItem().Thinking.ShouldBe("internal reasoning");
    }

    [Fact]
    public void ToProviderMessages_FiltersUnknownMessageTypes()
    {
        IReadOnlyList<AgentMessage> messages =
        [
            new SystemAgentMessage("system"),
            new AgentUserMessage("user")
        ];

        var result = MessageConverter.ToProviderMessages(messages);

        result.ShouldHaveSingleItem();
        result[0].ShouldBeOfType<ProviderUserMessage>();
    }
}
