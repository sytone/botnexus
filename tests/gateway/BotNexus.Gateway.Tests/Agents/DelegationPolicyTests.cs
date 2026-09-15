using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// The operator's stated access policy, applied to BOTH ways an agent reaches another agent
/// (Plan 3.6).
///
/// <remarks>
/// <c>gateway:agentExchange:accessPolicy</c> already existed and was already stated. It was
/// enforced on <c>agent_converse</c> and not on <c>spawn_subagent</c> - and the spawn path is the
/// stronger of the two, because a Mirror spawn runs the TARGET's descriptor verbatim and the child
/// therefore holds the target's tools rather than the parent's.
/// </remarks>
/// </summary>
public sealed class DelegationPolicyTests
{
    private const string Uid = "abc123";

    // ── The rule itself ───────────────────────────────────────────────────

    [Fact]
    public void A_named_grant_authorises_the_target()
    {
        var initiator = Descriptor("nova", subAgents: ["scribe"]);

        DelegationPolicy.IsGranted(initiator, "scribe", Descriptor("scribe")).ShouldBeTrue();
    }

    [Fact]
    public void An_ungranted_target_is_not_authorised()
    {
        var initiator = Descriptor("nova", subAgents: ["scribe"]);

        DelegationPolicy.IsGranted(initiator, "auditor", Descriptor("auditor")).ShouldBeFalse();
    }

    [Fact]
    public void A_role_grant_authorises_any_agent_carrying_that_role()
    {
        // How an operator says "may delegate to any reviewer" without naming each one.
        var initiator = Descriptor("nova", subAgentRoles: ["reviewer"]);
        var target = Descriptor("auditor", role: "reviewer");

        DelegationPolicy.IsGranted(initiator, "auditor", target).ShouldBeTrue();
    }

    [Fact]
    public void A_role_grant_cannot_be_satisfied_by_an_agent_this_gateway_cannot_see()
    {
        // The role lives in the TARGET's metadata. A remote agent's descriptor is not resolvable
        // here, and a role asserted by a gateway we do not control is not a grant we made.
        var initiator = Descriptor("nova", subAgentRoles: ["reviewer"]);

        DelegationPolicy.IsGranted(initiator, "remote-auditor", target: null).ShouldBeFalse();
    }

    [Fact]
    public void Grants_are_matched_case_insensitively()
    {
        var initiator = Descriptor("nova", subAgents: ["Scribe"], subAgentRoles: ["Reviewer"]);

        DelegationPolicy.IsGranted(initiator, "scribe", Descriptor("scribe")).ShouldBeTrue();
        DelegationPolicy.IsGranted(initiator, "auditor", Descriptor("auditor", role: "REVIEWER")).ShouldBeTrue();
    }

    // ── The gap that was open on the spawn path ───────────────────────────

    [Fact]
    public void Under_whitelist_an_agent_cannot_mirror_an_agent_it_was_never_granted()
    {
        // THE test. Before this, spawn_subagent(targetAgentId: ...) consulted no allow-list at
        // all, so an operator who had written `whitelist` got it applied to conversation and
        // ignored on delegation - the path that hands the child the TARGET's toolset.
        var parent = Descriptor("nova", subAgents: ["scribe"]);
        var target = Descriptor("privileged", tools: ["exec"]);
        var manager = BuildManager(parent, target, whitelist: true);

        Should.Throw<UnauthorizedAccessException>(() =>
            manager.ValidateDelegationGrant(Request("nova", new Mirror(AgentId.From("privileged"))), parent));
    }

    [Fact]
    public void The_refusal_says_how_to_fix_it()
    {
        // A bare "not allowed" leaves an operator guessing which of three knobs to reach for.
        var parent = Descriptor("nova", subAgents: ["scribe"]);
        var manager = BuildManager(parent, Descriptor("privileged"), whitelist: true);

        var error = Should.Throw<UnauthorizedAccessException>(() =>
            manager.ValidateDelegationGrant(Request("nova", new Mirror(AgentId.From("privileged"))), parent));

        error.Message.ShouldContain("subAgents");
        error.Message.ShouldContain("subAgentRoles");
        error.Message.ShouldContain("accessPolicy");
    }

    [Fact]
    public void Under_whitelist_a_granted_target_is_still_allowed()
    {
        var parent = Descriptor("nova", subAgents: ["scribe"]);
        var manager = BuildManager(parent, Descriptor("scribe"), whitelist: true);

        Should.NotThrow(() =>
            manager.ValidateDelegationGrant(Request("nova", new Mirror(AgentId.From("scribe"))), parent));
    }

