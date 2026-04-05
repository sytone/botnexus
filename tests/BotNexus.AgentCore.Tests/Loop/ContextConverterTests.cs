using System.Text.Json;
using BotNexus.AgentCore.Loop;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.AgentCore.Tests.TestUtils;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.AgentCore.Tests.Loop;

using AgentUserMessage = BotNexus.AgentCore.Types.UserMessage;
using ProviderUserMessage = BotNexus.Providers.Core.Models.UserMessage;

public class ContextConverterTests
{
    [Fact]
    public async Task ToProviderContext_ConvertsMessagesAndTools()
    {
        var tools = new IAgentTool[] { new CalculateTool(), new GetCurrentTimeTool() };
        AgentContext context = new(
            SystemPrompt: "System prompt",
            Messages: [new AgentUserMessage("hello")],
            Tools: tools);

        var providerContext = await ContextConverter.ToProviderContext(
            context,
            (messages, _) => Task.FromResult<IReadOnlyList<Message>>(MessageConverter.ToProviderMessages(messages)),
            CancellationToken.None);

        providerContext.SystemPrompt.Should().Be("System prompt");
        providerContext.Messages.Should().ContainSingle().Which.Should().BeOfType<ProviderUserMessage>();
        providerContext.Tools.Should().NotBeNull();
        providerContext.Tools!.Should().HaveCount(2);
        providerContext.Tools.Select(tool => tool.Name).Should().BeEquivalentTo("calculate", "get_current_time");
    }

    [Fact]
    public void ToProviderTool_MapsDefinitionFields()
    {
        var expectedSchema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}}}""").RootElement.Clone();
        var tool = new StubTool("search", "Search docs", expectedSchema);

        var providerTool = ContextConverter.ToProviderTool(tool);

        providerTool.Name.Should().Be("search");
        providerTool.Description.Should().Be("Search docs");
        providerTool.Parameters.GetProperty("type").GetString().Should().Be("object");
        providerTool.Parameters.GetProperty("properties").GetProperty("query").GetProperty("type").GetString().Should().Be("string");
    }

    private sealed class StubTool(string name, string description, JsonElement schema) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, description, schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
        }
    }
}
