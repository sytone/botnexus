using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="BuiltInAgentConfigurationSource"/>.
/// </summary>
public sealed class BuiltInAgentConfigurationSourceTests
{
    private readonly BuiltInAgentConfigurationSource _source = new();

    [Fact]
    public async Task LoadAsync_ReturnsAllSixBuiltInAgents()
    {
        var agents = await _source.LoadAsync();

        agents.Count.ShouldBe(6);
    }

    [Theory]
    [InlineData("researcher")]
    [InlineData("coder")]
    [InlineData("planner")]
    [InlineData("reviewer")]
    [InlineData("writer")]
    [InlineData("analyst")]
    public async Task LoadAsync_ContainsExpectedAgentId(string agentId)
    {
        var agents = await _source.LoadAsync();

        agents.ShouldContain(a => a.AgentId.Value == agentId,
            $"built-in agents must include '{agentId}'");
    }

    [Theory]
    [InlineData("researcher")]
    [InlineData("coder")]
    [InlineData("planner")]
    [InlineData("reviewer")]
    [InlineData("writer")]
    [InlineData("analyst")]
    public async Task LoadAsync_EachAgentHasRoleMetadataMatchingId(string agentId)
    {
        var agents = await _source.LoadAsync();
        var agent = agents.Single(a => a.AgentId.Value == agentId);

        agent.Metadata.ShouldContainKey("role",
            $"agent '{agentId}' must have a 'role' metadata entry");
        agent.Metadata["role"].ShouldBe(agentId,
            $"agent '{agentId}' role metadata must equal its ID for SubAgentRoles matching");
    }

    [Theory]
    [InlineData("researcher", "web_search")]
    [InlineData("researcher", "web_fetch")]
    [InlineData("researcher", "memory_search")]
    [InlineData("coder", "shell")]
    [InlineData("coder", "exec")]
    [InlineData("coder", "write")]
    [InlineData("planner", "memory_save")]
    [InlineData("reviewer", "shell")]
    [InlineData("writer", "write")]
    [InlineData("analyst", "shell")]
    [InlineData("analyst", "exec")]
    public async Task LoadAsync_AgentHasExpectedToolInToolIds(string agentId, string toolId)
    {
        var agents = await _source.LoadAsync();
        var agent = agents.Single(a => a.AgentId.Value == agentId);

        agent.ToolIds.ShouldContain(toolId,
            $"agent '{agentId}' must have tool '{toolId}'");
    }

    [Theory]
    [InlineData("researcher", "shell")]
    [InlineData("researcher", "exec")]
    [InlineData("researcher", "write")]
    [InlineData("researcher", "edit")]
    [InlineData("reviewer", "write")]
    [InlineData("reviewer", "edit")]
    public async Task LoadAsync_ReadOnlyAgentsDoNotHaveMutatingTools(string agentId, string mutatingTool)
    {
        var agents = await _source.LoadAsync();
        var agent = agents.Single(a => a.AgentId.Value == agentId);

        agent.ToolIds.ShouldNotContain(mutatingTool,
            $"read-only agent '{agentId}' must NOT have mutating tool '{mutatingTool}'");
    }

    [Fact]
    public async Task LoadAsync_AllAgentsHaveSystemPrompt()
    {
        var agents = await _source.LoadAsync();

        foreach (var agent in agents)
        {
            agent.SystemPrompt.ShouldNotBeNullOrWhiteSpace(
                $"built-in agent '{agent.AgentId}' must have a system prompt");
        }
    }

    [Fact]
    public async Task LoadAsync_AllAgentsHaveDisplayNameAndDescription()
    {
        var agents = await _source.LoadAsync();

        foreach (var agent in agents)
        {
            agent.DisplayName.ShouldNotBeNullOrWhiteSpace(
                $"built-in agent '{agent.AgentId}' must have a display name");
            agent.Description.ShouldNotBeNullOrWhiteSpace(
                $"built-in agent '{agent.AgentId}' must have a description");
        }
    }

    [Fact]
    public void Watch_ReturnsNull_BecauseBuiltInsAreStatic()
    {
        var watcher = _source.Watch(_ => { });

        watcher.ShouldBeNull("built-in agents never change at runtime");
    }

    [Fact]
    public void AgentIds_ContainsAllSixRoles()
    {
        BuiltInAgentConfigurationSource.AgentIds.ShouldBe(
            ["researcher", "coder", "planner", "reviewer", "writer", "analyst"],
            ignoreOrder: true);
    }
}
