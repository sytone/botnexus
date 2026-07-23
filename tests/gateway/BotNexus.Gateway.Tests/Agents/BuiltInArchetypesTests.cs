using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Agents;
using Xunit;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Regression tests for the built-in sub-agent archetype catalog (#2136). The six worker
/// archetypes are no longer registered as named conversational agents; they exist only as
/// spawn-time profiles resolved by <see cref="DefaultSubAgentManager.ResolveSpawnPlan"/>.
/// </summary>
public sealed class BuiltInArchetypesTests
{
    [Theory]
    [InlineData("researcher")]
    [InlineData("coder")]
    [InlineData("planner")]
    [InlineData("reviewer")]
    [InlineData("writer")]
    [InlineData("analyst")]
    public void ReservedAgentIds_contains_all_six_worker_roles(string id)
    {
        Assert.Contains(id, BuiltInArchetypes.ReservedAgentIds);
        Assert.True(BuiltInArchetypes.IsReserved(id));
    }

    [Fact]
    public void ReservedAgentIds_has_exactly_six_entries()
    {
        Assert.Equal(6, BuiltInArchetypes.ReservedAgentIds.Count);
    }

    [Theory]
    [InlineData("CODER")]
    [InlineData("Researcher")]
    [InlineData("  writer  ")]
    public void IsReserved_is_case_and_whitespace_insensitive(string id)
    {
        Assert.True(BuiltInArchetypes.IsReserved(id));
    }

    [Theory]
    [InlineData("nova")]
    [InlineData("farnsworth")]
    [InlineData("general")]
    [InlineData("")]
    [InlineData(null)]
    public void IsReserved_returns_false_for_non_archetype_ids(string? id)
    {
        Assert.False(BuiltInArchetypes.IsReserved(id));
    }

    [Fact]
    public void GetProfile_returns_tool_restriction_for_each_archetype()
    {
        Assert.Contains("shell", BuiltInArchetypes.GetProfile(SubAgentArchetype.Coder)!.ToolIds);
        Assert.DoesNotContain("shell", BuiltInArchetypes.GetProfile(SubAgentArchetype.Researcher)!.ToolIds);
        Assert.DoesNotContain("write", BuiltInArchetypes.GetProfile(SubAgentArchetype.Reviewer)!.ToolIds);
        Assert.Contains("web_search", BuiltInArchetypes.GetProfile(SubAgentArchetype.Planner)!.ToolIds);
    }

    [Fact]
    public void GetProfile_returns_null_for_general_archetype()
    {
        // 'general' has no built-in tool restriction - the sub-agent inherits the parent's tools.
        Assert.Null(BuiltInArchetypes.GetProfile(SubAgentArchetype.General));
    }
}
