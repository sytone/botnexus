using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.AgentCore.Tests.Configuration;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using ProviderUserMessage = BotNexus.Agent.Providers.Core.Models.UserMessage;
using ProviderAssistantMessage = BotNexus.Agent.Providers.Core.Models.AssistantMessage;
using ProviderToolResultMessage = BotNexus.Agent.Providers.Core.Models.ToolResultMessage;

public class DefaultMessageConverterTests
{
    [Fact]
    public async Task Create_ConvertsUserMessage()
    {
        var converter = DefaultMessageConverter.Create();

        var result = await converter([new AgentUserMessage("hello")], CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Should().BeOfType<ProviderUserMessage>()
            .Which.Content.Text.Should().Be("hello");
    }

    [Fact]
    public async Task Create_ConvertsAssistantMessage()
    {
        var converter = DefaultMessageConverter.Create();
        var message = new AssistantAgentMessage(
            Content: "assistant text",
            ContentBlocks:
            [
                new TextContent("assistant text"),
                new ThinkingContent("thinking"),
                new ToolCallContent("tool-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" })
            ],
            ToolCalls: [new ToolCallContent("tool-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" })],
            FinishReason: StopReason.ToolUse,
            Usage: new AgentUsage(10, 5));

        var result = await converter([message], CancellationToken.None);

        result.Should().ContainSingle();
        var providerMessage = result[0].Should().BeOfType<ProviderAssistantMessage>().Subject;
        providerMessage.Content.OfType<TextContent>().Should().ContainSingle(content => content.Text == "assistant text");
        providerMessage.Content.OfType<ThinkingContent>().Should().ContainSingle(content => content.Thinking == "thinking");
        providerMessage.Content.OfType<ToolCallContent>().Should().ContainSingle(call => call.Id == "tool-1");
        providerMessage.StopReason.Should().Be(StopReason.ToolUse);
        providerMessage.Usage.Input.Should().Be(10);
        providerMessage.Usage.Output.Should().Be(5);
    }

    [Fact]
    public async Task Create_ConvertsToolResultMessage()
    {
        var converter = DefaultMessageConverter.Create();
        var message = new ToolResultAgentMessage(
            ToolCallId: "call-1",
            ToolName: "calculate",
            Result: new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "2")]));

        var result = await converter([message], CancellationToken.None);

        result.Should().ContainSingle();
        var providerMessage = result[0].Should().BeOfType<ProviderToolResultMessage>().Subject;
        providerMessage.ToolCallId.Should().Be("call-1");
        providerMessage.ToolName.Should().Be("calculate");
        providerMessage.Content.Should().ContainSingle().Which.Should().BeOfType<TextContent>().Which.Text.Should().Be("2");
    }

    [Fact]
    public async Task Create_FiltersOutSystemMessages()
    {
        var converter = DefaultMessageConverter.Create();

        var result = await converter([new SystemAgentMessage("system context")], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ReturnsEmptyForEmptyOrNullMessages()
    {
        var converter = DefaultMessageConverter.Create();

        var emptyResult = await converter([], CancellationToken.None);
        var nullResult = await converter(null!, CancellationToken.None);

        emptyResult.Should().BeEmpty();
        nullResult.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ConvertsMixedMessageTypes()
    {
        var converter = DefaultMessageConverter.Create();
        IReadOnlyList<AgentMessage> messages =
        [
            new AgentUserMessage("user"),
            new AssistantAgentMessage("assistant"),
            new ToolResultAgentMessage("call-1", "tool", new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")])),
            new SystemAgentMessage("summary")
        ];

        var result = await converter(messages, CancellationToken.None);

        result.Should().HaveCount(3);
        result[0].Should().BeOfType<ProviderUserMessage>();
        result[1].Should().BeOfType<ProviderAssistantMessage>();
        result[2].Should().BeOfType<ProviderToolResultMessage>();
    }

    [Fact]
    public async Task Create_SkipsUnsupportedMessageShapes()
    {
        var converter = DefaultMessageConverter.Create();
        IReadOnlyList<AgentMessage> messages = [new UnknownAgentMessage(), new AgentUserMessage("ok")];

        var result = await converter(messages, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Should().BeOfType<ProviderUserMessage>();
    }

    private sealed record UnknownAgentMessage() : AgentMessage("user");
}
