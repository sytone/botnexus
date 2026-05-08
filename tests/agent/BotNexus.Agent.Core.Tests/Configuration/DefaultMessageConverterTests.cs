using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Configuration;

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

        result.ShouldHaveSingleItem();
        result[0].ShouldBeOfType<ProviderUserMessage>()
            .Content.Text.ShouldBe("hello");
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

        result.ShouldHaveSingleItem();
        var providerMessage = result[0].ShouldBeOfType<ProviderAssistantMessage>();
        providerMessage.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("assistant text");
        providerMessage.Content.OfType<ThinkingContent>().ShouldHaveSingleItem().Thinking.ShouldBe("thinking");
        providerMessage.Content.OfType<ToolCallContent>().ShouldHaveSingleItem().Id.ShouldBe("tool-1");
        providerMessage.StopReason.ShouldBe(StopReason.ToolUse);
        providerMessage.Usage.Input.ShouldBe(10);
        providerMessage.Usage.Output.ShouldBe(5);
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

        result.ShouldHaveSingleItem();
        var providerMessage = result[0].ShouldBeOfType<ProviderToolResultMessage>();
        providerMessage.ToolCallId.ShouldBe("call-1");
        providerMessage.ToolName.ShouldBe("calculate");
        providerMessage.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContent>()
            .Text.ShouldBe("2");
    }

    [Fact]
    public async Task Create_FiltersOutSystemMessages()
    {
        var converter = DefaultMessageConverter.Create();

        var result = await converter([new SystemAgentMessage("system context")], CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_ReturnsEmptyForEmptyOrNullMessages()
    {
        var converter = DefaultMessageConverter.Create();

        var emptyResult = await converter([], CancellationToken.None);
        var nullResult = await converter(null!, CancellationToken.None);

        emptyResult.ShouldBeEmpty();
        nullResult.ShouldBeEmpty();
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

        result.Count().ShouldBe(3);
        result[0].ShouldBeOfType<ProviderUserMessage>();
        result[1].ShouldBeOfType<ProviderAssistantMessage>();
        result[2].ShouldBeOfType<ProviderToolResultMessage>();
    }

    [Fact]
    public async Task Create_SkipsUnsupportedMessageShapes()
    {
        var converter = DefaultMessageConverter.Create();
        IReadOnlyList<AgentMessage> messages = [new UnknownAgentMessage(), new AgentUserMessage("ok")];

        var result = await converter(messages, CancellationToken.None);

        result.ShouldHaveSingleItem();
        result[0].ShouldBeOfType<ProviderUserMessage>();
    }

    private sealed record UnknownAgentMessage() : AgentMessage("user");
}
