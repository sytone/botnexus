using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Channels.SignalR;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class SignalRIntegrationTests : IAsyncDisposable
{
    private const string TestAgentId = "test-agent";

    [Fact]
    public async Task Hub_OnConnect_ReceivesConnectedMessage()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = CreateHubConnection(factory);
        var connectedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<JsonElement>("Connected", payload => connectedTcs.TrySetResult(payload));

        await connection.StartAsync(cts.Token);
        var connected = await connectedTcs.Task.WaitAsync(cts.Token);

        connected.GetProperty("connectionId").GetString().Should().Be(connection.ConnectionId);
        connected.GetProperty("agents").EnumerateArray()
            .Select(agent => agent.GetProperty("agentId").GetString())
            .Should().Contain(TestAgentId);
    }

    [Fact]
    public async Task Hub_SubscribeAll_ReturnsSessionManifest()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("manifest-1"),
            AgentId = TestAgentId,
            Status = GatewaySessionStatus.Active,
            ChannelType = ChannelKey.From("signalr")
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var result = await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);
        var sessions = result.GetProperty("sessions").EnumerateArray().ToList();

        sessions.Should().ContainSingle(s => s.GetProperty("sessionId").GetString() == "manifest-1");
    }

    [Fact]
    public async Task Hub_SubscribeAll_JoinsAllGroups()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionA = "subscribe-all-a";
        const string sessionB = "subscribe-all-b";
        await SeedSessionAsync(factory, new GatewaySession { SessionId = sessionA, AgentId = TestAgentId, Status = GatewaySessionStatus.Active }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession { SessionId = sessionB, AgentId = TestAgentId, Status = GatewaySessionStatus.Active }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        var eventA = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventB = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.ContentDelta == "all-a")
                eventA.TrySetResult(payload);
            if (payload.ContentDelta == "all-b")
                eventB.TrySetResult(payload);
        });

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(sessionA, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "all-a" }, cts.Token);
        await adapter.SendStreamEventAsync(sessionB, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "all-b" }, cts.Token);

        (await eventA.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("all-a");
        (await eventB.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("all-b");
    }

    [Fact]
    public async Task Hub_SubscribeAll_JoinsVisibleGroups()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionTarget = "subscribe-one-target";
        const string sessionOther = "subscribe-one-other";
        await SeedSessionAsync(factory, new GatewaySession { SessionId = sessionTarget, AgentId = TestAgentId, Status = GatewaySessionStatus.Active }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession { SessionId = sessionOther, AgentId = TestAgentId, Status = GatewaySessionStatus.Active }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var subscribed = await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);
        subscribed.GetProperty("sessions").EnumerateArray().Should()
            .Contain(item => item.GetProperty("sessionId").GetString() == sessionTarget)
            .And.Contain(item => item.GetProperty("sessionId").GetString() == sessionOther);

        var targetReceived = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherReceived = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.ContentDelta == "target-only")
                targetReceived.TrySetResult(payload);
            if (payload.ContentDelta == "other-should-not-arrive")
                otherReceived.TrySetResult(payload);
        });

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(sessionTarget, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "target-only" }, cts.Token);
        await adapter.SendStreamEventAsync(sessionOther, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "other-should-not-arrive" }, cts.Token);

        (await targetReceived.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("target-only");
        (await otherReceived.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("other-should-not-arrive");
    }

    [Fact]
    public async Task Hub_Connected_IncludesMultiSessionCapability()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = CreateHubConnection(factory);
        var connectedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<JsonElement>("Connected", payload => connectedTcs.TrySetResult(payload));

        await connection.StartAsync(cts.Token);
        var connected = await connectedTcs.Task.WaitAsync(cts.Token);

        connected.GetProperty("capabilities").GetProperty("multiSession").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Hub_SubscribeAll_WithExistingJoinSession_BothWork()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string joinedSession = "legacy-join-session";
        const string subscribedSession = "legacy-subscribe-session";
        await SeedSessionAsync(factory, new GatewaySession { SessionId = subscribedSession, AgentId = TestAgentId, Status = GatewaySessionStatus.Active }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var joinResult = await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, joinedSession, cts.Token);
        joinResult.GetProperty("sessionId").GetString().Should().Be(joinedSession);

        var subscribeAllResult = await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);
        subscribeAllResult.GetProperty("sessions").EnumerateArray()
            .Should().Contain(item => item.GetProperty("sessionId").GetString() == subscribedSession);

        var joinedTcs = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribedTcs = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.ContentDelta == "legacy-join-event")
                joinedTcs.TrySetResult(payload);
            if (payload.ContentDelta == "subscribe-all-event")
                subscribedTcs.TrySetResult(payload);
        });

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(joinedSession, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "legacy-join-event" }, cts.Token);
        await adapter.SendStreamEventAsync(subscribedSession, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "subscribe-all-event" }, cts.Token);

        (await joinedTcs.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("legacy-join-event");
        (await subscribedTcs.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("subscribe-all-event");
    }

    [Fact]
    public async Task Hub_JoinSession_ReturnsSessionData()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "session-join-1";
        var result = await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);

        result.GetProperty("sessionId").GetString().Should().Be(sessionId);
        result.GetProperty("agentId").GetString().Should().Be(TestAgentId);
        result.GetProperty("messageCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Hub_JoinSession_WithNullSessionId_CreatesNewSession()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var result = await connection.InvokeCoreAsync<JsonElement>("JoinSession", [TestAgentId, null], cts.Token);

        result.GetProperty("sessionId").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("agentId").GetString().Should().Be(TestAgentId);
    }

    [Fact]
    public async Task Hub_JoinSession_WithExistingSessionId_JoinsExistingSession()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "existing-session";
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);
        var secondJoin = await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);

        secondJoin.GetProperty("sessionId").GetString().Should().Be(sessionId);
        secondJoin.GetProperty("messageCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Hub_SendMessage_DispatchesToGateway()
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
        var result = await connection.InvokeAsync<JsonElement>("SendMessage", TestAgentId, "signalr", "hello", cts.Token);
        var sessionId = result.GetProperty("sessionId").GetString();
        sessionId.Should().NotBeNullOrWhiteSpace();

        dispatcher.Messages.Should().ContainSingle()
            .Which.Should().Match<InboundMessage>(m =>
                m.TargetAgentId == TestAgentId &&
                m.SessionId == sessionId &&
                m.Content == "hello" &&
                Equals(m.Metadata["messageType"], "message"));
    }

    [Fact]
    public async Task Hub_SendMessage_WithNullSessionId_Fails()
    {
        var dispatcher = new RecordingDispatcher(failOnNullSessionId: true);
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var act = async () => await connection.InvokeCoreAsync("SendMessage", [TestAgentId, null, "no-session"], cts.Token);

        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task Hub_SwitchSession_JoinNewAfterLeavingOld()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionA = "switch-a";
        const string sessionB = "switch-b";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionA,
            AgentId = TestAgentId,
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionB,
            AgentId = TestAgentId,
            ChannelType = ChannelKey.From("telegram"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SendMessage", TestAgentId, "telegram", "latest", cts.Token);

        dispatcher.Messages.Should().ContainSingle();
        dispatcher.Messages[0].SessionId.Should().Be(sessionB);
    }

    [Fact]
    public async Task Hub_RapidSessionSwitch_LatestWins()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionA = "rapid-a";
        const string sessionB = "rapid-b";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionA,
            AgentId = TestAgentId,
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionB,
            AgentId = TestAgentId,
            ChannelType = ChannelKey.From("telegram"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await Task.WhenAll(
            connection.InvokeAsync("SendMessage", TestAgentId, "signalr", "before-switch", cts.Token),
            connection.InvokeAsync("SendMessage", TestAgentId, "telegram", "after-switch", cts.Token));

        dispatcher.Messages.Should().ContainSingle(m => m.SessionId == sessionB && m.Content == "after-switch");
    }

    [Fact]
    public async Task Hub_SessionSwitch_SendDuringActiveJoin_UsesJoinedSession()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionA = "active-join-a";
        const string sessionB = "active-join-b";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionA,
            AgentId = TestAgentId,
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionB,
            AgentId = TestAgentId,
            ChannelType = ChannelKey.From("telegram"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SendMessage", TestAgentId, "telegram", "send-during-join", cts.Token);

        dispatcher.Messages.Should().ContainSingle();
        dispatcher.Messages[0].SessionId.Should().Be(sessionB);
        dispatcher.Messages[0].Content.Should().Be("send-during-join");
    }

    [Fact]
    public async Task Hub_SessionSwitch_LeaveJoinImmediateSend_RoutesToNewSession()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionA = "leave-join-a";
        const string sessionB = "leave-join-b";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionA,
            AgentId = TestAgentId,
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionB,
            AgentId = TestAgentId,
            ChannelType = ChannelKey.From("telegram"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SendMessage", TestAgentId, "telegram", "immediate-after-join", cts.Token);

        dispatcher.Messages.Should().ContainSingle();
        dispatcher.Messages[0].SessionId.Should().Be(sessionB);
        dispatcher.Messages[0].Content.Should().Be("immediate-after-join");
    }

    [Fact]
    public async Task Hub_SessionSwitch_MultipleAgentsInterleaved_SendRoutesByAgentAndSession()
    {
        const string agentA = "agent-a";
        const string agentB = "agent-b";
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token, agentA);
        await RegisterAgentAsync(factory, cts.Token, agentB);

        const string sessionA = "interleaved-session-1";
        const string sessionB = "interleaved-session-2";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionA,
            AgentId = agentA,
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionB,
            AgentId = agentB,
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SendMessage", agentA, "signalr", "message-for-a", cts.Token);
        await connection.InvokeAsync("SendMessage", agentB, "signalr", "message-for-b", cts.Token);

        dispatcher.Messages.Should().HaveCount(2);
        dispatcher.Messages.Should().ContainSingle(m =>
            m.TargetAgentId == agentA &&
            m.SessionId == sessionA &&
            m.Content == "message-for-a");
        dispatcher.Messages.Should().ContainSingle(m =>
            m.TargetAgentId == agentB &&
            m.SessionId == sessionB &&
            m.Content == "message-for-b");
    }

    [Fact]
    public async Task Hub_MultipleAgentsIsolation_JoinThenSend_RoutesToJoinedSessions()
    {
        const string agentA = "agent-a";
        const string agentB = "agent-b";
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token, agentA);
        await RegisterAgentAsync(factory, cts.Token, agentB);

        const string sessionA = "multi-agent-join-a";
        const string sessionB = "multi-agent-join-b";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionA,
            AgentId = agentA,
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionB,
            AgentId = agentB,
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("JoinSession", agentA, sessionA, cts.Token);
        await connection.InvokeAsync<JsonElement>("JoinSession", agentB, sessionB, cts.Token);
        await connection.InvokeAsync("SendMessage", agentA, "signalr", "message-for-a", cts.Token);
        await connection.InvokeAsync("SendMessage", agentB, "signalr", "message-for-b", cts.Token);

        dispatcher.Messages.Should().HaveCount(2);
        dispatcher.Messages.Should().ContainSingle(m =>
            m.TargetAgentId == agentA &&
            m.SessionId == sessionA &&
            m.Content == "message-for-a");
        dispatcher.Messages.Should().ContainSingle(m =>
            m.TargetAgentId == agentB &&
            m.SessionId == sessionB &&
            m.Content == "message-for-b");
    }

    [Fact]
    public async Task Hub_SendMessage_AfterLeaveSession_HandlesStaleSessionGracefully()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionA = "stale-session-a";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = sessionA,
            AgentId = TestAgentId,
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionA, cts.Token);
        await connection.InvokeAsync("LeaveSession", sessionA, cts.Token);

        var result = await connection.InvokeAsync<JsonElement>("SendMessage", TestAgentId, "signalr", "after-leave", cts.Token);

        result.GetProperty("sessionId").GetString().Should().NotBeNullOrWhiteSpace();
        dispatcher.Messages.Should().ContainSingle();
        dispatcher.Messages[0].Content.Should().Be("after-leave");
        dispatcher.Messages[0].SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Hub_SessionSwitch_ConcurrentClientsDifferentSessions_ReceiveOnlyOwnEvents()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connectionA = await CreateStartedConnection(factory, cts.Token);
        await using var connectionB = await CreateStartedConnection(factory, cts.Token);
        const string sessionA = "concurrent-client-a";
        const string sessionB = "concurrent-client-b";
        await connectionA.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionA, cts.Token);
        await connectionB.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionB, cts.Token);

        var receivedA = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedB = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var crossReceivedA = false;
        var crossReceivedB = false;

        using var _ = connectionA.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.ContentDelta == "event-a")
                receivedA.TrySetResult(payload);
            if (payload.ContentDelta == "event-b")
                crossReceivedA = true;
        });
        using var __ = connectionB.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.ContentDelta == "event-b")
                receivedB.TrySetResult(payload);
            if (payload.ContentDelta == "event-a")
                crossReceivedB = true;
        });

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(sessionA, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "event-a"
        }, cts.Token);
        await adapter.SendStreamEventAsync(sessionB, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "event-b"
        }, cts.Token);

        (await receivedA.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("event-a");
        (await receivedB.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("event-b");
        await Task.Delay(250, cts.Token);
        crossReceivedA.Should().BeFalse();
        crossReceivedB.Should().BeFalse();
    }

    [Fact]
    public async Task Hub_MultipleClients_SameSession_BothReceiveMessages()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connectionA = await CreateStartedConnection(factory, cts.Token);
        await using var connectionB = await CreateStartedConnection(factory, cts.Token);

        const string sessionId = "shared-session";
        await connectionA.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);
        await connectionB.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);

        var receivedA = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedB = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connectionA.On<AgentStreamEvent>("ContentDelta", payload => receivedA.TrySetResult(payload));
        using var __ = connectionB.On<AgentStreamEvent>("ContentDelta", payload => receivedB.TrySetResult(payload));

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(sessionId, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "group-message"
        }, cts.Token);

        (await receivedA.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("group-message");
        (await receivedB.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("group-message");
    }

    [Fact]
    public async Task Hub_Steer_DispatchesWithControlMetadata()
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
        const string sessionId = "steer-session";
        await connection.InvokeAsync("Steer", TestAgentId, sessionId, "course correction", cts.Token);

        dispatcher.Messages.Should().ContainSingle();
        dispatcher.Messages[0].Metadata["messageType"].Should().Be("steer");
        dispatcher.Messages[0].Metadata["control"].Should().Be("steer");
    }

    [Fact]
    public async Task Hub_FollowUp_DispatchesThroughGateway()
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
        const string sessionId = "followup-session";
        await connection.InvokeAsync("FollowUp", TestAgentId, sessionId, "next step", cts.Token);

        dispatcher.Messages.Should().ContainSingle();
        dispatcher.Messages[0].Content.Should().Be("next step");
        dispatcher.Messages[0].Metadata["messageType"].Should().Be("message");
    }

    [Fact]
    public async Task Hub_ResetSession_DeletesSessionAndNotifiesClient()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "reset-session";
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);

        var resetTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<JsonElement>("SessionReset", payload => resetTcs.TrySetResult(payload));
        await connection.InvokeAsync("ResetSession", TestAgentId, sessionId, cts.Token);

        var resetPayload = await resetTcs.Task.WaitAsync(cts.Token);
        resetPayload.GetProperty("sessionId").GetString().Should().Be(sessionId);
        resetPayload.GetProperty("agentId").GetString().Should().Be(TestAgentId);

        var store = factory.Services.GetRequiredService<ISessionStore>();
        var session = await store.GetAsync(sessionId, cts.Token);
        session.Should().BeNull();
    }

    [Fact]
    public async Task Hub_Abort_StopsAgentInstance()
    {
        var supervisor = new AbortAwareSupervisor(TestAgentId, "abort-session");
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IAgentSupervisor>();
            services.AddSingleton<IAgentSupervisor>(supervisor);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("Abort", TestAgentId, "abort-session", cts.Token);

        supervisor.Handle.AbortCalled.Should().BeTrue();
        supervisor.GetOrCreateCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Hub_ChannelAdapter_SendsContentDeltaToSessionGroup()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "adapter-delta";
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);

        var deltaTcs = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload => deltaTcs.TrySetResult(payload));

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(sessionId, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "delta-text"
        }, cts.Token);

        var payload = await deltaTcs.Task.WaitAsync(cts.Token);
        payload.Type.Should().Be(AgentStreamEventType.ContentDelta);
        payload.ContentDelta.Should().Be("delta-text");
    }

    [Fact]
    public async Task Hub_ChannelAdapter_SendsToCorrectGroup()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connectionA = await CreateStartedConnection(factory, cts.Token);
        await using var connectionB = await CreateStartedConnection(factory, cts.Token);
        const string sessionA = "group-a";
        const string sessionB = "group-b";
        await connectionA.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionA, cts.Token);
        await connectionB.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionB, cts.Token);

        var aTcs = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReceived = false;
        using var _ = connectionA.On<AgentStreamEvent>("ContentDelta", payload => aTcs.TrySetResult(payload));
        using var __ = connectionB.On<AgentStreamEvent>("ContentDelta", _ => bReceived = true);

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(sessionA, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "session-a-only"
        }, cts.Token);

        (await aTcs.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("session-a-only");
        await Task.Delay(250, cts.Token);
        bReceived.Should().BeFalse();
    }

    [Fact]
    public async Task Hub_ChannelAdapter_AllEventTypes()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "all-events";
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);

        var handlers = new Dictionary<string, TaskCompletionSource<AgentStreamEvent>>(StringComparer.Ordinal)
        {
            ["MessageStart"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["ContentDelta"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["ThinkingDelta"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["ToolStart"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["ToolEnd"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["MessageEnd"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["Error"] = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        var subscriptions = new List<IDisposable>
        {
            connection.On<AgentStreamEvent>("MessageStart", payload => handlers["MessageStart"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("ContentDelta", payload => handlers["ContentDelta"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("ThinkingDelta", payload => handlers["ThinkingDelta"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("ToolStart", payload => handlers["ToolStart"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("ToolEnd", payload => handlers["ToolEnd"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("MessageEnd", payload => handlers["MessageEnd"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("Error", payload => handlers["Error"].TrySetResult(payload))
        };

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        foreach (var type in Enum.GetValues<AgentStreamEventType>())
        {
            await adapter.SendStreamEventAsync(sessionId, new AgentStreamEvent
            {
                Type = type,
                ContentDelta = type == AgentStreamEventType.ContentDelta ? "delta" : null,
                ThinkingContent = type == AgentStreamEventType.ThinkingDelta ? "thinking" : null,
                ToolCallId = type is AgentStreamEventType.ToolStart or AgentStreamEventType.ToolEnd ? "tool-1" : null,
                ToolName = type is AgentStreamEventType.ToolStart or AgentStreamEventType.ToolEnd ? "search" : null,
                ToolResult = type == AgentStreamEventType.ToolEnd ? "done" : null,
                ToolIsError = type == AgentStreamEventType.ToolEnd ? false : null,
                ErrorMessage = type == AgentStreamEventType.Error ? "boom" : null
            }, cts.Token);
        }

        foreach (var expected in Enum.GetValues<AgentStreamEventType>())
        {
            var method = expected switch
            {
                AgentStreamEventType.MessageStart => "MessageStart",
                AgentStreamEventType.ContentDelta => "ContentDelta",
                AgentStreamEventType.ThinkingDelta => "ThinkingDelta",
                AgentStreamEventType.ToolStart => "ToolStart",
                AgentStreamEventType.ToolEnd => "ToolEnd",
                AgentStreamEventType.MessageEnd => "MessageEnd",
                AgentStreamEventType.Error => "Error",
                _ => throw new ArgumentOutOfRangeException()
            };

            var payload = await handlers[method].Task.WaitAsync(cts.Token);
            payload.Type.Should().Be(expected);
        }

        foreach (var subscription in subscriptions)
            subscription.Dispose();
    }

    [Fact]
    public async Task Hub_GetAgents_ReturnsRegisteredAgents()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var agents = await connection.InvokeAsync<JsonElement>("GetAgents", cts.Token);

        agents.ValueKind.Should().Be(JsonValueKind.Array);
        agents.EnumerateArray()
            .Select(agent => agent.GetProperty("agentId").GetString())
            .Should().Contain(TestAgentId);
    }

    [Fact]
    public async Task Hub_GetAgentStatus_ReturnsNullForUnknownSession()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var status = await connection.InvokeAsync<object?>("GetAgentStatus", TestAgentId, "unknown-session", cts.Token);

        status.Should().BeNull();
    }

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

                    configureServices?.Invoke(services);
                });
            });

    private static HubConnection CreateHubConnection(WebApplicationFactory<Program> factory)
    {
        var server = factory.Server;
        var handler = server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/hub/gateway", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    private static async Task<HubConnection> CreateStartedConnection(WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        var connection = CreateHubConnection(factory);
        await connection.StartAsync(CancellationToken.None);
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

        var response = await client.PostAsJsonAsync("/api/agents", descriptor, CancellationToken.None);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }

    private static async Task SeedSessionAsync(WebApplicationFactory<Program> factory, GatewaySession session, CancellationToken cancellationToken)
    {
        var store = factory.Services.GetRequiredService<ISessionStore>();
        await store.SaveAsync(session, cancellationToken);
    }

    private static CancellationTokenSource CreateTimeout()
        => new(TimeSpan.FromSeconds(15));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class RecordingDispatcher(bool failOnNullSessionId = false) : IChannelDispatcher
    {
        public List<InboundMessage> Messages { get; } = [];

        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            if (failOnNullSessionId && string.IsNullOrWhiteSpace(message.SessionId))
                throw new InvalidOperationException("sessionId is required.");

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class AbortAwareSupervisor(string agentId, string sessionId) : IAgentSupervisor
    {
        private readonly AgentInstance _instance = new()
        {
            AgentId = AgentId.From(agentId),
            SessionId = SessionId.From(sessionId),
            InstanceId = $"{agentId}::{sessionId}",
            IsolationStrategy = "in-process",
            Status = AgentInstanceStatus.Running
        };

        public AbortAwareHandle Handle { get; } = new(agentId, sessionId);
        public bool GetOrCreateCalled { get; private set; }

        public Task<IAgentHandle> GetOrCreateAsync(AgentId requestedAgentId, SessionId requestedSessionId, CancellationToken cancellationToken = default)
        {
            GetOrCreateCalled = true;
            requestedAgentId.Should().Be(agentId);
            requestedSessionId.Should().Be(sessionId);
            return Task.FromResult<IAgentHandle>(Handle);
        }

        public Task StopAsync(AgentId requestedAgentId, SessionId requestedSessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public AgentInstance? GetInstance(AgentId requestedAgentId, SessionId requestedSessionId)
            => requestedAgentId == agentId && requestedSessionId == sessionId ? _instance : null;

        public IReadOnlyList<AgentInstance> GetAllInstances() => [_instance];

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AbortAwareHandle(AgentId agentId, SessionId sessionId) : IAgentHandle
    {
        public BotNexus.Domain.Primitives.AgentId AgentId { get; } = agentId;
        public BotNexus.Domain.Primitives.SessionId SessionId { get; } = sessionId;
        public bool IsRunning => true;
        public bool AbortCalled { get; private set; }

        public Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Content = string.Empty });

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task AbortAsync(CancellationToken cancellationToken = default)
        {
            AbortCalled = true;
            return Task.CompletedTask;
        }

        public Task SteerAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FollowUpAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FollowUpAsync(AgentMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

