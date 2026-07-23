using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Unit tests for the pure helpers extracted from <see cref="DefaultSubAgentManager.SpawnAsync"/>
/// (#1565) — <see cref="DefaultSubAgentManager.ResolveSpawnPlan"/> (the Embody/Mirror resolution)
/// and <see cref="DefaultSubAgentManager.EnforceSpawnLimits"/> (depth/concurrency guards). These
/// exercise the resolution/guard logic directly, without driving the full 200+ line spawn path.
/// </summary>
public sealed class DefaultSubAgentManagerPlanTests
{
    private const string Uid = "abc123";

    // ---------------- ResolveSpawnPlan: Embody ----------------

    [Fact]
    public void ResolveSpawnPlan_Embody_ClonesParentDescriptor_AndChildIdUsesArchetypeSlot()
    {
        var parent = Descriptor("nova", "gpt-4o", "openai", "You are Nova.");
        var manager = BuildManager(parent);
        var request = Request("nova", new Embody(SubAgentArchetype.Coder));

        var plan = manager.ResolveSpawnPlan(request, parent, Uid);

        plan.Archetype.ShouldBe(SubAgentArchetype.Coder);
        plan.BaseDescriptor.AgentId.ShouldBe(AgentId.From("nova"));
        plan.BaseDescriptor.ModelId.ShouldBe("gpt-4o");
        plan.ChildAgentId.Value.ShouldBe($"nova--subagent--coder--{Uid}");
    }

    [Fact]
    public void ResolveSpawnPlan_Embody_PropagatesCustomisationOverrides()
    {
        var parent = Descriptor("nova", "gpt-4o", "openai");
        var manager = BuildManager(parent);
        var request = Request("nova", new Embody(
            SubAgentArchetype.Reviewer,
            new EmbodyCustomizations
            {
                Name = "code-checker",
                ModelOverride = "gpt-5-mini",
                ApiProviderOverride = "openai",
                ToolIds = ["read", "grep"],
                SystemPromptOverride = "Review only."
            }));

        var plan = manager.ResolveSpawnPlan(request, parent, Uid);

        plan.Name.ShouldBe("code-checker");
        plan.ModelOverride.ShouldBe("gpt-5-mini");
        plan.ApiProviderOverride.ShouldBe("openai");
        plan.ToolIds.ShouldBe(["read", "grep"]);
        plan.SystemPromptOverride.ShouldBe("Review only.");
    }

    [Fact]
    public void ResolveSpawnPlan_Embody_AppliesArchetypeToolRestriction_WhenNoExplicitTools()
    {
        // #2136: archetypes are resolved from the built-in catalog, not a registered named agent.
        // With no explicit tool override, the archetype's tool restriction is applied to the plan
        // while model/provider still come from the parent descriptor.
        var parent = Descriptor("nova", "gpt-4o", "openai", "You are Nova.");
        var manager = BuildManager(parent);
        var request = Request("nova", new Embody(SubAgentArchetype.Coder));

        var plan = manager.ResolveSpawnPlan(request, parent, Uid);

        plan.BaseDescriptor.ModelId.ShouldBe("gpt-4o");
        plan.ToolIds.ShouldNotBeNull();
        plan.ToolIds!.ShouldContain("shell");
        plan.ToolIds.ShouldContain("edit");
    }

    [Fact]
    public void ResolveSpawnPlan_Embody_ExplicitTools_OverrideArchetypeRestriction()
    {
        var parent = Descriptor("nova", "gpt-4o", "openai");
        var manager = BuildManager(parent);
        var request = Request("nova", new Embody(
            SubAgentArchetype.Coder,
            new EmbodyCustomizations { ToolIds = ["read"] }));

        var plan = manager.ResolveSpawnPlan(request, parent, Uid);

        plan.ToolIds.ShouldBe(["read"]);
    }

    [Fact]
    public void ResolveSpawnPlan_Embody_GeneralArchetype_InheritsParentTools()
    {
        // 'general' has no built-in restriction, so ToolIds stays null (inherit parent's tools).
        var parent = Descriptor("nova", "gpt-4o", "openai");
        var manager = BuildManager(parent);
        var request = Request("nova", new Embody(SubAgentArchetype.General));

        var plan = manager.ResolveSpawnPlan(request, parent, Uid);

        plan.ToolIds.ShouldBeNull();
    }

    // ---------------- ResolveSpawnPlan: Mirror ----------------

