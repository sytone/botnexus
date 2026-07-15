using BotNexus.Domain;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Citizens;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Citizens;
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

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Phase 2 (#568): the SignalR hub binds the resolved claims-based <see cref="UserId"/> into the
/// <see cref="DefaultUserRegistry"/> lifecycle. On connect the caller's User is upserted and a
/// <c>(signalr, connectionId)</c> <see cref="ChannelIdentity"/> is attached; on disconnect that
/// channel identity is detached while the stable <see cref="UserId"/> is retained so conversation
/// history keyed to it survives a reconnect.
/// </summary>
public sealed class SignalRChannelIdentityLifecycleTests
{
    private static readonly ChannelKey SignalR = ChannelKey.From("signalr");

    [Fact]
    public async Task OnConnected_RegistersUser_AndAttachesSignalRChannelIdentity()
    {
        var registry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);
        var hub = CreateHub(registry, connectionId: "conn-1", userIdentifier: "user-oid-abc");

        await hub.OnConnectedAsync();

        var user = registry.Get(UserId.From("user-oid-abc"));
        user.ShouldNotBeNull();
        user!.ChannelIdentities.ShouldContain(new ChannelIdentity(SignalR, ChannelAddress.From("conn-1")));
    }

    [Fact]
    public async Task OnDisconnected_DetachesChannelIdentity_ButRetainsUser()
    {
        var registry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);
        var hub = CreateHub(registry, connectionId: "conn-1", userIdentifier: "user-oid-abc");

        await hub.OnConnectedAsync();
        await hub.OnDisconnectedAsync(null);

        var user = registry.Get(UserId.From("user-oid-abc"));
        // The User record and its stable UserId survive the disconnect so identity persists...
        user.ShouldNotBeNull();
        // ...but the connection's channel identity is gone.
        user!.ChannelIdentities.ShouldNotContain(new ChannelIdentity(SignalR, ChannelAddress.From("conn-1")));
    }

    [Fact]
    public async Task Reconnect_WithNewConnectionId_KeepsSameUserId()
    {
        // Reconnect scenario: the same authenticated identity connects again on a fresh connection
        // id. The UserId must be reused (not duplicated), so any conversation history keyed to it is
        // preserved across the disconnect/reconnect cycle.
        var registry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);

        var first = CreateHub(registry, connectionId: "conn-1", userIdentifier: "user-oid-abc");
        await first.OnConnectedAsync();
        await first.OnDisconnectedAsync(null);

        var second = CreateHub(registry, connectionId: "conn-2", userIdentifier: "user-oid-abc");
        await second.OnConnectedAsync();

        // Exactly one User exists for the stable identity across both connections.
        registry.GetAll().Count(u => u.Id == UserId.From("user-oid-abc")).ShouldBe(1);
        var user = registry.Get(UserId.From("user-oid-abc"));
        user.ShouldNotBeNull();
        // The reconnected connection's channel identity is attached; the stale one is gone.
        user!.ChannelIdentities.ShouldContain(new ChannelIdentity(SignalR, ChannelAddress.From("conn-2")));
        user.ChannelIdentities.ShouldNotContain(new ChannelIdentity(SignalR, ChannelAddress.From("conn-1")));
    }

    [Fact]
    public async Task TwoConcurrentConnections_SameUser_BothChannelIdentitiesAttached()
    {
        // A second live connection for the same user (e.g. a second tab) adds its own channel
        // identity without dropping the first, and without duplicating the User.
        var registry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);

        var first = CreateHub(registry, connectionId: "conn-1", userIdentifier: "user-oid-abc");
        var second = CreateHub(registry, connectionId: "conn-2", userIdentifier: "user-oid-abc");

        await first.OnConnectedAsync();
        await second.OnConnectedAsync();

        var user = registry.Get(UserId.From("user-oid-abc"));
        user.ShouldNotBeNull();
        user!.ChannelIdentities.ShouldContain(new ChannelIdentity(SignalR, ChannelAddress.From("conn-1")));
        user.ChannelIdentities.ShouldContain(new ChannelIdentity(SignalR, ChannelAddress.From("conn-2")));
        registry.GetAll().Count(u => u.Id == UserId.From("user-oid-abc")).ShouldBe(1);
    }

    [Fact]
    public async Task OnConnected_WithoutRegistryWired_IsNoOp()
    {
        // Legacy hosts / test hubs that do not wire the citizen registry keep working: the
        // lifecycle hooks are a no-op when the registry is absent.
        var hub = CreateHub(userRegistry: null, worldContext: null, connectionId: "conn-1", userIdentifier: "user-oid-abc");

        await Should.NotThrowAsync(() => hub.OnConnectedAsync());
        await Should.NotThrowAsync(() => hub.OnDisconnectedAsync(null));
    }

    private static GatewayHub CreateHub(
        IUserRegistry? userRegistry,
        IWorldContext? worldContext = null,
        string connectionId = "conn-test",
        string? userIdentifier = "user")
    {
        var sessionStore = new InMemorySessionStore();
        var convStore = new InMemoryConversationStore();
        var router = new DefaultConversationRouter(convStore, sessionStore, NullLogger<DefaultConversationRouter>.Instance);
        var dispatcher = new DefaultConversationDispatcher(router, convStore);
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

        var conversationRouter = new Mock<IConversationRouter>();
        conversationRouter
            .Setup(r => r.MuteBindingByAddressAsync(It.IsAny<AgentId?>(), It.IsAny<ChannelKey>(), It.IsAny<ChannelAddress>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var world = worldContext ?? new TestWorldContext();

        return new GatewayHub(
            Mock.Of<IAgentSupervisor>(),
            registry.Object,
            sessionStore,
            activity.Object,
            conversationRouter.Object,
            app,
            NullLogger<GatewayHub>.Instance,
            convStore,
            askUserResponseRegistry: null,
            userRegistry: userRegistry,
            worldContext: userRegistry is null ? null : world)
        {
            Clients = clients.Object,
            Groups = Mock.Of<IGroupManager>(),
            Context = new LifecycleHubCallerContext(connectionId, userIdentifier)
        };
    }

    private sealed class TestWorldContext : IWorldContext
    {
        public WorldIdentity Current { get; } = new() { Id = "world-test", Name = "Test World" };
    }

    private sealed class LifecycleHubCallerContext : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];

        public LifecycleHubCallerContext(string connectionId, string? userIdentifier)
        {
            ConnectionId = connectionId;
            UserIdentifier = userIdentifier;
            User = new ClaimsPrincipal();
            Features = new FeatureCollection();
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier { get; }
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; }
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort() { }
    }
}
