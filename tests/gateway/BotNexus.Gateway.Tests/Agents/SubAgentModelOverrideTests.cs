using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// #2647: <c>spawn_subagent</c>'s <c>model</c>/<c>apiProvider</c> overrides must reach the
/// <b>registered child descriptor</b> - the state the child actually dispatches against - and not
/// merely the <see cref="SubAgentInfo"/> reporting record. Every assertion here reads
/// <c>AgentDescriptor.ModelId</c> / <c>AgentDescriptor.ApiProvider</c> off the registry, never
/// <c>SubAgentInfo.Model</c>, because asserting on the reporting record is precisely what made the
/// original defect invisible: the record was populated from a resolution nothing wrote through.
/// </summary>
public sealed class SubAgentModelOverrideTests
{
    // ---------------- clause 1: model override reaches the descriptor ----------------

    [Fact]
    public async Task SpawnAsync_ModelOverride_IsAppliedToChildDescriptor()
    {
        var (registry, manager) = Build();

        var spawned = await manager.SpawnAsync(Request(new Embody(
            SubAgentArchetype.General,
            new EmbodyCustomizations { ModelOverride = "child-model" })));

        Child(registry, spawned).ModelId.ShouldBe("child-model",
            "#2647 clause 1: the requested model must be written onto the descriptor the child " +
            "runs on. Inheriting the parent's model here is the silent-substitution defect.");
    }

    // ---------------- clause 2: provider override reaches the descriptor ----------------

    [Fact]
    public async Task SpawnAsync_ApiProviderOverride_IsAppliedToChildDescriptor()
    {
        var (registry, manager) = Build();

        var spawned = await manager.SpawnAsync(Request(new Embody(
            SubAgentArchetype.General,
            new EmbodyCustomizations { ModelOverride = "other-model", ApiProviderOverride = "other-provider" })));

        var child = Child(registry, spawned);
        child.ApiProvider.ShouldBe("other-provider",
            "#2647 clause 2: plan.ApiProviderOverride must have a production consumer in SpawnAsync.");
        child.ModelId.ShouldBe("other-model");
    }

    // ---------------- clause 3: descriptor and info record share ONE resolution ----------------

    [Fact]
    public async Task SpawnAsync_Override_DescriptorAndInfoRecordAgree()
    {
        var (registry, manager) = Build();

        var spawned = await manager.SpawnAsync(Request(new Embody(
            SubAgentArchetype.General,
            new EmbodyCustomizations { ModelOverride = "child-model" })));

        Child(registry, spawned).ModelId.ShouldBe(spawned.Model,
            "#2647 clause 3: what runs and what is reported derive from a single resolution.");
    }

    [Fact]
    public async Task SpawnAsync_ConfiguredSubAgentDefault_DescriptorAndInfoRecordAgree()
    {
        var (registry, manager) = Build(options: new GatewayOptions
        {
            SubAgents = new SubAgentOptions { DefaultModel = "configured-default" }
        });

        var spawned = await manager.SpawnAsync(Request(new Embody(SubAgentArchetype.General)));

        var child = Child(registry, spawned);
        child.ModelId.ShouldBe("configured-default",
            "#2647 clause 3: the configured sub-agent default is a real layer of the resolution " +
            "and must land on the descriptor, not only on the reporting record.");
        child.ModelId.ShouldBe(spawned.Model);
    }

    [Fact]
    public async Task SpawnAsync_PureInheritance_DescriptorAndInfoRecordAgree()
    {
        var (registry, manager) = Build();

        var spawned = await manager.SpawnAsync(Request(new Embody(SubAgentArchetype.General)));

        var child = Child(registry, spawned);
        child.ModelId.ShouldBe("parent-model");
        child.ApiProvider.ShouldBe("parent-provider");
        child.ModelId.ShouldBe(spawned.Model);
    }

    // ---------------- clause 4: unresolvable pair fails at spawn time ----------------

    [Fact]
    public async Task SpawnAsync_UnknownModel_ThrowsNamingRequestedValue_AndCreatesNothing()
    {
        var (registry, manager) = Build(modelRegistry: PopulatedModelRegistry());
        var before = registry.GetAll().Select(d => d.AgentId.Value).ToList();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => manager.SpawnAsync(Request(new Embody(
            SubAgentArchetype.General,
            new EmbodyCustomizations { ModelOverride = "gpt-5.6-sol" }))));

