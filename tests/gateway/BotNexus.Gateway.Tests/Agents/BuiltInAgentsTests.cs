using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class BuiltInAgentsTests
{
    [Fact]
    public void All_contains_six_agents()
    {
        Assert.Equal(6, BuiltInAgents.All.Count);
    }

    [Theory]
    [InlineData("researcher")]
    [InlineData("coder")]
    [InlineData("planner")]
    [InlineData("reviewer")]
    [InlineData("writer")]
    [InlineData("analyst")]
    public void All_contains_expected_agent_id(string expectedId)
    {
        Assert.Contains(BuiltInAgents.All, a => a.AgentId.Value == expectedId);
    }

    [Fact]
    public void All_agent_ids_are_unique()
    {
        var ids = BuiltInAgents.All.Select(a => a.AgentId.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_agents_have_display_name()
    {
        foreach (var agent in BuiltInAgents.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(agent.DisplayName),
                $"Agent '{agent.AgentId}' has no display name.");
        }
    }

    [Fact]
    public void All_agents_have_emoji()
    {
        foreach (var agent in BuiltInAgents.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(agent.Emoji),
                $"Agent '{agent.AgentId}' has no emoji.");
        }
    }

    [Fact]
    public void All_agents_have_description()
    {
        foreach (var agent in BuiltInAgents.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(agent.Description),
                $"Agent '{agent.AgentId}' has no description.");
        }
    }

    [Fact]
    public void All_agents_have_system_prompt()
    {
        foreach (var agent in BuiltInAgents.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(agent.SystemPrompt),
                $"Agent '{agent.AgentId}' has no system prompt.");
        }
    }

    [Fact]
    public void All_agents_have_non_empty_tool_ids()
    {
        foreach (var agent in BuiltInAgents.All)
        {
            Assert.True(agent.ToolIds.Count > 0,
                $"Agent '{agent.AgentId}' has no tool IDs.");
        }
    }

    [Fact]
    public void All_agents_have_role_metadata()
    {
        foreach (var agent in BuiltInAgents.All)
        {
            Assert.True(agent.Metadata.ContainsKey("role"),
                $"Agent '{agent.AgentId}' is missing 'role' metadata.");
            Assert.NotNull(agent.Metadata["role"]);
        }
    }

    [Fact]
    public void All_agents_have_builtin_metadata_flag()
    {
        foreach (var agent in BuiltInAgents.All)
        {
            Assert.True(agent.Metadata.ContainsKey("builtin"),
                $"Agent '{agent.AgentId}' is missing 'builtin' metadata flag.");
            Assert.Equal(true, agent.Metadata["builtin"]);
        }
    }

    [Fact]
    public void Researcher_cannot_execute_code()
    {
        var tools = BuiltInAgents.Researcher.ToolIds;
        Assert.DoesNotContain("shell", tools);
        Assert.DoesNotContain("exec", tools);
        Assert.DoesNotContain("write", tools);
        Assert.DoesNotContain("edit", tools);
    }

    [Fact]
    public void Researcher_has_read_and_search_tools()
    {
        var tools = BuiltInAgents.Researcher.ToolIds;
        Assert.Contains("web_search", tools);
        Assert.Contains("web_fetch", tools);
        Assert.Contains("read", tools);
        Assert.Contains("glob", tools);
        Assert.Contains("grep", tools);
    }

    [Fact]
    public void Coder_has_full_file_and_shell_access()
    {
        var tools = BuiltInAgents.Coder.ToolIds;
        Assert.Contains("read", tools);
        Assert.Contains("write", tools);
        Assert.Contains("edit", tools);
        Assert.Contains("shell", tools);
        Assert.Contains("exec", tools);
        Assert.Contains("process", tools);
    }

    [Fact]
    public void Coder_cannot_search_web()
    {
        var tools = BuiltInAgents.Coder.ToolIds;
        Assert.DoesNotContain("web_search", tools);
    }

    [Fact]
    public void Reviewer_is_read_only_with_shell()
    {
        var tools = BuiltInAgents.Reviewer.ToolIds;
        Assert.Contains("read", tools);
        Assert.Contains("glob", tools);
        Assert.Contains("grep", tools);
        Assert.Contains("shell", tools);
        Assert.DoesNotContain("write", tools);
        Assert.DoesNotContain("edit", tools);
        Assert.DoesNotContain("exec", tools);
    }

    [Fact]
    public void Planner_has_memory_and_web_tools()
    {
        var tools = BuiltInAgents.Planner.ToolIds;
        Assert.Contains("memory_search", tools);
        Assert.Contains("memory_save", tools);
        Assert.Contains("memory_get", tools);
        Assert.Contains("web_search", tools);
        Assert.Contains("read", tools);
        Assert.Contains("write", tools);
    }

    [Fact]
    public void Planner_cannot_execute_shell()
    {
        var tools = BuiltInAgents.Planner.ToolIds;
        Assert.DoesNotContain("shell", tools);
        Assert.DoesNotContain("exec", tools);
    }

    [Fact]
    public void Writer_has_file_write_and_search()
    {
        var tools = BuiltInAgents.Writer.ToolIds;
        Assert.Contains("read", tools);
        Assert.Contains("write", tools);
        Assert.Contains("edit", tools);
        Assert.Contains("web_search", tools);
        Assert.Contains("web_fetch", tools);
    }

    [Fact]
    public void Writer_cannot_execute_shell()
    {
        var tools = BuiltInAgents.Writer.ToolIds;
        Assert.DoesNotContain("shell", tools);
        Assert.DoesNotContain("exec", tools);
    }

    [Fact]
    public void Analyst_has_read_and_shell_access()
    {
        var tools = BuiltInAgents.Analyst.ToolIds;
        Assert.Contains("read", tools);
        Assert.Contains("glob", tools);
        Assert.Contains("grep", tools);
        Assert.Contains("shell", tools);
        Assert.Contains("exec", tools);
        Assert.Contains("web_fetch", tools);
    }

    [Fact]
    public void Analyst_cannot_write_files()
    {
        var tools = BuiltInAgents.Analyst.ToolIds;
        Assert.DoesNotContain("write", tools);
        Assert.DoesNotContain("edit", tools);
    }

    [Fact]
    public void All_agents_have_empty_model_and_provider()
    {
        // Built-in agents inherit model/provider from the spawning context
        foreach (var agent in BuiltInAgents.All)
        {
            Assert.Equal("", agent.ModelId);
            Assert.Equal("", agent.ApiProvider);
        }
    }

    [Fact]
    public void All_agents_are_named_kind()
    {
        foreach (var agent in BuiltInAgents.All)
        {
            Assert.Equal(AgentKind.Named, agent.Kind);
        }
    }
}
