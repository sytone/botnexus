using BotNexus.Domain;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.Dispatching;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;
using System.Text.Json;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3039: <see cref="GatewayHub.OnDisconnectedAsync"/> used to pass <c>Context.ConnectionAborted</c>
/// as the cancellation token for its binding-mute cleanup. ASP.NET Core signals that token *before*
/// invoking the handler, so the cleanup was cancelled by the very event it exists to handle: the
/// conversation scan threw <see cref="TaskCanceledException"/> and the dead connection's bindings
/// were never demoted to <see cref="BindingMode.Muted"/>.
///
/// These tests pin the disconnect path against an ALREADY-CANCELLED <c>ConnectionAborted</c>, which
/// is the real production state. Reverting the token argument turns both of them red.
/// </summary>
public sealed class GatewayHubDisconnectCleanupTokenTests
{
    private static readonly ChannelKey SignalR = ChannelKey.From("signalr");

    [Fact]
    public async Task OnDisconnected_WithCancelledConnectionAborted_StillInvokesMute_WithLiveToken()
    {
        // AC2: the router is still called, and the token it receives is NOT in a cancelled state.
        var observed = new List<CancellationToken>();
        var router = new Mock<IConversationRouter>();
        router
            .Setup(r => r.MuteBindingByAddressAsync(
                It.IsAny<AgentId?>(), It.IsAny<ChannelKey>(), It.IsAny<ChannelAddress>(), It.IsAny<CancellationToken>()))
            .Callback<AgentId?, ChannelKey, ChannelAddress, CancellationToken>((_, _, _, ct) => observed.Add(ct))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(router.Object, new InMemoryConversationStore(), connectionId: "conn-dead", abortCancelled: true);

        await hub.OnDisconnectedAsync(null);

        observed.Count.ShouldBe(1, "disconnect cleanup must run even though the connection has aborted");
        observed[0].IsCancellationRequested.ShouldBeFalse(
            "the cleanup must not observe the connection's own abort token -- that token is already " +
            "cancelled by the time OnDisconnectedAsync runs (#3039)");
    }

    [Fact]
    public async Task OnDisconnected_WithCancelledConnectionAborted_PersistsBindingAsMuted()
    {
        // AC3: end-to-end through the real router and a real store -- the binding for the dead
        // connection is actually demoted and saved, not merely "attempted".
        // The store MUST honour the cancellation token, exactly as SqliteConversationStore does.
        // A plain InMemoryConversationStore ignores `ct` outright, which makes this assertion
        // vacuous -- it passes even with the defect reintroduced. See CancellationHonouringStore.
        var store = new CancellationHonouringStore(new InMemoryConversationStore());
        var sessions = new InMemorySessionStore();
        var router = new DefaultConversationRouter(store, sessions, NullLogger<DefaultConversationRouter>.Instance);

        var conversationId = ConversationId.From("c_disconnect_3039");
        var conversation = new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("farnsworth")
        };
        conversation.ChannelBindings.Add(new ChannelBinding
        {
            BindingId = BindingId.From("b-live"),
            ChannelType = SignalR,
            ChannelAddress = ChannelAddress.From("conn-dead"),
            Mode = BindingMode.Interactive
        });
        await store.SaveAsync(conversation, CancellationToken.None);

        var hub = CreateHub(router, store, connectionId: "conn-dead", abortCancelled: true);

        await hub.OnDisconnectedAsync(null);

