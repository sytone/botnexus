using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.Hubs;
using BotNexus.Gateway.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
        const string sessionId = "dispatch-session";
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);
        await connection.InvokeAsync("SendMessage", TestAgentId, sessionId, "hello", cts.Token);

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

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionA = "switch-a";
        const string sessionB = "switch-b";

        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionA, cts.Token);
        await connection.InvokeAsync("LeaveSession", sessionA, cts.Token);
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionB, cts.Token);
        await connection.InvokeAsync("SendMessage", TestAgentId, sessionB, "latest", cts.Token);

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

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionA = "rapid-a";
        const string sessionB = "rapid-b";

        var joinA = connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionA, cts.Token);
        var joinB = connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionB, cts.Token);
        var results = await Task.WhenAll(joinA, joinB);
        results.Should().Contain(result => result.GetProperty("sessionId").GetString() == sessionB);

        await connection.InvokeAsync("SendMessage", TestAgentId, sessionB, "after-switch", cts.Token);
        dispatcher.Messages.Should().ContainSingle();
        dispatcher.Messages[0].SessionId.Should().Be(sessionB);
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

    private static async Task RegisterAgentAsync(WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient();
        var descriptor = new AgentDescriptor
        {
            AgentId = TestAgentId,
            DisplayName = "Test Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };

        var response = await client.PostAsJsonAsync("/api/agents", descriptor, CancellationToken.None);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
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
            AgentId = agentId,
            SessionId = sessionId,
            InstanceId = $"{agentId}::{sessionId}",
            IsolationStrategy = "in-process",
            Status = AgentInstanceStatus.Running
        };

        public AbortAwareHandle Handle { get; } = new(agentId, sessionId);
        public bool GetOrCreateCalled { get; private set; }

        public Task<IAgentHandle> GetOrCreateAsync(string requestedAgentId, string requestedSessionId, CancellationToken cancellationToken = default)
        {
            GetOrCreateCalled = true;
            requestedAgentId.Should().Be(agentId);
            requestedSessionId.Should().Be(sessionId);
            return Task.FromResult<IAgentHandle>(Handle);
        }

        public Task StopAsync(string requestedAgentId, string requestedSessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public AgentInstance? GetInstance(string requestedAgentId, string requestedSessionId)
            => requestedAgentId == agentId && requestedSessionId == sessionId ? _instance : null;

        public IReadOnlyList<AgentInstance> GetAllInstances() => [_instance];

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AbortAwareHandle(string agentId, string sessionId) : IAgentHandle
    {
        public string AgentId { get; } = agentId;
        public string SessionId { get; } = sessionId;
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
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