    [Fact]
    public void A_role_grant_is_honoured_on_the_spawn_path_too()
    {
        // Both paths share one implementation precisely so this cannot diverge.
        var parent = Descriptor("nova", subAgentRoles: ["reviewer"]);
        var manager = BuildManager(parent, Descriptor("auditor", role: "reviewer"), whitelist: true);

        Should.NotThrow(() =>
            manager.ValidateDelegationGrant(Request("nova", new Mirror(AgentId.From("auditor"))), parent));
    }

    // ── What must NOT change ──────────────────────────────────────────────

    [Fact]
    public void The_default_policy_changes_nothing()
    {
        // `open` is the default and the behaviour that shipped. An install that never stated a
        // policy must not start refusing spawns because this landed.
        var parent = Descriptor("nova");
        var manager = BuildManager(parent, Descriptor("anyone"), whitelist: false);

        Should.NotThrow(() =>
            manager.ValidateDelegationGrant(Request("nova", new Mirror(AgentId.From("anyone"))), parent));
    }

    [Fact]
    public void An_unconfigured_gateway_changes_nothing()
    {
        // The options are optional on the constructor, so every existing construction site keeps
        // working. Absent must mean `open`, not "deny everything".
        var parent = Descriptor("nova");
        var manager = BuildManager(parent, Descriptor("anyone"), whitelist: null);

        Should.NotThrow(() =>
            manager.ValidateDelegationGrant(Request("nova", new Mirror(AgentId.From("anyone"))), parent));
    }

    [Fact]
    public void An_embody_spawn_is_never_subject_to_the_grant_check()
    {
        // Embody clones the PARENT's own descriptor. It reaches no other agent, so there is no
        // grant to check - and checking one would break ordinary delegation under whitelist for
        // no security gain whatsoever.
        var parent = Descriptor("nova", subAgents: []);
        var manager = BuildManager(parent, target: null, whitelist: true);

        Should.NotThrow(() =>
            manager.ValidateDelegationGrant(Request("nova", new Embody(SubAgentArchetype.Coder)), parent));
    }

    [Fact]
    public void Mirroring_yourself_is_not_silently_exempt_from_the_policy()
    {
        // Self is NOT special-cased, and that is a decision rather than an oversight. Mirroring
        // yourself gains no authority, so an exemption would be harmless - but it would also be
        // an unstated rule, and the point of this work is that the policy reads the way it is
        // written. An operator who wants it grants it.
        var parent = Descriptor("nova", subAgents: []);
        var manager = BuildManager(parent, parent, whitelist: true);

        Should.Throw<UnauthorizedAccessException>(() =>
            manager.ValidateDelegationGrant(Request("nova", new Mirror(AgentId.From("nova"))), parent));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static AgentDescriptor Descriptor(
        string id,
        IReadOnlyList<string>? subAgents = null,
        IReadOnlyList<string>? subAgentRoles = null,
        IReadOnlyList<string>? tools = null,
        string? role = null)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (role is not null)
            metadata["role"] = role;

        return new AgentDescriptor
        {
            AgentId = AgentId.From(id),
            DisplayName = id,
            ModelId = "test-model",
            ApiProvider = "test",
            SubAgentIds = subAgents ?? [],
            SubAgentRoles = subAgentRoles ?? [],
            ToolIds = tools ?? [],
            Metadata = metadata
        };
    }

    private static SubAgentSpawnRequest Request(string parent, SubAgentSpawnMode mode) => new()
    {
        ParentAgentId = AgentId.From(parent),
        ParentSessionId = SessionId.From("s-1"),
        Task = "do the thing",
        InheritedConversationId = ConversationId.From("c-1"),
        Mode = mode
    };

    private static DefaultSubAgentManager BuildManager(
        AgentDescriptor parent,
        AgentDescriptor? target,
        bool? whitelist)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(parent.AgentId)).Returns(parent);
        if (target is not null)
            registry.Setup(r => r.Get(target.AgentId)).Returns(target);

        IOptions<AgentExchangeOptions>? exchange = whitelist is null
            ? null
            : Options.Create(new AgentExchangeOptions
            {
                AccessPolicy = whitelist.Value ? "whitelist" : "open"
            });

        return new DefaultSubAgentManager(
            new Mock<IAgentSupervisor>().Object,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance,
            exchangeOptions: exchange);
    }
}
