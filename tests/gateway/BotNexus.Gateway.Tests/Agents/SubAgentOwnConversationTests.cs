using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Behaviour tests for issue #2338: a sub-agent run is a conversation in its own right.
/// </summary>
/// <remarks>
/// <para>
/// Before this change <c>DefaultSubAgentManager</c> assigned the parent's <see cref="ConversationId"/>
/// straight onto the child session. Because <c>SignalRChannelAdapter</c> routes purely by conversation
/// id (<c>Clients.Group("conversation:{id}")</c>), that made every child tool call, thinking delta and
/// content delta land in the <em>parent's</em> group, interleaved with the parent's own turn.
/// </para>
/// <para>
/// These tests pin the replacement contract: the child gets its own minted id, the link to the
/// supervisor is the <c>ParentConversationId</c> edge (plus <c>SpawningToolCallId</c>), and the child
/// conversation is created through the sanctioned <c>ConversationFactory</c> seam so it carries
/// <c>Kind = AgentSubAgent</c> / <c>Source = Agent</c>.
/// </para>
/// </remarks>
public sealed class SubAgentOwnConversationTests
{
    private static readonly ConversationId ParentConversation = ConversationId.From("c_parent2338");

    [Fact]
    public async Task SpawnAsync_MintsChildConversation_DistinctFromParent()
    {
        var created = new List<Conversation>();
        var childSession = new GatewaySession();
        var manager = BuildManager(created, childSession, out _);

        var info = await manager.SpawnAsync(BuildRequest());

        info.ChildConversationId.ShouldNotBeNull();
        info.ChildConversationId!.Value.ShouldNotBe(ParentConversation);
        childSession.ConversationId.ShouldBe(info.ChildConversationId.Value);
        // The whole point: the child session must never be bound to the parent's group key.
        childSession.ConversationId.ShouldNotBe(ParentConversation);
    }

    [Fact]
    public async Task SpawnAsync_PersistsChildConversation_WithParentEdgeAndSpawningToolCall()
    {
        var created = new List<Conversation>();
        var manager = BuildManager(created, new GatewaySession(), out _);

        var info = await manager.SpawnAsync(BuildRequest(spawningToolCallId: "call_abc123"));

        var child = created.ShouldHaveSingleItem();
        child.ConversationId.ShouldBe(info.ChildConversationId!.Value);
        child.ParentConversationId.ShouldBe(ParentConversation);
        child.SpawningToolCallId.ShouldBe("call_abc123");
        // Provenance comes from the ConversationFactory sub-agent seam, not from hand-stamping.
        child.Kind.ShouldBe(ConversationKind.AgentSubAgent);
        child.Source.ShouldBe(ConversationSource.Agent);
    }

    [Fact]
    public async Task SpawnAsync_ChildConversation_IsOwnedByChildAgent_AndInitiatedByParent()
    {
        var created = new List<Conversation>();
        var manager = BuildManager(created, new GatewaySession(), out _);

        await manager.SpawnAsync(BuildRequest());

        var child = created.ShouldHaveSingleItem();
        child.AgentId.Value.ShouldContain("subagent");
        child.Initiator.ShouldBe(CitizenId.Of(AgentId.From("parent")));
    }

    [Fact]
    public async Task SpawnAsync_StillSucceeds_WhenChildConversationPersistenceFails()
    {
        // The conversation row is a best-effort side effect, exactly like the sub-agent session row.
        // A store failure must not abort the spawn - and must not fall back to sharing the parent id.
        var conversationStore = new Mock<IConversationStore>();
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store down"));

        var childSession = new GatewaySession();
        var manager = BuildManager(conversationStore.Object, childSession);

        var info = await manager.SpawnAsync(BuildRequest());

        info.Status.ShouldBe(SubAgentStatus.Running);
        info.ChildConversationId.ShouldNotBeNull();
        childSession.ConversationId.ShouldNotBe(ParentConversation);
    }

    [Fact]
    public async Task SpawnAsync_WithoutConversationStore_StillGivesChildItsOwnConversation()
    {
        // Minimal hosts / tests may not wire an IConversationStore. The row is then simply not
        // written, but the child must still get a distinct id so the parent's group stays clean.
        var childSession = new GatewaySession();
        var manager = BuildManager(conversationStore: null, childSession);

        var info = await manager.SpawnAsync(BuildRequest());

        info.ChildConversationId.ShouldNotBeNull();
        childSession.ConversationId.ShouldNotBe(ParentConversation);
    }

    private static SubAgentSpawnRequest BuildRequest(string? spawningToolCallId = null) => new()
    {
        ParentAgentId = AgentId.From("parent"),
        ParentSessionId = SessionId.From("parent-session"),
        Task = "do the thing",
        Mode = new Embody(SubAgentArchetype.General),
        InheritedConversationId = ParentConversation,
        SpawningToolCallId = spawningToolCallId
    };

    private static DefaultSubAgentManager BuildManager(
        List<Conversation> created,
        GatewaySession childSession,
        out Mock<IConversationStore> conversationStore)
    {
        conversationStore = new Mock<IConversationStore>();
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => created.Add(c))
            .ReturnsAsync((Conversation c, CancellationToken _) => c);
        return BuildManager(conversationStore.Object, childSession);
    }

    private static DefaultSubAgentManager BuildManager(
        IConversationStore? conversationStore,
        GatewaySession childSession)
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childSession);
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.AgentId).Returns(AgentId.From("child-agent"));
        handle.Setup(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return new AgentResponse { Content = "never" };
            });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("parent"),
            DisplayName = "Parent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            SystemPrompt = "You are a test agent."
        });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var activity = new Mock<IActivityBroadcaster>();
        activity.Setup(a => a.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var options = new Mock<IOptionsMonitor<GatewayOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new GatewayOptions());

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            activity.Object,
            new Mock<IChannelDispatcher>().Object,
            options.Object,
            NullLogger<DefaultSubAgentManager>.Instance,
            sessionStore: sessionStore.Object,
            conversationStore: conversationStore);
    }
}
