using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Integration;

/// <summary>
/// Regression-pin scenarios for the SignalR channel covering recently fixed portal-usability bugs.
/// Each <c>[Fact]</c> documents the GitHub issue it pins so a silent re-regression of the same
/// defect class fails CI with a meaningful test name. New behaviour belongs in
/// <c>SignalRIntegrationTests</c> / <c>SignalRConversationRoutingTests</c>; this file is for
/// holding-the-line scenarios against historical pain.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class SignalRReliabilityTests : IAsyncDisposable
{
    private const string TestAgentId = "test-agent";

    // ── #441 — Portal reconnect must not create a new conversation ──────────

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/441">#441</see>: each browser
    /// reconnect used to spawn a fresh <c>signalr:{agentId}</c> conversation, polluting the
    /// conversation list. <see cref="GatewayHub.ResolveOrCreateSessionAsync"/> now uses
    /// <c>agentId</c> as the <see cref="ChannelAddress"/>, so distinct
    /// <c>Context.ConnectionId</c>s for the same agent must resolve to the same conversation.
    /// </summary>
    [Fact]
    public async Task Reconnect_AfterDisconnect_ToSameAgent_ResolvesToSameConversation()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        string firstSessionId;
        // First connection — represents the original browser tab.
        await using (var first = await CreateStartedConnection(factory, cts.Token))
        {
            var firstResult = await first.InvokeAsync<JsonElement>(
                "SendMessage", TestAgentId, "signalr", "hello before disconnect", (string?)null, cts.Token);
            firstSessionId = firstResult.GetProperty("sessionId").GetString()!;
        }

        // Second connection from a *different* SignalR ConnectionId — the reconnect.
        await using var second = await CreateStartedConnection(factory, cts.Token);
        var secondResult = await second.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "hello after reconnect", (string?)null, cts.Token);
        var secondSessionId = secondResult.GetProperty("sessionId").GetString()!;

        dispatcher.Messages.Count.ShouldBe(2);

        var sessionStore = factory.Services.GetRequiredService<ISessionStore>();
        var firstSession = await sessionStore.GetAsync(SessionId.From(firstSessionId), cts.Token);
        var secondSession = await sessionStore.GetAsync(SessionId.From(secondSessionId), cts.Token);

        firstSession.ShouldNotBeNull();
        secondSession.ShouldNotBeNull();
        firstSession!.Session.ConversationId.ShouldNotBeNull("first connection must resolve a conversation; #441");
        secondSession!.Session.ConversationId.ShouldNotBeNull("reconnect must resolve a conversation; #441");
        secondSession.Session.ConversationId.ShouldBe(
            firstSession.Session.ConversationId,
            "reconnect must resolve to the same conversation as the original session; #441");
    }

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/441">#441</see>: multiple
    /// browser tabs hitting the same agent must all share one portal conversation regardless
    /// of how many SignalR connections are open. Closes the historical "N tabs = N duplicate
    /// conversations" defect.
    /// </summary>
    [Fact]
    public async Task MultipleSimultaneousConnections_ToSameAgent_AllRouteToSameConversation()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var tabA = await CreateStartedConnection(factory, cts.Token);
        await using var tabB = await CreateStartedConnection(factory, cts.Token);
        await using var tabC = await CreateStartedConnection(factory, cts.Token);

        var aResult = await tabA.InvokeAsync<JsonElement>("SendMessage", TestAgentId, "signalr", "from tab A", (string?)null, cts.Token);
        var bResult = await tabB.InvokeAsync<JsonElement>("SendMessage", TestAgentId, "signalr", "from tab B", (string?)null, cts.Token);
        var cResult = await tabC.InvokeAsync<JsonElement>("SendMessage", TestAgentId, "signalr", "from tab C", (string?)null, cts.Token);

        dispatcher.Messages.Count.ShouldBe(3);

        var sessionStore = factory.Services.GetRequiredService<ISessionStore>();
        var sessionIds = new[]
        {
            aResult.GetProperty("sessionId").GetString()!,
            bResult.GetProperty("sessionId").GetString()!,
            cResult.GetProperty("sessionId").GetString()!,
        };

        var conversationIds = new List<ConversationId?>();
        foreach (var id in sessionIds)
        {
            var session = await sessionStore.GetAsync(SessionId.From(id), cts.Token);
            session.ShouldNotBeNull();
            conversationIds.Add(session!.Session.ConversationId);
        }

        conversationIds.Distinct().Count().ShouldBe(
            1,
            "all tabs for the same agent must route into a single portal conversation; #441");
        conversationIds[0].ShouldNotBeNull();
    }

    // ── #332 — Cross-channel updates must reach connected SignalR clients ──

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/332">#332</see>: when a
    /// message arrives via an external channel (Telegram, etc.) and a conversation is created
    /// or updated, every connected SignalR client must see the <c>ConversationChanged</c>
    /// broadcast so the portal can refresh without a manual reload.
    /// </summary>
    [Fact]
    public async Task ConversationChangeNotification_BroadcastsToAllConnectedSignalRClients()
    {
        await using var factory = CreateTestFactory(services =>
        {
            services.AddSingleton<IConversationChangeNotifier, SignalRConversationChangeNotifier>();
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var browserOne = await CreateStartedConnection(factory, cts.Token);
        await using var browserTwo = await CreateStartedConnection(factory, cts.Token);

        var oneTcs = new TaskCompletionSource<ConversationChangedPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoTcs = new TaskCompletionSource<ConversationChangedPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = browserOne.On<ConversationChangedPayload>("ConversationChanged", payload => oneTcs.TrySetResult(payload));
        using var __ = browserTwo.On<ConversationChangedPayload>("ConversationChanged", payload => twoTcs.TrySetResult(payload));

        var notifier = factory.Services.GetRequiredService<IConversationChangeNotifier>();
        await notifier.NotifyConversationChangedAsync(
            changeType: "created",
            agentId: TestAgentId,
            conversationId: "conv-from-telegram",
            cts.Token);

        var payloadOne = await oneTcs.Task.WaitAsync(cts.Token);
        var payloadTwo = await twoTcs.Task.WaitAsync(cts.Token);

        payloadOne.ConversationId.ShouldBe("conv-from-telegram");
        payloadOne.AgentId.ShouldBe(TestAgentId);
        payloadOne.ChangeType.ShouldBe("created");
        payloadTwo.ConversationId.ShouldBe("conv-from-telegram",
            "every connected SignalR client must receive cross-channel conversation updates; #332");
    }

    // ── #314 — SendMessage after agent switch routes to the new agent ───────

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/314">#314</see>: switching
    /// agents in the portal used to deliver responses to the previous agent's conversation
    /// (e.g. <c>legacy:assistant</c>). The dispatched <see cref="InboundMessage"/> must carry
    /// the *current* agent's id and resolve to that agent's portal conversation.
    /// </summary>
    [Fact]
    public async Task SendMessage_AfterAgentSwitch_RoutesToNewAgentsConversation_NotPrevious()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token, agentId: "nova");
        await RegisterAgentAsync(factory, cts.Token, agentId: "quill");

        await using var connection = await CreateStartedConnection(factory, cts.Token);

        var novaResult = await connection.InvokeAsync<JsonElement>("SendMessage", "nova", "signalr", "hello nova", (string?)null, cts.Token);
        var quillResult = await connection.InvokeAsync<JsonElement>("SendMessage", "quill", "signalr", "hello quill", (string?)null, cts.Token);

        dispatcher.Messages.Count.ShouldBe(2);
        dispatcher.Messages[0].RoutingHints.ShouldNotBeNull();
        dispatcher.Messages[0].RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("nova");
        dispatcher.Messages[1].RoutingHints.ShouldNotBeNull();
        dispatcher.Messages[1].RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("quill");

        var sessionStore = factory.Services.GetRequiredService<ISessionStore>();
        var novaSession = await sessionStore.GetAsync(SessionId.From(novaResult.GetProperty("sessionId").GetString()!), cts.Token);
        var quillSession = await sessionStore.GetAsync(SessionId.From(quillResult.GetProperty("sessionId").GetString()!), cts.Token);

        novaSession.ShouldNotBeNull();
        quillSession.ShouldNotBeNull();
        novaSession!.Session.ConversationId.ShouldNotBeNull("nova send must resolve a conversation");
        quillSession!.Session.ConversationId.ShouldNotBeNull("quill send must resolve a conversation");
        quillSession.Session.ConversationId.ShouldNotBe(
            novaSession.Session.ConversationId,
            "agent switch must route into the new agent's conversation, not the previous agent's; #314");
    }

    // ── #216 — NO_REPLY sentinel must not surface in the UI ─────────────────

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/216">#216</see>: the
    /// <c>NO_REPLY</c> sentinel agents use for silent housekeeping was being rendered as
    /// literal text in the Blazor UI. <see cref="SignalRChannelAdapter.SendAsync"/> must
    /// drop messages whose trimmed content equals <c>NO_REPLY</c>.
    /// </summary>
    [Fact]
    public async Task NoReplySentinel_IsSuppressed_AndNotForwardedAsContentDelta()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "no-reply-session";
        #pragma warning disable CS0618 // JoinSession is the supported way to subscribe to a synthetic test session
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);
        #pragma warning restore CS0618

        var contentReceived = false;
        using var _ = connection.On<ContentDeltaPayload>("ContentDelta", _ => contentReceived = true);

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From(TestAgentId),
            SessionId = sessionId,
            Content = "NO_REPLY"
        }, cts.Token);

        // Give the hub a moment to fan-out — the assertion is the absence of an event.
        await Task.Delay(250, cts.Token);
        contentReceived.ShouldBeFalse(
            "NO_REPLY sentinel must not be forwarded as a ContentDelta event over SignalR; #216");
    }

    /// <summary>
    /// Defensive companion to <see cref="NoReplySentinel_IsSuppressed_AndNotForwardedAsContentDelta"/>:
    /// ordinary messages with leading/trailing whitespace are still delivered — the
    /// suppression only fires for the exact <c>NO_REPLY</c> sentinel.
    /// </summary>
    [Fact]
    public async Task NonSentinelContent_StillDelivered_AsContentDelta()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "normal-reply-session";
        #pragma warning disable CS0618
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);
        #pragma warning restore CS0618

        var receivedTcs = new TaskCompletionSource<ContentDeltaPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<ContentDeltaPayload>("ContentDelta", payload => receivedTcs.TrySetResult(payload));

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From(TestAgentId),
            SessionId = sessionId,
            Content = "  hello  "
        }, cts.Token);

        var payload = await receivedTcs.Task.WaitAsync(cts.Token);
        payload.ContentDelta.ShouldBe("  hello  ",
            "whitespace-padded content must still be delivered verbatim — only the literal NO_REPLY sentinel is suppressed");
    }

    // ── #192 — Steering must be dispatched as a control message ────────────

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/192">#192</see>: a steer used
    /// to fall through as a normal user prompt in the wrong conversation when the agent
    /// wasn't running. The dispatched <see cref="InboundMessage"/> for a <c>Steer</c> call
    /// must carry the <c>control=steer</c> metadata so downstream code can route it as a
    /// control plane message rather than a regular turn.
    /// </summary>
    [Fact]
    public async Task Steer_DispatchesAsControlMessage_WithSteerMetadata_NotRegularPrompt()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>(
            "Steer", TestAgentId, "steer-session-1", "stop and reconsider", (string?)null, cts.Token);

        var dispatched = dispatcher.Messages.ShouldHaveSingleItem();
        dispatched.Metadata.ShouldNotBeNull();
        dispatched.Metadata.ShouldContainKey("messageType");
        dispatched.Metadata["messageType"].ShouldBe("steer");
        dispatched.Metadata.ShouldContainKey("control");
        dispatched.Metadata["control"].ShouldBe(
            "steer",
            "Steer hub method must dispatch with control=steer metadata so it cannot be re-routed as a regular prompt; #192");
    }

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/192">#192</see>: when the
    /// caller supplies an explicit <c>conversationId</c>, the steer must carry that id on the
    /// dispatched <see cref="InboundMessage"/> so the router targets that conversation rather
    /// than falling through to a different SignalR-created conversation.
    /// </summary>
    [Fact]
    public async Task Steer_WithExplicitConversationId_CarriesIt_OnDispatchedInbound()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>(
            "Steer", TestAgentId, "steer-session-2", "be more thorough", "explicit-target-conv", cts.Token);

        var dispatched = dispatcher.Messages.ShouldHaveSingleItem();
        dispatched.RoutingHints.ShouldNotBeNull();
        dispatched.RoutingHints!.RequestedConversationId!.Value.Value.ShouldBe(
            "explicit-target-conv",
            "Steer with explicit conversationId must carry it through; #192");
    }

    // ── #130 — Stale bindings must be muted on disconnect ──────────────────

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/130">#130</see>:
    /// SignalR reconnects used to accumulate <c>Interactive</c> bindings indefinitely, so
    /// fan-out kept delivering to dead connections. <see cref="GatewayHub.OnDisconnectedAsync"/>
    /// must call <see cref="IConversationRouter.MuteBindingByAddressAsync"/> with the dropped
    /// connection's id (encoded as <see cref="ChannelAddress"/>) so any binding keyed to that
    /// connection is structurally silenced.
    /// </summary>
    [Fact]
    public async Task OnDisconnect_MutesBindingByConnectionId_OnTheConversationRouter()
    {
        var recording = new RecordingConversationRouter();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IConversationRouter>();
            services.AddSingleton<IConversationRouter>(recording);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        string connectionId;
        await using (var connection = await CreateStartedConnection(factory, cts.Token))
        {
            connectionId = connection.ConnectionId ?? throw new InvalidOperationException("connection id was null");
        }

        // OnDisconnectedAsync is fire-and-forget on the server. Poll briefly so the test
        // doesn't depend on hub-internal timing being instantaneous.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && recording.MuteByAddressCalls.Count == 0)
            await Task.Delay(50, cts.Token);

        var muteCall = recording.MuteByAddressCalls.ShouldHaveSingleItem();
        muteCall.ChannelType.Value.ShouldBe("signalr");
        muteCall.ChannelAddress.Value.ShouldBe(
            connectionId,
            "OnDisconnectedAsync must mute the binding keyed to the dropped connection id; #130");
        muteCall.AgentId.ShouldBeNull("disconnect search spans every agent's conversations");
    }

    // ── #235 — Sub-agent spawn notification must not include the full prompt ──

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/235">#235</see>: the portal
    /// used to receive the entire sub-agent task prompt in the spawn notification, blowing
    /// up the chat with multi-paragraph payloads. <see cref="SubAgentSignalRBridge"/> must
    /// truncate the task to a compact summary (currently 120 characters + ellipsis).
    /// </summary>
    [Fact]
    public async Task SubAgentSpawnNotification_TruncatesLongTask_To120CharsPlusEllipsis()
    {
        await using var factory = CreateTestFactory(services =>
        {
            services.AddSingleton<SubAgentSignalRBridge>();
            services.AddHostedService(sp => sp.GetRequiredService<SubAgentSignalRBridge>());
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string parentSessionId = "parent-session-sub-agent";
        #pragma warning disable CS0618
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, parentSessionId, cts.Token);
        #pragma warning restore CS0618

        var receivedTcs = new TaskCompletionSource<SubAgentEventPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<SubAgentEventPayload>("SubAgentSpawned", payload => receivedTcs.TrySetResult(payload));

        var longTask = new string('x', 500);
        var subAgent = new SubAgentInfo
        {
            SubAgentId = "sub-1",
            ParentSessionId = SessionId.From(parentSessionId),
            ChildSessionId = SessionId.From($"{parentSessionId}::subagent::1"),
            Name = "researcher-one",
            Task = longTask,
            Archetype = SubAgentArchetype.General,
            Status = SubAgentStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };

        var activity = factory.Services.GetRequiredService<IActivityBroadcaster>();
        await activity.PublishAsync(new GatewayActivity
        {
            Type = GatewayActivityType.SubAgentSpawned,
            AgentId = TestAgentId,
            SessionId = parentSessionId,
            Message = "Sub-agent spawned",
            Data = new Dictionary<string, object?> { ["subAgent"] = subAgent }
        }, cts.Token);

        var payload = await receivedTcs.Task.WaitAsync(cts.Token);
        payload.SubAgentId.ShouldBe("sub-1");
        payload.Task.Length.ShouldBeLessThanOrEqualTo(
            121,
            "task summary must be truncated to a compact size before SignalR fan-out; #235 (120 chars + ellipsis)");
        payload.Task.ShouldEndWith("\u2026", Case.Sensitive, "truncation must use the ellipsis character");
    }

    // ── #264 — Rapid stream burst must deliver every event in order ────────

    /// <summary>
    /// Pins <see href="https://github.com/sytone/botnexus/issues/264">#264</see>: rapid
    /// tool-call sequences (especially from sub-agents) flood SignalR fan-out. Whether the
    /// implementation throttles, coalesces, or simply queues, every delta the adapter
    /// emits must reach subscribed clients in the same order with none dropped — otherwise
    /// the portal renders a corrupted assistant message.
    /// </summary>
    [Fact]
    public async Task RapidStreamDeltaBurst_AllEventsDeliveredInOrder_NoneDropped()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "burst-session";
        #pragma warning disable CS0618
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);
        #pragma warning restore CS0618

        const int burstCount = 25;
        var received = new List<string>(burstCount);
        var allReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            lock (received)
            {
                received.Add(payload.ContentDelta ?? string.Empty);
                if (received.Count >= burstCount)
                    allReceived.TrySetResult(true);
            }
        });

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        for (var i = 0; i < burstCount; i++)
        {
            await adapter.SendStreamEventAsync(sessionId, new AgentStreamEvent
            {
                Type = AgentStreamEventType.ContentDelta,
                ContentDelta = $"delta-{i:D2}"
            }, cts.Token);
        }

        await allReceived.Task.WaitAsync(cts.Token);

        lock (received)
        {
            received.Count.ShouldBe(burstCount, "every delta must be delivered (no drops); #264");
            for (var i = 0; i < burstCount; i++)
                received[i].ShouldBe($"delta-{i:D2}", $"delta #{i} must arrive in order");
        }
    }

    // ── #383 — Canvas persistence across reconnect (still open) ────────────

    /// <summary>
    /// Placeholder for <see href="https://github.com/sytone/botnexus/issues/383">#383</see>:
    /// canvas HTML is not persisted across reconnect and is invisible to a second browser.
    /// Tracked here so the scenario lights up automatically once the fix lands.
    /// </summary>
    [Fact(Skip = "Blocked by #383: canvas HTML is not persisted across reconnect.")]
    public Task Canvas_PersistsAcrossReconnect_AndIsVisibleToSecondBrowser()
        => Task.CompletedTask;

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> CreateTestFactory(Action<IServiceCollection>? configureServices = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureServices(services =>
                {
                    var hostedServices = services
                        .Where(d => d.ServiceType == typeof(IHostedService))
                        .ToList();
                    foreach (var descriptor in hostedServices)
                        services.Remove(descriptor);

                    services.RemoveAll<IAgentConfigurationWriter>();
                    services.AddSingleton<IAgentConfigurationWriter, NoOpAgentConfigurationWriter>();

                    services.AddSignalRChannelForTests();

                    services.Replace(ServiceDescriptor.Singleton<ISessionStore, InMemorySessionStore>());
                    services.Replace(ServiceDescriptor.Singleton<IConversationStore, InMemoryConversationStore>());

                    configureServices?.Invoke(services);
                });
            });

    private static async Task<HubConnection> CreateStartedConnection(WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        var server = factory.Server;
        var handler = server.CreateHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hub/gateway", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await connection.StartAsync(cancellationToken);
        return connection;
    }

    private static async Task RegisterAgentAsync(WebApplicationFactory<Program> factory, CancellationToken cancellationToken, string agentId = TestAgentId)
    {
        using var client = factory.CreateClient();
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From(agentId),
            DisplayName = $"Test Agent {agentId}",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };

        var response = await client.PostAsJsonAsync("/api/agents", descriptor, cancellationToken);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }

    private static CancellationTokenSource CreateTimeout()
        => new(TimeSpan.FromSeconds(15));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class RecordingDispatcher : IChannelDispatcher
    {
        public List<InboundMessage> Messages { get; } = [];

        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed record MuteByAddressCall(AgentId? AgentId, ChannelKey ChannelType, ChannelAddress ChannelAddress);

    /// <summary>
    /// Test router that records <see cref="MuteBindingByAddressAsync"/> calls and returns
    /// inert results for the methods exercised during connection setup. Sufficient for
    /// asserting <c>OnDisconnectedAsync</c>'s mute call shape (#130) without the full
    /// <c>DefaultConversationRouter</c> pipeline.
    /// </summary>
    private sealed class RecordingConversationRouter : IConversationRouter
    {
        public List<MuteByAddressCall> MuteByAddressCalls { get; } = [];

        public Task<ConversationRoutingResult> ResolveInboundAsync(
            AgentId agentId,
            ChannelKey channelType,
            ChannelAddress channelAddress,
            string? conversationId = null,
            CancellationToken ct = default,
            CitizenId? initiator = null)
            => Task.FromResult(new ConversationRoutingResult(
                new Conversation
                {
                    ConversationId = ConversationId.From($"recording:{agentId.Value}"),
                    AgentId = agentId,
                    Title = "recording"
                },
                SessionId.From($"recording-session:{agentId.Value}"),
                IsNewSession: true,
                OriginatingBinding: null));

        public Task<IReadOnlyList<ChannelBinding>> GetOutboundBindingsAsync(
            SessionId sessionId,
            BindingId? originatingBindingId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChannelBinding>>([]);

        public Task ReattachBindingAsync(BindingId bindingId, ConversationId targetConversationId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MuteBindingAsync(ConversationId conversationId, BindingId bindingId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MuteBindingByAddressAsync(AgentId? agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
        {
            MuteByAddressCalls.Add(new MuteByAddressCall(agentId, channelType, channelAddress));
            return Task.CompletedTask;
        }
    }
}
