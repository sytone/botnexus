using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// <see cref="AgentDescriptor.CanDelegate"/> restates the tool-level admission rule that
/// <c>ToolProviderContext.ToolAllowed</c> applies to <c>spawn_subagent</c>. These tests pin both
/// branches of that rule, because the property exists to drive a roster badge and a badge that
/// disagrees with the tools an agent is actually handed is worse than no badge at all.
/// </summary>
public sealed class AgentDescriptorCanDelegateTests
{
    [Fact]
    public void Unrestricted_agent_can_delegate()
    {
        // An empty toolIds list is the "everything the registry offers" case, which is how every
        // agent that omits toolIds from its config is resolved.
        CreateDescriptor(toolIds: []).CanDelegate.ShouldBeTrue();
    }

    [Fact]
    public void Wildcard_agent_can_delegate()
    {
        // ["*"] is the user-friendly alias for [], normalised upstream in InProcessIsolationStrategy.
        CreateDescriptor(toolIds: ["*"]).CanDelegate.ShouldBeTrue();
    }

    [Fact]
    public void Restricted_agent_granted_the_tool_can_delegate()
    {
        CreateDescriptor(toolIds: ["read", "bash", "spawn_subagent"]).CanDelegate.ShouldBeTrue();
    }

    [Fact]
    public void Tool_name_match_is_case_insensitive()
    {
        CreateDescriptor(toolIds: ["Spawn_SubAgent"]).CanDelegate.ShouldBeTrue();
    }

    [Fact]
    public void Restricted_agent_granted_only_converse_can_delegate()
    {
        // agent_converse is the other way to hand work to another agent - it calls an agent that
        // already exists rather than spawning an ephemeral child. An operator reading the roster
        // does not care which mechanism is configured, so both must mark the card.
        CreateDescriptor(toolIds: ["read", "bash", "agent_converse"]).CanDelegate.ShouldBeTrue();
    }

    [Fact]
    public void Restricted_agent_without_either_tool_cannot_delegate()
    {
        // The shape of every purpose-built agent on a typical install: an explicit toolIds list
        // naming neither delegation tool.
        CreateDescriptor(toolIds: ["read", "write", "bash", "exec", "canvas", "grep", "curl"])
            .CanDelegate.ShouldBeFalse();
    }

    [Fact]
    public void A_wildcard_among_other_tools_is_not_the_wildcard_alias()
    {
        // Only a lone "*" means "all tools". Mixed with siblings it is just an unmatched id, so the
        // list stays restrictive and the spawn tool is not admitted.
        CreateDescriptor(toolIds: ["*", "read"]).CanDelegate.ShouldBeFalse();
    }

    private static AgentDescriptor CreateDescriptor(IReadOnlyList<string> toolIds) => new()
    {
        AgentId = AgentId.From("test-agent"),
        DisplayName = "Test Agent",
        ApiProvider = "anthropic",
        ModelId = "claude-haiku-4-5-20251001",
        ToolIds = toolIds
    };
}
