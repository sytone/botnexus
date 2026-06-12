using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class ListAgentsToolTests
{
    private static AgentDescriptor MakeAgent(string id, string? description = null, string? emoji = null, IReadOnlyList<string>? capabilities = null)
    {
        var meta = new Dictionary<string, object?>();
        if (capabilities is not null)
            meta["capabilities"] = JsonDocument.Parse(
                JsonSerializer.Serialize(capabilities)).RootElement;
        return new AgentDescriptor
        {
            AgentId = AgentId.From(id),
            DisplayName = id.ToUpperInvariant(),
            Description = description,
            Emoji = emoji,
            ModelId = "test-model",
            ApiProvider = "test",
            Metadata = meta
        };
    }

    private static Mock<IAgentRegistry> MakeRegistry(params AgentDescriptor[] agents)
    {
        var mock = new Mock<IAgentRegistry>();
        mock.Setup(r => r.GetAll()).Returns(agents.ToList());
        foreach (var a in agents)
            mock.Setup(r => r.Get(a.AgentId)).Returns(a);
        return mock;
    }

    [Fact]
    public void Tool_HasExpectedNameAndLabel()
    {
        var tool = new ListAgentsTool(Mock.Of<IAgentRegistry>(), AgentId.From("caller"));
        tool.Name.ShouldBe("list_agents");
        tool.Label.ShouldBe("List Agents");
    }

    [Fact]
    public async Task ExecuteAsync_NoFilter_ReturnsAllAgentsOrdered()
    {
        var registry = MakeRegistry(
            MakeAgent("zebra"),
            MakeAgent("alpha"),
            MakeAgent("mango"));
        registry.Setup(r => r.Get(AgentId.From("caller"))).Returns((AgentDescriptor?)null);

        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));
        var result = await tool.ExecuteAsync("t1", new Dictionary<string, object?>());

        var json = result.Content[0].Value;
        var list = JsonSerializer.Deserialize<List<JsonElement>>(json)!;
        list.Count.ShouldBe(3);
        list[0].GetProperty("agentId").GetString().ShouldBe("alpha");
        list[1].GetProperty("agentId").GetString().ShouldBe("mango");
        list[2].GetProperty("agentId").GetString().ShouldBe("zebra");
    }

    [Fact]
    public async Task ExecuteAsync_WithFilter_ReturnsMatchingAgents()
    {
        var registry = MakeRegistry(
            MakeAgent("coder", "Expert at coding tasks"),
            MakeAgent("planner", "Project planning specialist"));
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));
        var args = new Dictionary<string, object?> { ["filter"] = "coder" };
        var result = await tool.ExecuteAsync("t1", args);

        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list.Count.ShouldBe(1);
        list[0].GetProperty("agentId").GetString().ShouldBe("coder");
    }

    [Fact]
    public async Task ExecuteAsync_FilterMatchesDescription_ReturnsAgent()
    {
        var registry = MakeRegistry(
            MakeAgent("nova", "Handles documentation and writing"),
            MakeAgent("codey", "Writes and reviews code"));
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));
        var args = new Dictionary<string, object?> { ["filter"] = "documentation" };
        var result = await tool.ExecuteAsync("t1", args);

        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list.Count.ShouldBe(1);
        list[0].GetProperty("agentId").GetString().ShouldBe("nova");
    }

    [Fact]
    public async Task ExecuteAsync_WithCapabilityFilter_ReturnsMatchingAgents()
    {
        var registry = MakeRegistry(
            MakeAgent("researcher", capabilities: ["research", "web-search"]),
            MakeAgent("coder", capabilities: ["code", "review"]));
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));
        var args = new Dictionary<string, object?> { ["capability"] = "research" };
        var result = await tool.ExecuteAsync("t1", args);

        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list.Count.ShouldBe(1);
        list[0].GetProperty("agentId").GetString().ShouldBe("researcher");
    }

    [Fact]
    public async Task ExecuteAsync_OpenPolicy_AllAgentsCanConverse()
    {
        var registry = MakeRegistry(MakeAgent("specialist"), MakeAgent("other"));
        registry.Setup(r => r.Get(AgentId.From("caller"))).Returns((AgentDescriptor?)null);

        var options = new AgentExchangeOptions { AccessPolicy = "open" };
        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"), options);
        var result = await tool.ExecuteAsync("t1", new Dictionary<string, object?>());

        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list.ShouldAllBe(e => e.GetProperty("canConverse").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_WhitelistPolicy_OnlyWhitelistedCanConverse()
    {
        var callerDescriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("caller"),
            DisplayName = "Caller",
            ModelId = "m",
            ApiProvider = "p",
            SubAgentIds = ["specialist"]
        };
        var specialist = MakeAgent("specialist");
        var other = MakeAgent("other");

        var registry = MakeRegistry(specialist, other);
        registry.Setup(r => r.Get(AgentId.From("caller"))).Returns(callerDescriptor);

        var options = new AgentExchangeOptions { AccessPolicy = "whitelist" };
        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"), options);
        var result = await tool.ExecuteAsync("t1", new Dictionary<string, object?>());

        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        var specialistEntry = list.First(e => e.GetProperty("agentId").GetString() == "specialist");
        var otherEntry = list.First(e => e.GetProperty("agentId").GetString() == "other");

        specialistEntry.GetProperty("canConverse").GetBoolean().ShouldBeTrue();
        otherEntry.GetProperty("canConverse").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_NullOptions_DefaultsToOpen()
    {
        var registry = MakeRegistry(MakeAgent("target"));
        registry.Setup(r => r.Get(AgentId.From("caller"))).Returns((AgentDescriptor?)null);

        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"), exchangeOptions: null);
        var result = await tool.ExecuteAsync("t1", new Dictionary<string, object?>());

        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list[0].GetProperty("canConverse").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CallerHasSubAgentId_CanConverseIsTrue()
    {
        var callerDescriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("caller"),
            DisplayName = "Caller",
            ModelId = "m",
            ApiProvider = "p",
            SubAgentIds = ["specialist"]
        };
        var specialist = MakeAgent("specialist");
        var other = MakeAgent("other");

        var registry = MakeRegistry(specialist, other);
        registry.Setup(r => r.Get(AgentId.From("caller"))).Returns(callerDescriptor);

        // Default null options → open policy → all canConverse
        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));
        var result = await tool.ExecuteAsync("t1", new Dictionary<string, object?>());

        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list.ShouldAllBe(e => e.GetProperty("canConverse").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_EmptyRegistry_ReturnsEmptyList()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([]);
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));
        var result = await tool.ExecuteAsync("t1", new Dictionary<string, object?>());

        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_FilterMatchesNone_ReturnsEmptyList()
    {
        var registry = MakeRegistry(MakeAgent("coder"), MakeAgent("planner"));
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));
        var args = new Dictionary<string, object?> { ["filter"] = "zzz-no-match" };
        var result = await tool.ExecuteAsync("t1", args);

        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_AgentWithEmojiAndDescription_IncludedInOutput()
    {
        var registry = MakeRegistry(MakeAgent("farnsworth", "Platform engineer", "🔬"));
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));
        var result = await tool.ExecuteAsync("t1", new Dictionary<string, object?>());

        var json = result.Content[0].Value;
        json.ShouldContain("farnsworth");
        json.ShouldContain("Platform engineer");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_ReturnsArgumentsUnchanged()
    {
        var tool = new ListAgentsTool(Mock.Of<IAgentRegistry>(), AgentId.From("caller"));
        var args = new Dictionary<string, object?> { ["filter"] = "test" };
        var result = await tool.PrepareArgumentsAsync(args);
        result.ShouldBe(args);
    }
}