        ex.Message.ShouldContain("gpt-5.6-sol");
        registry.GetAll().Select(d => d.AgentId.Value).ShouldBe(before, ignoreOrder: true,
            "#2647 clause 4: no child descriptor may be registered for a spawn that cannot resolve.");
        (await manager.ListAsync(SessionId.From("parent-session"))).ShouldBeEmpty(
            "#2647 clause 4: no sub-agent record may be created for a spawn that cannot resolve.");
    }

    [Fact]
    public async Task SpawnAsync_UnknownProvider_ThrowsNamingRequestedValue()
    {
        var (_, manager) = Build(modelRegistry: PopulatedModelRegistry());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => manager.SpawnAsync(Request(new Embody(
            SubAgentArchetype.General,
            new EmbodyCustomizations { ModelOverride = "known-model", ApiProviderOverride = "no-such-provider" }))));

        ex.Message.ShouldContain("no-such-provider");
    }

    [Fact]
    public async Task SpawnAsync_KnownModel_PassesPreflight_AndReachesDescriptor()
    {
        var (registry, manager) = Build(modelRegistry: PopulatedModelRegistry());

        var spawned = await manager.SpawnAsync(Request(new Embody(
            SubAgentArchetype.General,
            new EmbodyCustomizations { ModelOverride = "known-model", ApiProviderOverride = "known-provider" })));

        var child = Child(registry, spawned);
        child.ModelId.ShouldBe("known-model");
        child.ApiProvider.ShouldBe("known-provider");
    }

    // ---------------- clause 5: Mirror still inherits the target ----------------

    [Fact]
    public async Task SpawnAsync_Mirror_InheritsTargetDescriptorModel_EvenWithConfiguredDefault()
    {
        // A configured sub-agent DefaultModel is an Embody-layer concern. If the new descriptor
        // assignment leaked into the Mirror branch, the child would silently run on
        // "configured-default" instead of the mirrored target's model (#562 / #1565 regression).
        var (registry, manager) = Build(options: new GatewayOptions
        {
            SubAgents = new SubAgentOptions { DefaultModel = "configured-default" }
        });
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("target-agent"),
            DisplayName = "Target",
            ModelId = "target-model",
            ApiProvider = "target-provider"
        });

        var spawned = await manager.SpawnAsync(Request(new Mirror(AgentId.From("target-agent"))));

        var child = Child(registry, spawned);
        child.ModelId.ShouldBe("target-model",
            "#2647 clause 5: Mirror is strict pass-through of the target descriptor - no override, " +
            "not even the configured sub-agent default, may be applied.");
        child.ApiProvider.ShouldBe("target-provider");
    }

    // ---------------- helpers ----------------

    private static ModelRegistry PopulatedModelRegistry()
    {
        var registry = new ModelRegistry();
        registry.Register("known-provider", new LlmModel(
            Id: "known-model",
            Name: "known-model",
            Api: "test-api",
            Provider: "known-provider",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024));
        return registry;
    }

    private static AgentDescriptor Child(IAgentRegistry registry, SubAgentInfo info)
    {
        var match = registry.GetAll()
            .FirstOrDefault(d => d.AgentId.Value.Equals(info.ChildAgentId, StringComparison.OrdinalIgnoreCase));
        match.ShouldNotBeNull($"Child descriptor '{info.ChildAgentId}' must be registered.");
        return match!;
    }

    private static SubAgentSpawnRequest Request(SubAgentSpawnMode mode)
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            Mode = mode,
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

    private static (DefaultAgentRegistry Registry, DefaultSubAgentManager Manager) Build(
        GatewayOptions? options = null,
        ModelRegistry? modelRegistry = null)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("parent-agent"),
            DisplayName = "Parent Agent",
            ModelId = "parent-model",
            ApiProvider = "parent-provider"
        });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HangingHandle());

        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry,
            new Mock<IActivityBroadcaster>().Object,
            new Mock<IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(options ?? new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance,
            modelRegistry: modelRegistry);

        return (registry, manager);
    }

    private static IAgentHandle HangingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new AgentResponse { Content = "never" };
            });
        return handle.Object;
    }
}
