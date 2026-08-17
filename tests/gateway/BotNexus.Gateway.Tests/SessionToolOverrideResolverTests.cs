using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the narrowing-only contract of the per-session tool overlay (issue #2523).
/// </summary>
/// <remarks>
/// The security property under test is that the session overlay can only ever REMOVE tools from
/// the agent's configured set. If it could add one, the overlay would become a privilege-escalation
/// seam: any actor able to set a conversation override could grant itself <c>exec</c> on an agent
/// deliberately configured without it. Every assertion here is made on the RESOLVED TOOL LIST, not
/// on a mock invocation, so these tests fail if the implementation merely logs a refusal while
/// still handing the tool through.
/// </remarks>
public sealed class SessionToolOverrideResolverTests
{
    private static readonly string[] AgentTools = ["read", "write", "edit", "shell"];

    private static bool IsRuntimePinned(string tool) =>
        DefaultToolPolicyProvider.RuntimePinnedTools.Contains(tool);

    [Fact]
    public void Resolve_WithNoOverride_ReturnsAgentSetUnchanged()
    {
        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides: null);

        Assert.Equal(AgentTools, result.Tools);
        Assert.Empty(result.RefusedTools);
        Assert.False(result.IsNarrowed);
    }

    [Fact]
    public void Resolve_WithEmptyOverride_ReturnsAgentSetUnchanged()
    {
        var result = SessionToolOverrideResolver.Resolve(AgentTools, new SessionToolOverride());

        Assert.Equal(AgentTools, result.Tools);
        Assert.False(result.IsNarrowed);
    }

    [Fact]
    public void Resolve_DisabledTools_AreRemovedFromResolvedList()
    {
        var overrides = new SessionToolOverride { DisabledTools = ["shell", "write"] };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        Assert.Equal(["read", "edit"], result.Tools);
        Assert.DoesNotContain("shell", result.Tools);
        Assert.True(result.IsNarrowed);
    }

    [Fact]
    public void Resolve_DisabledIsCaseInsensitive()
    {
        var overrides = new SessionToolOverride { DisabledTools = ["SHELL"] };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        Assert.DoesNotContain("shell", result.Tools);
    }

    [Fact]
    public void Resolve_EnabledTools_NarrowToThatSubsetOnly()
    {
        var overrides = new SessionToolOverride { EnabledTools = ["read"] };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        Assert.Equal(["read"], result.Tools);
        Assert.True(result.IsNarrowed);
    }

    // ---- The security property: widening is REFUSED, asserted on the resolved list ----

    [Fact]
    public void Resolve_EnablingToolTheAgentDoesNotHave_RefusesIt()
    {
        // "exec" is deliberately absent from the agent's configured set.
        var overrides = new SessionToolOverride { EnabledTools = ["read", "exec"] };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        // The resolved list is the authority: exec must NOT appear in it.
        Assert.DoesNotContain("exec", result.Tools);
        Assert.Equal(["read"], result.Tools);
        Assert.Equal(["exec"], result.RefusedTools);
    }

    [Fact]
    public void Resolve_EnablingOnlyUnavailableTools_YieldsEmptyListNotTheAgentSet()
    {
        // Regression guard: a naive "if the intersection is empty, fall back to everything"
        // implementation would turn a refused widening into a FULL grant - strictly worse than
        // the escalation it was meant to prevent.
        var overrides = new SessionToolOverride { EnabledTools = ["exec", "process"] };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        Assert.Empty(result.Tools);
        Assert.Equal(["exec", "process"], result.RefusedTools);
    }

    [Fact]
    public void Resolve_DisablingToolTheAgentDoesNotHave_IsNotARefusal()
    {
        // Disabling an absent tool is already satisfied - a no-op, not a widening attempt.
        var overrides = new SessionToolOverride { DisabledTools = ["exec"] };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        Assert.Equal(AgentTools, result.Tools);
        Assert.Empty(result.RefusedTools);
    }

    [Fact]
    public void Resolve_DisabledBeatsEnabled_ForTheSameTool()
    {
        // Ambiguity resolves toward the narrower outcome. A seam that exists to reduce blast radius
        // must never resolve a contradiction by granting.
        var overrides = new SessionToolOverride
        {
            EnabledTools = ["read", "write"],
            DisabledTools = ["write"]
        };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        Assert.Equal(["read"], result.Tools);
    }

    [Fact]
    public void Resolve_PinnedTools_SurviveDisable()
    {
        // Runtime-pinned tools are required for basic agent function; the existing deny-list path
        // exempts them (DefaultToolPolicyProvider.IsDenied) and this overlay must agree, or a
        // session override could brick the agent's ability to answer at all.
        string[] agentTools = ["read", "ask_user", "conversation"];
        var overrides = new SessionToolOverride { DisabledTools = ["ask_user", "read"] };

        var result = SessionToolOverrideResolver.Resolve(agentTools, overrides, IsRuntimePinned);

        Assert.Contains("ask_user", result.Tools);
        Assert.DoesNotContain("read", result.Tools);
    }

    [Fact]
    public void Resolve_PinnedTools_SurviveAnEnabledNarrowingThatOmitsThem()
    {
        string[] agentTools = ["read", "ask_user"];
        var overrides = new SessionToolOverride { EnabledTools = ["read"] };

        var result = SessionToolOverrideResolver.Resolve(agentTools, overrides, IsRuntimePinned);

        Assert.Contains("ask_user", result.Tools);
        Assert.Contains("read", result.Tools);
    }

    [Fact]
    public void Resolve_PinnedToolTheAgentDoesNotHave_IsStillNotGranted()
    {
        // Pinning exempts a tool from being DROPPED; it must not conjure a tool the agent never had.
        string[] agentTools = ["read"];
        var overrides = new SessionToolOverride { EnabledTools = ["ask_user"] };

        var result = SessionToolOverrideResolver.Resolve(agentTools, overrides, IsRuntimePinned);

        Assert.DoesNotContain("ask_user", result.Tools);
        Assert.Equal(["ask_user"], result.RefusedTools);
    }

    [Fact]
    public void Resolve_PreservesAgentSetOrdering()
    {
        var overrides = new SessionToolOverride { DisabledTools = ["edit"] };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        Assert.Equal(["read", "write", "shell"], result.Tools);
    }

    [Fact]
    public void Resolve_DeduplicatesRepeatedEntries()
    {
        var overrides = new SessionToolOverride { EnabledTools = ["read", "read", "READ"] };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        Assert.Equal(["read"], result.Tools);
    }

    [Fact]
    public void Resolve_IgnoresBlankEntries()
    {
        var overrides = new SessionToolOverride { EnabledTools = ["read", "  ", ""] };

        var result = SessionToolOverrideResolver.Resolve(AgentTools, overrides);

        Assert.Equal(["read"], result.Tools);
        Assert.Empty(result.RefusedTools);
    }

    // ---- Round-trip / persistence shape ----

    [Fact]
    public void ToJson_RoundTripsThroughFromJson()
    {
        var original = new SessionToolOverride
        {
            EnabledTools = ["read", "grep"],
            DisabledTools = ["exec"]
        };

        var restored = SessionToolOverride.FromJson(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal(["read", "grep"], restored!.EnabledTools);
        Assert.Equal(["exec"], restored.DisabledTools);
        Assert.True(restored.HasRestrictions);
    }

    [Fact]
    public void ToJson_RoundTrip_PreservesTheResolvedOutcome()
    {
        // The persisted form must produce the SAME narrowing after a reconnect - that is the whole
        // point of storing it on the conversation row rather than in session memory.
        var original = new SessionToolOverride { DisabledTools = ["shell"] };
        var before = SessionToolOverrideResolver.Resolve(AgentTools, original);

        var after = SessionToolOverrideResolver.Resolve(
            AgentTools,
            SessionToolOverride.FromJson(original.ToJson()));

        Assert.Equal(before.Tools, after.Tools);
        Assert.DoesNotContain("shell", after.Tools);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    public void FromJson_AbsentOrCorrupt_ReturnsNullRatherThanThrowing(string? json)
    {
        // Must not throw on the agent-construction path: an unreadable overlay degrades to "no
        // overlay" (the agent's own configured set), never to an exception that bricks the turn.
        var parsed = SessionToolOverride.FromJson(json);

        Assert.Null(parsed);
        Assert.Equal(AgentTools, SessionToolOverrideResolver.Resolve(AgentTools, parsed).Tools);
    }

    [Fact]
    public void HasRestrictions_IsFalseForEmptyLists()
    {
        var overrides = new SessionToolOverride { EnabledTools = [], DisabledTools = [] };

        Assert.False(overrides.HasRestrictions);
    }
}
