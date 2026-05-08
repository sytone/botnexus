using System.Text.Json;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using ProviderUserMessage = BotNexus.Agent.Providers.Core.Models.UserMessage;

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

        providerContext.SystemPrompt.ShouldBe("System prompt");
        providerContext.Messages.ShouldHaveSingleItem().ShouldBeOfType<ProviderUserMessage>();
        providerContext.Tools.ShouldNotBeNull();
        providerContext.Tools!.Count().ShouldBe(2);
        providerContext.Tools.Select(tool => tool.Name).ShouldBe(["calculate", "get_current_time"]);
    }

    [Fact]
    public void ToProviderTool_MapsDefinitionFields()
    {
        var expectedSchema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}}}""").RootElement.Clone();
        var tool = new StubTool("search", "Search docs", expectedSchema);

        var providerTool = ContextConverter.ToProviderTool(tool);

        providerTool.Name.ShouldBe("search");
        providerTool.Description.ShouldBe("Search docs");
        providerTool.Parameters.GetProperty("type").GetString().ShouldBe("object");
        providerTool.Parameters.GetProperty("properties").GetProperty("query").GetProperty("type").GetString().ShouldBe("string");
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