        var reloaded = await store.GetAsync(conversationId, CancellationToken.None);
        reloaded.ShouldNotBeNull();
        var binding = reloaded!.ChannelBindings.Single(b => b.BindingId.Value == "b-live");
        binding.Mode.ShouldBe(
            BindingMode.Muted,
            "the dead connection's binding must be muted; leaving it Interactive is the #3039 defect");
    }

    [Fact]
    public async Task OnDisconnected_StoreFailure_IsStillSwallowedAndLogged()
    {
        // AC5: the fix removes the EXPECTED cancellation, it must not remove the guard against a
        // genuine store failure. A throwing router must not surface out of OnDisconnectedAsync.
        var router = new Mock<IConversationRouter>();
        router
            .Setup(r => r.MuteBindingByAddressAsync(
                It.IsAny<AgentId?>(), It.IsAny<ChannelKey>(), It.IsAny<ChannelAddress>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store is wedged"));

        var hub = CreateHub(router.Object, new InMemoryConversationStore(), connectionId: "conn-dead", abortCancelled: true);

        await Should.NotThrowAsync(() => hub.OnDisconnectedAsync(null));
    }

    private static GatewayHub CreateHub(
        IConversationRouter conversationRouter,
        IConversationStore convStore,
        string connectionId,
        bool abortCancelled)
    {
        var sessionStore = new InMemorySessionStore();
        var storeRouter = new DefaultConversationRouter(convStore, sessionStore, NullLogger<DefaultConversationRouter>.Instance);
        var dispatcher = new DefaultConversationDispatcher(storeRouter, convStore);
        var coordinator = new SessionCompactionCoordinator(
            Mock.Of<ISessionCompactor>(),
            sessionStore,
            Mock.Of<IAgentSupervisor>(),
            Mock.Of<IChannelManager>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<SessionCompactionCoordinator>.Instance);
        var app = new GatewayHubApplicationService(
            new CapturingInboundMessageOrchestrator(),
            Mock.Of<ISessionWarmupService>(),
            dispatcher,
            coordinator);

        var caller = new Mock<IGatewayHubClient>();
        caller.Setup(p => p.Connected(It.IsAny<ConnectedPayload>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients<IGatewayHubClient>>();
        clients.SetupGet(c => c.Caller).Returns(caller.Object);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([]);

        var activity = new Mock<IActivityBroadcaster>();
        activity.Setup(a => a.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        return new GatewayHub(
            Mock.Of<IAgentSupervisor>(),
            registry.Object,
            sessionStore,
            activity.Object,
            conversationRouter,
            app,
            NullLogger<GatewayHub>.Instance,
            convStore,
            askUserPromptResolver: null,
            userRegistry: null,
            worldContext: null)
        {
            Clients = clients.Object,
            Groups = Mock.Of<IGroupManager>(),
            Context = new AbortedHubCallerContext(connectionId, abortCancelled)
        };
    }

    /// <summary>
    /// Wraps a conversation store so that every operation observes its <see cref="CancellationToken"/>,
    /// which is what <c>SqliteConversationStore</c> does in production and what
    /// <c>InMemoryConversationStore</c> conspicuously does NOT do -- it drops <c>ct</c> on the floor.
    ///
    /// Without this decorator the end-to-end #3039 assertion is vacuous: the mutation run on
    /// 2026-08-12 (runId 20260812175515-41a8c8a9) reintroduced the defect and this test still passed,
    /// because the fake store happily completed the scan using an already-cancelled token.
    /// </summary>
    private sealed class CancellationHonouringStore(IConversationStore inner) : IConversationStore
    {
        private static void Check(CancellationToken ct) => ct.ThrowIfCancellationRequested();

        public Task<Conversation?> GetAsync(ConversationId id, CancellationToken ct = default)
        { Check(ct); return inner.GetAsync(id, ct); }

        public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
        { Check(ct); return inner.ListAsync(agentId, ct); }

        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
        { Check(ct); return inner.ListForCitizenAsync(citizen, ct); }

        public Task AddParticipantsAsync(ConversationId id, IEnumerable<SessionParticipant> participants, CancellationToken ct = default)
        { Check(ct); return inner.AddParticipantsAsync(id, participants, ct); }

        public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
        { Check(ct); return inner.CreateAsync(conversation, ct); }

        public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
        { Check(ct); return inner.SaveAsync(conversation, ct); }

        public Task ArchiveAsync(ConversationId id, CancellationToken ct = default)
        { Check(ct); return inner.ArchiveAsync(id, ct); }

        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey type, ChannelAddress address, CancellationToken ct = default)
        { Check(ct); return inner.ResolveByBindingAsync(agentId, type, address, ct); }

        public Task TouchAsync(ConversationId id, CancellationToken ct = default)
        { Check(ct); return inner.TouchAsync(id, ct); }

        public Task PinAsync(ConversationId id, bool pin, CancellationToken ct = default)
        { Check(ct); return inner.PinAsync(id, pin, ct); }

        public Task<bool> AddBindingAsync(ConversationId id, ChannelBinding binding, CancellationToken ct = default)
        { Check(ct); return inner.AddBindingAsync(id, binding, ct); }

        public Task<bool> RemoveBindingAsync(ConversationId id, BindingId bindingId, CancellationToken ct = default)
        { Check(ct); return inner.RemoveBindingAsync(id, bindingId, ct); }

        public Task<bool> MoveBindingAsync(ConversationId from, ConversationId to, BindingId bindingId, CancellationToken ct = default)
        { Check(ct); return inner.MoveBindingAsync(from, to, bindingId, ct); }

        public Task<Conversation?> PatchMetadataAsync(ConversationId id, ConversationMetadataPatch patch, CancellationToken ct = default)
        { Check(ct); return inner.PatchMetadataAsync(id, patch, ct); }

        public Task<Conversation?> PatchOverrideAsync(ConversationId id, ConversationOverridePatch patch, CancellationToken ct = default)
        { Check(ct); return inner.PatchOverrideAsync(id, patch, ct); }

        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
        { Check(ct); return inner.GetSummariesAsync(ct); }

        public Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId id, CancellationToken ct = default)
        { Check(ct); return inner.GetCanvasStateAsync(id, ct); }

        public Task<bool> SetCanvasStateKeyAsync(ConversationId id, string key, JsonElement value, CancellationToken ct = default)
        { Check(ct); return inner.SetCanvasStateKeyAsync(id, key, value, ct); }

        public Task DeleteCanvasStateKeyAsync(ConversationId id, string key, CancellationToken ct = default)
        { Check(ct); return inner.DeleteCanvasStateKeyAsync(id, key, ct); }

        public Task ClearCanvasStateAsync(ConversationId id, CancellationToken ct = default)
        { Check(ct); return inner.ClearCanvasStateAsync(id, ct); }
    }

    /// <summary>
    /// Reproduces the production shape: by the time OnDisconnectedAsync runs, ConnectionAborted has
    /// already fired. The existing lifecycle test double hard-codes CancellationToken.None, which is
    /// why it could never have caught this.
    /// </summary>
    private sealed class AbortedHubCallerContext : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];
        private readonly CancellationTokenSource _cts = new();

        public AbortedHubCallerContext(string connectionId, bool abortCancelled)
        {
            ConnectionId = connectionId;
            User = new ClaimsPrincipal();
            Features = new FeatureCollection();
            if (abortCancelled)
                _cts.Cancel();
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; }
        public override CancellationToken ConnectionAborted => _cts.Token;
        public override void Abort() => _cts.Cancel();
    }
}
