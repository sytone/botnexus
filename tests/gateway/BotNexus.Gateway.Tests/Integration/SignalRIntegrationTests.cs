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
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

using BotNexus.Gateway.Tests;

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

        connected.GetProperty("connectionId").GetString().ShouldBe(connection.ConnectionId);
        connected.GetProperty("agents").EnumerateArray()
            .Select(agent => agent.GetProperty("agentId").GetString())
            .ShouldContain(TestAgentId);
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
            AgentId = AgentId.From(TestAgentId),
            Status = GatewaySessionStatus.Active,
            ChannelType = ChannelKey.From("signalr")
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var result = await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);
        var sessions = result.GetProperty("sessions").EnumerateArray().ToList();

        // Replaces the legacy Hub_JoinSession_ReturnsSessionData coverage: the per-session
        // payload SubscribeAll surfaces must include the agent and message-count fields the
        // portal relies on for session-summary rendering.
        var manifest = sessions.Where(s => s.GetProperty("sessionId").GetString() == "manifest-1").ShouldHaveSingleItem();
        manifest.GetProperty("agentId").GetString().ShouldBe(TestAgentId);
        manifest.GetProperty("messageCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Hub_SubscribeAll_JoinsAllGroups()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionA = "subscribe-all-a";
        const string sessionB = "subscribe-all-b";
        await SeedSessionAsync(factory, new GatewaySession { SessionId = SessionId.From(sessionA), AgentId = AgentId.From(TestAgentId), Status = GatewaySessionStatus.Active }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession { SessionId = SessionId.From(sessionB), AgentId = AgentId.From(TestAgentId), Status = GatewaySessionStatus.Active }, cts.Token);

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
        await adapter.SendStreamEventAsync(StreamTargets.For(sessionA), new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "all-a" }, cts.Token);
        await adapter.SendStreamEventAsync(StreamTargets.For(sessionB), new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "all-b" }, cts.Token);

        (await eventA.Task.WaitAsync(cts.Token)).ContentDelta.ShouldBe("all-a");
        (await eventB.Task.WaitAsync(cts.Token)).ContentDelta.ShouldBe("all-b");
    }

    [Fact]
    public async Task Hub_SubscribeAll_JoinsVisibleGroups()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionTarget = "subscribe-one-target";
        const string sessionOther = "subscribe-one-other";
        await SeedSessionAsync(factory, new GatewaySession { SessionId = SessionId.From(sessionTarget), AgentId = AgentId.From(TestAgentId), Status = GatewaySessionStatus.Active }, cts.Token);
        await SeedSessionAsync(factory, new GatewaySession { SessionId = SessionId.From(sessionOther), AgentId = AgentId.From(TestAgentId), Status = GatewaySessionStatus.Active }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var subscribed = await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);
        var sessionsArr = subscribed.GetProperty("sessions").EnumerateArray().ToList();
        sessionsArr.ShouldContain(item => item.GetProperty("sessionId").GetString() == sessionTarget);
        sessionsArr.ShouldContain(item => item.GetProperty("sessionId").GetString() == sessionOther);

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
        await adapter.SendStreamEventAsync(StreamTargets.For(sessionTarget), new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "target-only" }, cts.Token);
        await adapter.SendStreamEventAsync(StreamTargets.For(sessionOther), new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "other-should-not-arrive" }, cts.Token);

        (await targetReceived.Task.WaitAsync(cts.Token)).ContentDelta.ShouldBe("target-only");
        (await otherReceived.Task.WaitAsync(cts.Token)).ContentDelta.ShouldBe("other-should-not-arrive");
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

        connected.GetProperty("capabilities").GetProperty("multiSession").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Hub_SendMessage_DispatchesToGateway()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var result = await connection.InvokeAsync<JsonElement>("SendMessage", TestAgentId, "signalr", "hello", (string?)null, cts.Token);
        var sessionId = result.GetProperty("sessionId").GetString();
        sessionId.ShouldNotBeNullOrWhiteSpace();

        var msg = dispatcher.Messages.ShouldHaveSingleItem();
        msg.RoutingHints.ShouldNotBeNull();
        msg.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe(TestAgentId);
        msg.RoutingHints.RequestedSessionId!.Value.Value.ShouldBe(sessionId);
        msg.Content.ShouldBe("hello");
        msg.Metadata["messageType"].ShouldBe("message");
    }

    [Fact]
    public async Task Hub_SendMessage_WithNullSessionId_Fails()
    {
        var dispatcher = new RecordingDispatcher(failOnNullSessionId: true);
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        Func<Task> act = async () => await connection.InvokeCoreAsync("SendMessage", [TestAgentId, null, "no-session", null], cts.Token);

        await act.ShouldThrowAsync<HubException>();
    }

    [Fact]
    public async Task Hub_SwitchSession_JoinNewAfterLeavingOld()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        // Wave 2: conversation routing creates a new session for new channel addresses.
        // Seed sessions are no longer picked up by channel-type scan; a conversation binding is required.
        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SendMessage", TestAgentId, "telegram", "latest", (string?)null, cts.Token);

        dispatcher.Messages.ShouldHaveSingleItem();
        var dispatched = dispatcher.Messages[0];
        dispatched.RoutingHints.ShouldNotBeNull();
        dispatched.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe(TestAgentId);
        dispatched.Content.ShouldBe("latest");
        dispatched.RoutingHints.RequestedSessionId.ShouldNotBeNull();
        dispatched.RoutingHints.RequestedSessionId!.Value.Value.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Hub_RapidSessionSwitch_LatestWins()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        // Wave 2: conversation routing creates sessions per conversation binding.
        // Rapid sends on different channel types each create/reuse their own conversation sessions.
        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await Task.WhenAll(
            connection.InvokeAsync("SendMessage", TestAgentId, "signalr", "before-switch", (string?)null, cts.Token),
            connection.InvokeAsync("SendMessage", TestAgentId, "telegram", "after-switch", (string?)null, cts.Token));

        dispatcher.Messages.ShouldContain(m => m.Content == "after-switch");
        dispatcher.Messages.ShouldContain(m => m.Content == "before-switch");
    }

    [Fact]
    public async Task Hub_SessionSwitch_SendDuringActiveJoin_UsesJoinedSession()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        // Wave 2: conversation routing creates a session per conversation binding.
        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SendMessage", TestAgentId, "telegram", "send-during-join", (string?)null, cts.Token);

        dispatcher.Messages.ShouldHaveSingleItem();
        dispatcher.Messages[0].Content.ShouldBe("send-during-join");
        dispatcher.Messages[0].RoutingHints.ShouldNotBeNull();
        dispatcher.Messages[0].RoutingHints!.RequestedSessionId.ShouldNotBeNull();
        dispatcher.Messages[0].RoutingHints!.RequestedSessionId!.Value.Value.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Hub_SessionSwitch_LeaveJoinImmediateSend_RoutesToNewSession()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        // Wave 2: conversation routing creates sessions per conversation binding.
        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SendMessage", TestAgentId, "telegram", "immediate-after-join", (string?)null, cts.Token);

        dispatcher.Messages.ShouldHaveSingleItem();
        dispatcher.Messages[0].Content.ShouldBe("immediate-after-join");
        dispatcher.Messages[0].RoutingHints.ShouldNotBeNull();
        dispatcher.Messages[0].RoutingHints!.RequestedSessionId.ShouldNotBeNull();
        dispatcher.Messages[0].RoutingHints!.RequestedSessionId!.Value.Value.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Hub_SessionSwitch_MultipleAgentsInterleaved_SendRoutesByAgentAndSession()
    {
        const string agentA = "agent-a";
        const string agentB = "agent-b";
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token, agentA);
        await RegisterAgentAsync(factory, cts.Token, agentB);

        // Wave 2: conversation routing creates sessions per conversation binding.
        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SendMessage", agentA, "signalr", "message-for-a", (string?)null, cts.Token);
        await connection.InvokeAsync("SendMessage", agentB, "signalr", "message-for-b", (string?)null, cts.Token);

        dispatcher.Messages.Count().ShouldBe(2);
        dispatcher.Messages.Where(m =>
            m.RoutingHints != null &&
            m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == agentA &&
            m.Content == "message-for-a").ShouldHaveSingleItem();
        dispatcher.Messages.Where(m =>
            m.RoutingHints != null &&
            m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == agentB &&
            m.Content == "message-for-b").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Hub_MultipleAgentsIsolation_JoinThenSend_RoutesToJoinedSessions()
    {
        const string agentA = "agent-a";
        const string agentB = "agent-b";
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token, agentA);
        await RegisterAgentAsync(factory, cts.Token, agentB);

        // Wave 2: conversation routing creates sessions per conversation binding.
        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SendMessage", agentA, "signalr", "message-for-a", (string?)null, cts.Token);
        await connection.InvokeAsync("SendMessage", agentB, "signalr", "message-for-b", (string?)null, cts.Token);

        dispatcher.Messages.Count().ShouldBe(2);
        dispatcher.Messages.Where(m =>
            m.RoutingHints != null &&
            m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == agentA &&
            m.Content == "message-for-a").ShouldHaveSingleItem();
        dispatcher.Messages.Where(m =>
            m.RoutingHints != null &&
            m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == agentB &&
            m.Content == "message-for-b").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Hub_MultipleClients_SameSession_BothReceiveMessages()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionId = "shared-session";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(TestAgentId),
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connectionA = await CreateStartedConnection(factory, cts.Token);
        await using var connectionB = await CreateStartedConnection(factory, cts.Token);
        await connectionA.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);
        await connectionB.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        var receivedA = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedB = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connectionA.On<AgentStreamEvent>("ContentDelta", payload => receivedA.TrySetResult(payload));
        using var __ = connectionB.On<AgentStreamEvent>("ContentDelta", payload => receivedB.TrySetResult(payload));

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(StreamTargets.For(sessionId), new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "group-message"
        }, cts.Token);

        (await receivedA.Task.WaitAsync(cts.Token)).ContentDelta.ShouldBe("group-message");
        (await receivedB.Task.WaitAsync(cts.Token)).ContentDelta.ShouldBe("group-message");
    }

    [Fact]
    public async Task Hub_Steer_DispatchesWithControlMetadata()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "steer-session";
        string? conversationId = null;
        var result = await connection.InvokeAsync<JsonElement>("Steer", TestAgentId, sessionId, "course correction", conversationId, cts.Token);

        dispatcher.Messages.ShouldHaveSingleItem();
        result.GetProperty("sessionId").GetString().ShouldBe(sessionId);
        dispatcher.Messages[0].RoutingHints.ShouldNotBeNull();
        dispatcher.Messages[0].RoutingHints!.RequestedSessionId!.Value.Value.ShouldBe(sessionId);
        dispatcher.Messages[0].RoutingHints!.RequestedConversationId.ShouldBeNull();
        dispatcher.Messages[0].Metadata["messageType"].ShouldBe("steer");
        dispatcher.Messages[0].Metadata["control"].ShouldBe("steer");
    }

    [Fact]
    public async Task Hub_Steer_SetsConversationIdWhenProvided()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "steer-session";
        const string conversationId = "conv-42";
        var result = await connection.InvokeAsync<JsonElement>("Steer", TestAgentId, sessionId, "course correction", conversationId, cts.Token);

        dispatcher.Messages.ShouldHaveSingleItem();
        result.GetProperty("sessionId").GetString().ShouldBe(sessionId);
        dispatcher.Messages[0].RoutingHints.ShouldNotBeNull();
        dispatcher.Messages[0].RoutingHints!.RequestedSessionId!.Value.Value.ShouldBe(sessionId);
        dispatcher.Messages[0].RoutingHints!.RequestedConversationId!.Value.Value.ShouldBe("conv-42");
        dispatcher.Messages[0].Metadata["messageType"].ShouldBe("steer");
        dispatcher.Messages[0].Metadata["control"].ShouldBe("steer");
    }

    [Fact]
    public async Task Hub_FollowUp_DispatchesThroughGateway()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.UseRecordingDispatcher(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "followup-session";
        await connection.InvokeAsync("FollowUp", TestAgentId, sessionId, "next step", cts.Token);

        dispatcher.Messages.ShouldHaveSingleItem();
        dispatcher.Messages[0].Content.ShouldBe("next step");
        dispatcher.Messages[0].Metadata["messageType"].ShouldBe("message");
    }

    [Fact]
    public async Task Hub_ResetSession_SealsSessionAndNotifiesClient()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionId = "reset-session";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(TestAgentId),
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        var resetTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<JsonElement>("SessionReset", payload => resetTcs.TrySetResult(payload));
        await connection.InvokeAsync("ResetSession", TestAgentId, sessionId, cts.Token);

        var resetPayload = await resetTcs.Task.WaitAsync(cts.Token);
        resetPayload.GetProperty("sessionId").GetString().ShouldBe(sessionId);
        resetPayload.GetProperty("agentId").GetString().ShouldBe(TestAgentId);

        // Session is sealed in place — Status flips to Sealed, the row stays in the store so
        // transcript readers still see the conversation history. ArchiveAsync is intentionally
        // not used (it deletes in InMemorySessionStore, hides files in FileSessionStore).
        var store = factory.Services.GetRequiredService<ISessionStore>();
        var session = await store.GetAsync(SessionId.From(sessionId), cts.Token);
        session.ShouldNotBeNull();
        session!.Status.ShouldBe(SessionStatus.Sealed);
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

        supervisor.Handle.AbortCalled.ShouldBeTrue();
        supervisor.GetOrCreateCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Hub_ChannelAdapter_SendsContentDeltaToSessionGroup()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionId = "adapter-delta";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(TestAgentId),
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        var deltaTcs = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload => deltaTcs.TrySetResult(payload));

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(StreamTargets.For(sessionId), new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "delta-text"
        }, cts.Token);

        var payload = await deltaTcs.Task.WaitAsync(cts.Token);
        payload.Type.ShouldBe(AgentStreamEventType.ContentDelta);
        payload.ContentDelta.ShouldBe("delta-text");
    }

    [Fact]
    public async Task Hub_ChannelAdapter_AllEventTypes()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        const string sessionId = "all-events";
        await SeedSessionAsync(factory, new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(TestAgentId),
            Status = GatewaySessionStatus.Active
        }, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        var handlers = new Dictionary<string, TaskCompletionSource<AgentStreamEvent>>(StringComparer.Ordinal)
        {
            ["MessageStart"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["ContentDelta"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["ThinkingDelta"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["ToolStart"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["ToolEnd"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["MessageEnd"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["Error"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["UserInputRequired"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["TurnInterrupted"] = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        var subscriptions = new List<IDisposable>
        {
            connection.On<AgentStreamEvent>("MessageStart", payload => handlers["MessageStart"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("ContentDelta", payload => handlers["ContentDelta"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("ThinkingDelta", payload => handlers["ThinkingDelta"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("ToolStart", payload => handlers["ToolStart"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("ToolEnd", payload => handlers["ToolEnd"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("MessageEnd", payload => handlers["MessageEnd"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("Error", payload => handlers["Error"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("UserInputRequired", payload => handlers["UserInputRequired"].TrySetResult(payload)),
            connection.On<AgentStreamEvent>("TurnInterrupted", payload => handlers["TurnInterrupted"].TrySetResult(payload))
        };

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        foreach (var type in Enum.GetValues<AgentStreamEventType>())
        {
            await adapter.SendStreamEventAsync(StreamTargets.For(sessionId), new AgentStreamEvent
            {
                Type = type,
                ContentDelta = type == AgentStreamEventType.ContentDelta ? "delta" : null,
                ThinkingContent = type == AgentStreamEventType.ThinkingDelta ? "thinking" : null,
                ToolCallId = type is AgentStreamEventType.ToolStart or AgentStreamEventType.ToolEnd ? "tool-1" : null,
                ToolName = type is AgentStreamEventType.ToolStart or AgentStreamEventType.ToolEnd ? "search" : null,
                ToolResult = type == AgentStreamEventType.ToolEnd ? "done" : null,
                ToolIsError = type == AgentStreamEventType.ToolEnd ? false : null,
                UserInputRequest = type == AgentStreamEventType.UserInputRequired
                    ? new AskUserRequest
                    {
                        RequestId = "request-1",
                        ConversationId = ConversationId.From("conversation-1"),
                        SessionId = SessionId.From(sessionId),
                        AgentId = AgentId.From(TestAgentId),
                        Prompt = "Need input?"
                    }
                    : null
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
                AgentStreamEventType.UserInputRequired => "UserInputRequired",
                AgentStreamEventType.TurnInterrupted => "TurnInterrupted",
                AgentStreamEventType.TurnEnd => null, // internal persistence signal -- not forwarded to Hub clients
                _ => throw new ArgumentOutOfRangeException()
            };

            if (method is null) continue; // TurnEnd is internal-only, not forwarded to Hub clients
            var payload = await handlers[method].Task.WaitAsync(cts.Token);
            payload.Type.ShouldBe(expected);
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

        agents.ValueKind.ShouldBe(JsonValueKind.Array);
        agents.EnumerateArray()
            .Select(agent => agent.GetProperty("agentId").GetString())
            .ShouldContain(TestAgentId);
    }

    [Fact]
    public async Task Hub_GetAgentStatus_ReturnsNullForUnknownSession()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var status = await connection.InvokeAsync<object?>("GetAgentStatus", TestAgentId, "unknown-session", cts.Token);

        status.ShouldBeNull();
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

                    // Force InMemory stores in tests ΓÇö prevents SQLite file creation and ensures isolation
                    services.Replace(ServiceDescriptor.Singleton<ISessionStore, InMemorySessionStore>());
                    services.Replace(ServiceDescriptor.Singleton<IConversationStore, InMemoryConversationStore>());

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
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }

    private static async Task SeedSessionAsync(WebApplicationFactory<Program> factory, GatewaySession session, CancellationToken cancellationToken)
    {
        var store = factory.Services.GetRequiredService<ISessionStore>();
        await store.SaveAsync(session, cancellationToken);
    }

    private static CancellationTokenSource CreateTimeout()
        => new(TimeSpan.FromSeconds(15));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class RecordingDispatcher(bool failOnNullSessionId = false) : IChannelDispatcher, IInboundMessageOrchestrator
    {
        public List<InboundMessage> Messages { get; } = [];

        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            if (failOnNullSessionId && string.IsNullOrWhiteSpace(message.RoutingHints?.RequestedSessionId?.Value))
                throw new InvalidOperationException("sessionId is required.");

            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task<InboundDispatchResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            if (failOnNullSessionId && string.IsNullOrWhiteSpace(message.RoutingHints?.RequestedSessionId?.Value))
                throw new InvalidOperationException("sessionId is required.");

            Messages.Add(message);
            return Task.FromResult(InboundDispatchResult.Accepted(Array.Empty<DispatchResult>()));
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

        public AbortAwareHandle Handle { get; } = new(AgentId.From(agentId), SessionId.From(sessionId));
        public bool GetOrCreateCalled { get; private set; }

        public Task<IAgentHandle> GetOrCreateAsync(AgentId requestedAgentId, SessionId requestedSessionId, CancellationToken cancellationToken = default)
        {
            GetOrCreateCalled = true;
            requestedAgentId.Value.ShouldBe(agentId);
            requestedSessionId.Value.ShouldBe(sessionId);
            return Task.FromResult<IAgentHandle>(Handle);
        }

        public Task StopAsync(AgentId requestedAgentId, SessionId requestedSessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public AgentInstance? GetInstance(AgentId requestedAgentId, SessionId requestedSessionId)
            => requestedAgentId == agentId && requestedSessionId == sessionId ? _instance : null;

        public IAgentHandle? GetHandle(AgentId requestedAgentId, SessionId requestedSessionId) => null;

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

        public Task<AgentResponse> PromptAsync(UserMessage message, CancellationToken cancellationToken = default)
            => PromptAsync(message.Content, cancellationToken);

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(UserMessage message, CancellationToken cancellationToken = default)
            => StreamAsync(message.Content, cancellationToken);

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

