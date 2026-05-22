using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class PlatformAdminToolTests
{
    private static AgentDescriptor MakeAgent(string id) => new()
    {
        AgentId = AgentId.From(id),
        DisplayName = id.ToUpperInvariant(),
        ModelId = "test-model",
        ApiProvider = "test-provider"
    };

    private static (PlatformAdminTool tool, Mock<IAgentRegistry> registry, Mock<IAgentSupervisor> supervisor, Mock<IAgentConfigurationWriter> writer)
        MakeTool(params AgentDescriptor[] agents)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns(agents.ToList());
        foreach (var a in agents)
        {
            registry.Setup(r => r.Get(a.AgentId)).Returns(a);
            registry.Setup(r => r.Contains(a.AgentId)).Returns(true);
        }
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        // override for registered agents
        foreach (var a in agents)
        {
            registry.Setup(r => r.Get(a.AgentId)).Returns(a);
            registry.Setup(r => r.Contains(a.AgentId)).Returns(true);
        }

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetAllInstances()).Returns([]);

        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        writer.Setup(w => w.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var config = new PlatformConfig { Version = 1 };
        var monitor = new TestOptionsMonitor<PlatformConfig>(config);

        var tool = new PlatformAdminTool(registry.Object, supervisor.Object, writer.Object, monitor);
        return (tool, registry, supervisor, writer);
    }

    private static Dictionary<string, object?> Args(string action, params (string key, string value)[] extras)
    {
        var d = new Dictionary<string, object?> { ["action"] = JsonDocument.Parse($"\"{action}\"").RootElement };
        foreach (var (k, v) in extras)
            d[k] = JsonDocument.Parse($"\"{v}\"").RootElement;
        return d;
    }

    private static bool IsSuccess(AgentToolResult r) =>
        !r.Content[0].Value.StartsWith("Error:", StringComparison.Ordinal);

    private static bool IsError(AgentToolResult r) =>
        r.Content[0].Value.StartsWith("Error:", StringComparison.Ordinal);

    // ─── Tool metadata ───────────────────────────────────────────────────────

    [Fact]
    public void Tool_HasExpectedNameAndLabel()
    {
        var (tool, _, _, _) = MakeTool();
        tool.Name.ShouldBe("botnexus_admin");
        tool.Label.ShouldBe("BotNexus Platform Admin");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_ReturnsArgumentsUnchanged()
    {
        var (tool, _, _, _) = MakeTool();
        var args = new Dictionary<string, object?> { ["action"] = "list_agents" };
        var result = await tool.PrepareArgumentsAsync(args);
        result.ShouldBe(args);
    }

    // ─── get_config ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetConfig_ReturnsConfigSummary()
    {
        var (tool, _, _, _) = MakeTool();
        var result = await tool.ExecuteAsync("t1", Args("get_config"));
        IsSuccess(result).ShouldBeTrue();
        result.Content[0].Value.ShouldContain("version");
    }

    // ─── list_agents ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAgents_ReturnsAllAgentsOrdered()
    {
        var (tool, _, _, _) = MakeTool(MakeAgent("zebra"), MakeAgent("alpha"), MakeAgent("mango"));
        var result = await tool.ExecuteAsync("t1", Args("list_agents"));
        IsSuccess(result).ShouldBeTrue();
        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list.Count.ShouldBe(3);
        list[0].GetProperty("agentId").GetString().ShouldBe("alpha");
        list[2].GetProperty("agentId").GetString().ShouldBe("zebra");
    }

    [Fact]
    public async Task ListAgents_RunningAgentFlaggedCorrectly()
    {
        var agent = MakeAgent("worker");
        var (tool, _, supervisor, _) = MakeTool(agent);
        var instance = new AgentInstance
        {
            InstanceId = "inst-1",
            AgentId = agent.AgentId,
            SessionId = SessionId.Create(),
            Status = AgentInstanceStatus.Running,
            IsolationStrategy = "in-process"
        };
        supervisor.Setup(s => s.GetAllInstances()).Returns([instance]);

        var result = await tool.ExecuteAsync("t1", Args("list_agents"));
        var list = JsonSerializer.Deserialize<List<JsonElement>>(result.Content[0].Value)!;
        list[0].GetProperty("isRunning").GetBoolean().ShouldBeTrue();
    }

    // ─── get_platform_status ─────────────────────────────────────────────────

    [Fact]
    public async Task GetPlatformStatus_ReturnsCountsAndServerTime()
    {
        var (tool, _, _, _) = MakeTool(MakeAgent("a"), MakeAgent("b"));
        var result = await tool.ExecuteAsync("t1", Args("get_platform_status"));
        IsSuccess(result).ShouldBeTrue();
        var obj = JsonSerializer.Deserialize<JsonElement>(result.Content[0].Value);
        obj.GetProperty("totalAgents").GetInt32().ShouldBe(2);
        obj.GetProperty("runningAgents").GetInt32().ShouldBe(0);
        obj.GetProperty("serverTime").GetString().ShouldNotBeNullOrEmpty();
    }

    // ─── create_agent ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAgent_RegistersAndPersists()
    {
        var (tool, registry, _, writer) = MakeTool();
        var args = Args("create_agent",
            ("agentId", "new-bot"),
            ("displayName", "New Bot"),
            ("modelId", "gpt-4o"),
            ("apiProvider", "openai"));
        var result = await tool.ExecuteAsync("t1", args);
        IsSuccess(result).ShouldBeTrue();
        result.Content[0].Value.ShouldContain("new-bot");
        registry.Verify(r => r.Register(It.Is<AgentDescriptor>(d => d.AgentId.ToString() == "new-bot")), Times.Once);
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAgent_MissingRequiredField_ReturnsError()
    {
        var (tool, _, _, _) = MakeTool();
        var args = Args("create_agent",
            ("agentId", "new-bot"),
            ("displayName", "New Bot"));
        // missing modelId and apiProvider
        var result = await tool.ExecuteAsync("t1", args);
        IsError(result).ShouldBeTrue();
        result.Content[0].Value.ShouldContain("requires modelId");
    }

    [Fact]
    public async Task CreateAgent_DuplicateId_ReturnsError()
    {
        var (tool, _, _, _) = MakeTool(MakeAgent("existing"));
        var args = Args("create_agent",
            ("agentId", "existing"),
            ("displayName", "Existing"),
            ("modelId", "m"),
            ("apiProvider", "p"));
        var result = await tool.ExecuteAsync("t1", args);
        IsError(result).ShouldBeTrue();
        result.Content[0].Value.ShouldContain("already exists");
    }

    // ─── update_agent ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAgent_UpdatesAndPersists()
    {
        var existing = MakeAgent("bot1");
        var (tool, registry, _, writer) = MakeTool(existing);
        registry.Setup(r => r.Update(existing.AgentId, It.IsAny<AgentDescriptor>())).Returns(true);

        var args = Args("update_agent",
            ("agentId", "bot1"),
            ("displayName", "Updated Bot"));
        var result = await tool.ExecuteAsync("t1", args);
        IsSuccess(result).ShouldBeTrue();
        result.Content[0].Value.ShouldContain("updated successfully");
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAgent_NotFound_ReturnsError()
    {
        var (tool, _, _, _) = MakeTool();
        var result = await tool.ExecuteAsync("t1", Args("update_agent", ("agentId", "ghost")));
        IsError(result).ShouldBeTrue();
        result.Content[0].Value.ShouldContain("not found");
    }

    // ─── delete_agent ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAgent_UnregistersAndDeletes()
    {
        var agent = MakeAgent("old-bot");
        var (tool, registry, _, writer) = MakeTool(agent);

        var result = await tool.ExecuteAsync("t1", Args("delete_agent", ("agentId", "old-bot")));
        IsSuccess(result).ShouldBeTrue();
        result.Content[0].Value.ShouldContain("deleted successfully");
        registry.Verify(r => r.Unregister(agent.AgentId), Times.Once);
        writer.Verify(w => w.DeleteAsync("old-bot", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAgent_NotFound_ReturnsError()
    {
        var (tool, _, _, _) = MakeTool();
        var result = await tool.ExecuteAsync("t1", Args("delete_agent", ("agentId", "ghost")));
        IsError(result).ShouldBeTrue();
        result.Content[0].Value.ShouldContain("not found");
    }

    // ─── unknown action ──────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var (tool, _, _, _) = MakeTool();
        var result = await tool.ExecuteAsync("t1", Args("nuke_everything"));
        IsError(result).ShouldBeTrue();
        result.Content[0].Value.ShouldContain("Unknown action");
    }

    [Fact]
    public async Task MissingAction_ReturnsError()
    {
        var (tool, _, _, _) = MakeTool();
        var result = await tool.ExecuteAsync("t1", new Dictionary<string, object?>());
        IsError(result).ShouldBeTrue();
    }
}
