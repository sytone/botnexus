using BotNexus.Domain;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Citizens;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Citizens;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.Dispatching;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using System.Security.Claims;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for #568: SignalR auth Phase 2 — ChannelIdentity lifecycle + DefaultUserRegistry integration.
/// Verifies that:
/// 1. OnConnectedAsync registers/upserts user with ChannelIdentity (signalr, connectionId)
/// 2. OnDisconnectedAsync removes the ChannelIdentity for this connection
/// 3. Same UserId survives reconnect — conversation history preserved
/// 4. Anonymous connections (no auth) skip registry operations
/// </summary>
public sealed class SignalRChannelIdentityLifecycleTests
{
    private static readonly WorldIdentity TestWorld = new() { Id = "test-world", Name = "Test World" };

    [Fact]
    public async Task OnConnected_AuthenticatedUser_RegistersWithChannelIdentity()
    {
        var userRegistry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);
        var hub = CreateHubWithRegistry(userRegistry, connectionId: "conn-1", userIdentifier: "user-oid-123");

        await hub.OnConnectedAsync();

        var user = userRegistry.Get(UserId.From("user-oid-123"));
        user.ShouldNotBeNull("User should be registered on connect");
        user.ChannelIdentities.ShouldContain(
            ci => ci.Channel == ChannelKey.From("signalr") && ci.SenderAddress == ChannelAddress.From("conn-1"));
    }

    [Fact]
    public async Task OnConnected_ReconnectingSameUser_AddsNewChannelIdentity()
    {
        var userRegistry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);

        // First connection
        var hub1 = CreateHubWithRegistry(userRegistry, connectionId: "conn-1", userIdentifier: "user-oid-456");
        await hub1.OnConnectedAsync();

        // Second connection (same user, different connectionId — e.g. browser refresh)
        var hub2 = CreateHubWithRegistry(userRegistry, connectionId: "conn-2", userIdentifier: "user-oid-456");
        await hub2.OnConnectedAsync();

        var user = userRegistry.Get(UserId.From("user-oid-456"));
        user.ShouldNotBeNull();
        user.ChannelIdentities.Count.ShouldBe(2);
        user.ChannelIdentities.ShouldContain(
            ci => ci.Channel == ChannelKey.From("signalr") && ci.SenderAddress == ChannelAddress.From("conn-1"));
        user.ChannelIdentities.ShouldContain(
            ci => ci.Channel == ChannelKey.From("signalr") && ci.SenderAddress == ChannelAddress.From("conn-2"));
    }

    [Fact]
    public async Task OnDisconnected_RemovesChannelIdentityForConnection()
    {
        var userRegistry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);
        var hub = CreateHubWithRegistry(userRegistry, connectionId: "conn-1", userIdentifier: "user-oid-789");

        await hub.OnConnectedAsync();
        await hub.OnDisconnectedAsync(null);

        var user = userRegistry.Get(UserId.From("user-oid-789"));
        user.ShouldNotBeNull("User record should persist after disconnect");
        user.ChannelIdentities.ShouldNotContain(
            ci => ci.Channel == ChannelKey.From("signalr") && ci.SenderAddress == ChannelAddress.From("conn-1"),
            "Disconnected connection's identity should be removed");
    }

    [Fact]
    public async Task OnDisconnected_MultipleConnections_OnlyRemovesDisconnectedOne()
    {
        var userRegistry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);

        // Two connections for same user
        var hub1 = CreateHubWithRegistry(userRegistry, connectionId: "conn-a", userIdentifier: "user-multi");
        var hub2 = CreateHubWithRegistry(userRegistry, connectionId: "conn-b", userIdentifier: "user-multi");
        await hub1.OnConnectedAsync();
        await hub2.OnConnectedAsync();

        // Disconnect first connection
        await hub1.OnDisconnectedAsync(null);

        var user = userRegistry.Get(UserId.From("user-multi"));
        user.ShouldNotBeNull();
        user.ChannelIdentities.Count.ShouldBe(1);
        user.ChannelIdentities.ShouldContain(
            ci => ci.SenderAddress == ChannelAddress.From("conn-b"),
            "Second connection identity should remain");
    }

    [Fact]
    public async Task OnConnected_AnonymousUser_SkipsRegistryOperation()
    {
        var userRegistry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);
        var hub = CreateHubWithRegistry(userRegistry, connectionId: "conn-anon", userIdentifier: null);

        await hub.OnConnectedAsync();

        userRegistry.GetAll().ShouldBeEmpty("Anonymous users should not be registered");
    }

    [Fact]
    public async Task OnConnected_NoRegistryAvailable_DoesNotThrow()
    {
        // Hub constructed without IUserRegistry (null) — should not crash
        var hub = CreateHubWithRegistry(null, connectionId: "conn-no-reg", userIdentifier: "user-123");

        await Should.NotThrowAsync(() => hub.OnConnectedAsync());
    }

    [Fact]
    public async Task ReconnectPreservesUserId_ConversationHistoryIntact()
    {
        var userRegistry = new DefaultUserRegistry(NullLogger<DefaultUserRegistry>.Instance);

        // Connect, then disconnect, then reconnect
        var hub1 = CreateHubWithRegistry(userRegistry, connectionId: "conn-old", userIdentifier: "stable-user");
        await hub1.OnConnectedAsync();
        await hub1.OnDisconnectedAsync(null);

        var hub2 = CreateHubWithRegistry(userRegistry, connectionId: "conn-new", userIdentifier: "stable-user");
        await hub2.OnConnectedAsync();

        // Same user record should exist with new connection
        var user = userRegistry.Get(UserId.From("stable-user"));
        user.ShouldNotBeNull("User should survive reconnect");
        user.ChannelIdentities.ShouldHaveSingleItem()
            .SenderAddress.ShouldBe(ChannelAddress.From("conn-new"));
    }

    // --- Factory ---

    private static GatewayHub CreateHubWithRegistry(
        IUserRegistry? userRegistry,
        string connectionId = "conn-test",
        string? userIdentifier = "user")
    {
        var sessionStore = new InMemorySessionStore();
        var convStore = new InMemoryConversationStore();
        var router = new DefaultConversationRouter(
            convStore,
            sessionStore,
            NullLogger<DefaultConversationRouter>.Instance);
        var dispatcher = new DefaultConversationDispatcher(router, convStore);
        var supervisor = Mock.Of<IAgentSupervisor>();
        var compactor = Mock.Of<ISessionCompactor>();
        var options = new TestOptionsMonitor<CompactionOptions>(new CompactionOptions());
        var coordinator = new SessionCompactionCoordinator(
            compactor,
            sessionStore,
            supervisor,
            Mock.Of<IChannelManager>(),
            options,
            NullLogger<SessionCompactionCoordinator>.Instance);

        var worldContext = new Mock<IWorldContext>();
        worldContext.Setup(w => w.Current).Returns(TestWorld);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns(Array.Empty<AgentDescriptor>());

        var caller = new Mock<IGatewayHubClient>();
        caller.Setup(c => c.Connected(It.IsAny<ConnectedPayload>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients<IGatewayHubClient>>();
        clients.SetupGet(c => c.Caller).Returns(caller.Object);

        var activity = new Mock<IActivityBroadcaster>();
        activity.Setup(a => a.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var hub = new GatewayHub(
            supervisor,
            registry.Object,
            sessionStore,
            new CapturingInboundMessageOrchestrator(),
            activity.Object,
            compactor,
            Mock.Of<ISessionWarmupService>(),
            dispatcher,
            router,
            options,
            NullLogger<GatewayHub>.Instance,
            convStore,
            null, // askUserResponseRegistry
            null, // resetService
            coordinator,
            userRegistry,
            worldContext.Object)
        {
            Clients = clients.Object,
            Groups = Mock.Of<IGroupManager>(),
            Context = new TestHubCallerContext(connectionId, userIdentifier)
        };

        return hub;
    }

    private sealed class TestHubCallerContext(string connectionId, string? userIdentifier) : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier { get; } = userIdentifier;
        public override ClaimsPrincipal? User { get; } = userIdentifier != null
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userIdentifier), new Claim("name", "Test User")], "Bearer"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort() { }
    }
}