    [Fact]
    public void ResolveSpawnPlan_Mirror_UsesTargetDescriptor_AndChildIdUsesTargetSlot()
    {
        var parent = Descriptor("nova", "gpt-4o", "openai", "You are Nova.");
        var target = Descriptor("farnsworth", "claude-sonnet-4", "anthropic", "You are Farnsworth.");
        var manager = BuildManager(parent, target);
        var request = Request("nova", new Mirror(AgentId.From("farnsworth")));

        var plan = manager.ResolveSpawnPlan(request, parent, Uid);

        // Mirror is strict pass-through of the TARGET descriptor — a regression to the parent's
        // descriptor would silently corrupt downstream model/cost attribution (#562).
        plan.Archetype.ShouldBe(SubAgentArchetype.General);
        plan.BaseDescriptor.AgentId.ShouldBe(AgentId.From("farnsworth"));
        plan.BaseDescriptor.ModelId.ShouldBe("claude-sonnet-4");
        plan.ChildAgentId.Value.ShouldBe($"nova--subagent--farnsworth--{Uid}");
        // Mirror never carries Embody customisation overrides.
        plan.Name.ShouldBeNull();
        plan.ModelOverride.ShouldBeNull();
        plan.ToolIds.ShouldBeNull();
    }

    [Fact]
    public void ResolveSpawnPlan_Mirror_UnknownTarget_ThrowsKeyNotFound()
    {
        var parent = Descriptor("nova", "gpt-4o", "openai");
        var manager = BuildManager(parent);
        var request = Request("nova", new Mirror(AgentId.From("ghost-agent")));

        var ex = Should.Throw<KeyNotFoundException>(() => manager.ResolveSpawnPlan(request, parent, Uid));
        ex.Message.ShouldContain("ghost-agent");
    }

    // ---------------- EnforceSpawnLimits ----------------

    [Fact]
    public void EnforceSpawnLimits_UnderLimits_DoesNotThrow()
    {
        var parent = Descriptor("nova", "gpt-4o", "openai");
        var manager = BuildManager(parent, options: new GatewayOptions
        {
            SubAgents = new SubAgentOptions { MaxDepth = 3, MaxConcurrentPerSession = 5 }
        });
        var request = Request("nova", new Embody(SubAgentArchetype.General));

        // Fresh manager: depth 0, 0 running — well under the ceilings.
        Should.NotThrow(() => manager.EnforceSpawnLimits(request));
    }

    [Fact]
    public void EnforceSpawnLimits_DepthAtCeiling_Throws()
    {
        var parent = Descriptor("nova", "gpt-4o", "openai");
        // A nested child session at depth == MaxDepth must be rejected. SessionId.ForSubAgent encodes
        // depth via the parent chain; MaxDepth=1 means any sub-agent session is already at the limit.
        var manager = BuildManager(parent, options: new GatewayOptions
        {
            SubAgents = new SubAgentOptions { MaxDepth = 1, MaxConcurrentPerSession = 0 }
        });
        var nestedParentSession = SessionId.ForSubAgent(SessionId.From("nova-root"), "child1");
        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = nestedParentSession,
            Task = "T",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("conv"),
            Mode = new Embody(SubAgentArchetype.General)
        };

        var ex = Should.Throw<InvalidOperationException>(() => manager.EnforceSpawnLimits(request));
        ex.Message.ShouldContain("maximum depth");
    }

    [Fact]
    public void EnforceSpawnLimits_ZeroLimits_Disabled_DoesNotThrow()
    {
        var parent = Descriptor("nova", "gpt-4o", "openai");
        var manager = BuildManager(parent, options: new GatewayOptions
        {
            SubAgents = new SubAgentOptions { MaxDepth = 0, MaxConcurrentPerSession = 0 }
        });
        var nestedParentSession = SessionId.ForSubAgent(SessionId.From("nova-root"), "child1");
        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = nestedParentSession,
            Task = "T",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("conv"),
            Mode = new Embody(SubAgentArchetype.General)
        };

        // 0 means "unbounded" — limits disabled, so even a nested session is allowed.
        Should.NotThrow(() => manager.EnforceSpawnLimits(request));
    }

    // ---------------- helpers ----------------

    private static AgentDescriptor Descriptor(string id, string model, string provider, string? prompt = null)
        => new()
        {
            AgentId = AgentId.From(id),
            DisplayName = id,
            ModelId = model,
            ApiProvider = provider,
            SystemPrompt = prompt ?? string.Empty
        };

    private static SubAgentSpawnRequest Request(string parentId, SubAgentSpawnMode mode)
        => new()
        {
            ParentAgentId = AgentId.From(parentId),
            ParentSessionId = SessionId.From($"{parentId}-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("inherited-conv"),
            Mode = mode
        };

    private static DefaultSubAgentManager BuildManager(
        AgentDescriptor parent,
        AgentDescriptor? target = null,
        GatewayOptions? options = null)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(parent.AgentId)).Returns(parent);
        if (target is not null)
            registry.Setup(r => r.Get(target.AgentId)).Returns(target);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        return new DefaultSubAgentManager(
            new Mock<IAgentSupervisor>().Object,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(options ?? new GatewayOptions()),
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultSubAgentManager>>().Object);
    }
}
