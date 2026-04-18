using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.AgentCore.Tests.Loop;

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

        result.Should().ContainSingle();
        result[0].Should().BeOfType<ProviderUserMessage>()
            .Which.Content.Text.Should().Be("hello");
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

        result.Should().ContainSingle();
        var providerMessage = result[0].Should().BeOfType<ProviderAssistantMessage>().Subject;
        providerMessage.Content.OfType<TextContent>().Should().ContainSingle(content => content.Text == "assistant text");
        providerMessage.Content.OfType<ToolCallContent>().Should().ContainSingle(call => call.Id == "tool-1");
        providerMessage.StopReason.Should().Be(StopReason.ToolUse);
        providerMessage.Usage.Input.Should().Be(10);
        providerMessage.Usage.Output.Should().Be(5);
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

        result.Should().ContainSingle();
        var providerToolResult = result[0].Should().BeOfType<ProviderToolResultMessage>().Subject;
        providerToolResult.ToolCallId.Should().Be("call-1");
        providerToolResult.ToolName.Should().Be("calculate");
        providerToolResult.IsError.Should().BeFalse();
        providerToolResult.Content.Should().ContainSingle()
            .Which.Should().BeOfType<TextContent>().Which.Text.Should().Be("2");
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

        var provider = MessageConverter.ToProviderMessages([original]).Single().Should().BeOfType<ProviderAssistantMessage>().Subject;
        var roundTripped = MessageConverter.ToAgentMessage(provider);

        roundTripped.Content.Should().Be("Round trip");
        roundTripped.FinishReason.Should().Be(StopReason.ToolUse);
        roundTripped.Usage.Should().NotBeNull();
        roundTripped.Usage!.InputTokens.Should().Be(3);
        roundTripped.Usage.OutputTokens.Should().Be(4);
        roundTripped.ToolCalls.Should().NotBeNull();
        roundTripped.ToolCalls!.Should().ContainSingle(call => call.Id == "tool-7");
        roundTripped.ContentBlocks.Should().NotBeNull();
        roundTripped.ContentBlocks!.OfType<ThinkingContent>().Should().ContainSingle(thinking => thinking.Thinking == "internal reasoning");
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

        result.Should().ContainSingle();
        result[0].Should().BeOfType<ProviderUserMessage>();
    }
}
