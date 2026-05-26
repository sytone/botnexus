using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Tests for <see cref="DefaultSubAgentManager"/> Mode-driven spawn path
/// introduced in Phase 5 / F-6 step 3 (#562). These pin the new
/// SubAgentSpawnMode = Embody | Mirror discriminated union behaviour
/// independently from the legacy top-level field path, which is covered
/// by <see cref="SubAgentTargetAgentIdTests"/>.
/// </summary>
public sealed class SubAgentSpawnModeRoutingTests
{
    [Fact]
    public async Task SpawnAsync_WithModeEmbody_ClonesParentDescriptor_AndChildIdUsesArchetypeSlot()
    {
        var (manager, registry, registeredDescriptor, registeredAgentId) = BuildHarness(
            parentDescriptor: new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "gpt-4o",
                ApiProvider = "openai",
                SystemPrompt = "You are Nova."
            });

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = SessionId.From("nova-session"),
            Task = "Write some code",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("inherited-conv"),
            Mode = new Embody(SubAgentArchetype.Coder)
        };

        var result = await manager.SpawnAsync(request);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
        result.Archetype.ShouldBe(SubAgentArchetype.Coder);
        registeredDescriptor.Value.ShouldNotBeNull();
        registeredDescriptor.Value!.ModelId.ShouldBe("gpt-4o");
        registeredDescriptor.Value.SystemPrompt.ShouldBe("You are Nova.");
        registeredDescriptor.Value.Kind.ShouldBe(AgentKind.SubAgent);
        registeredDescriptor.Value.DisplayName.ShouldContain("(coder)");
        registeredAgentId.Value.ShouldNotBeNull();
        registeredAgentId.Value!.Value.Value.ShouldStartWith("nova--subagent--coder--");
    }

    [Fact]
    public async Task SpawnAsync_WithModeEmbody_CustomisationsPropagateTo_SubAgentInfo()
    {
        var (manager, _, _, _) = BuildHarness(
            parentDescriptor: new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "gpt-4o",
                ApiProvider = "openai"
            });

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = SessionId.From("nova-session"),
            Task = "T",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("inherited-conv"),
            Mode = new Embody(
                SubAgentArchetype.Reviewer,
                new EmbodyCustomizations
                {
                    Name = "code-checker",
                    ModelOverride = "gpt-5-mini"
                })
        };

        var info = await manager.SpawnAsync(request);

        info.Name.ShouldBe("code-checker");
        info.Model.ShouldBe("gpt-5-mini");
        info.Archetype.ShouldBe(SubAgentArchetype.Reviewer);
    }

    [Fact]
    public async Task SpawnAsync_WithModeMirror_UsesTargetDescriptor_AndChildIdUsesTargetSlot()
    {
        var (manager, registry, registeredDescriptor, registeredAgentId) = BuildHarness(
            parentDescriptor: new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "gpt-4o",
                ApiProvider = "openai",
                SystemPrompt = "You are Nova."
            },
            extra: new AgentDescriptor
            {
                AgentId = AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "claude-sonnet-4",
                ApiProvider = "anthropic",
                SystemPrompt = "You are Farnsworth, a coding assistant."
            });

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = SessionId.From("nova-session"),
            Task = "Write some code",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("inherited-conv"),
            Mode = new Mirror(AgentId.From("farnsworth"))
        };

        var result = await manager.SpawnAsync(request);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
        // Bug-hunt MEDIUM (#562): Mirror's model fallback must derive from the
        // mirrored target's descriptor, not the parent's. A regression to
        // `parentDescriptor.ModelId` would report "gpt-4o" here instead of
        // the target's "claude-sonnet-4" — silent metadata corruption that
        // downstream telemetry / cost accounting would compute against the
        // wrong model.
        result.Model.ShouldBe("claude-sonnet-4");
        registeredDescriptor.Value.ShouldNotBeNull();
        registeredDescriptor.Value!.ModelId.ShouldBe("claude-sonnet-4");
        registeredDescriptor.Value.SystemPrompt.ShouldBe("You are Farnsworth, a coding assistant.");
        registeredDescriptor.Value.Kind.ShouldBe(AgentKind.SubAgent);
        registeredAgentId.Value.ShouldNotBeNull();
        // Mirror child id encodes the target agent id in the role slot
        // (#562 locked design), distinguishing the mirror from any role-based
        // Embody spawn against the same parent.
        registeredAgentId.Value!.Value.Value.ShouldStartWith("nova--subagent--farnsworth--");
    }

    [Fact]
    public async Task SpawnAsync_WithModeMirror_UnknownTarget_ThrowsKeyNotFound()
    {
        var (manager, _, _, _) = BuildHarness(
            parentDescriptor: new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "gpt-4o",
                ApiProvider = "openai"
            });

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = SessionId.From("nova-session"),
            Task = "Do something",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("inherited-conv"),
            Mode = new Mirror(AgentId.From("ghost-agent"))
        };

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => manager.SpawnAsync(request));
        ex.Message.ShouldContain("ghost-agent");
    }

    /// <summary>
    /// Bug-hunt HIGH (#562): <c>required</c> is a compile-time guarantee
    /// only. Callers using <c>null!</c> or JSON deserialization quirks can
    /// surface a runtime null Mode. The manager must reject this with a
    /// clean <see cref="ArgumentNullException"/> rather than hitting the
    /// default switch arm and dereferencing <c>request.Mode.GetType()</c>
    /// (NullReferenceException would propagate as an HTTP 500 to callers).
    /// </summary>
    [Fact]
    public async Task SpawnAsync_WithNullMode_ThrowsArgumentNullException()
    {
        var (manager, _, _, _) = BuildHarness(
            parentDescriptor: new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "gpt-4o",
                ApiProvider = "openai"
            });

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = SessionId.From("nova-session"),
            Task = "Do something",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("inherited-conv"),
            Mode = null!
        };

        await Should.ThrowAsync<ArgumentNullException>(
            () => manager.SpawnAsync(request));
    }

    private static (
        DefaultSubAgentManager Manager,
        Mock<IAgentRegistry> Registry,
        Box<AgentDescriptor?> RegisteredDescriptor,
        Box<AgentId?> RegisteredAgentId) BuildHarness(
        AgentDescriptor parentDescriptor,
        AgentDescriptor? extra = null)
    {
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var registeredDescriptor = new Box<AgentDescriptor?>();
        var registeredAgentId = new Box<AgentId?>();

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(parentDescriptor.AgentId)).Returns(parentDescriptor);
        if (extra is not null)
            registry.Setup(r => r.Get(extra.AgentId)).Returns(extra);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry
            .Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback<AgentDescriptor>(d =>
            {
                registeredDescriptor.Value = d;
                registeredAgentId.Value = d.AgentId;
            });

        var options = new TestOptionsMonitor<GatewayOptions>(new GatewayOptions());
        var manager = new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            options,
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultSubAgentManager>>().Object);

        return (manager, registry, registeredDescriptor, registeredAgentId);
    }

    private static Mock<IAgentHandle> CreateHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("nova"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }

    // Mutable wrapper so the Moq callback can publish back into the test scope.
    private sealed class Box<T> { public T? Value { get; set; } }
}
