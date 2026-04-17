using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.AgentCore.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Agents;
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
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Sessions;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class SessionSwitchingTests : IAsyncDisposable
{
    private const string TestAgentId = "test-agent";

    [Fact]
    public async Task SessionSwitch_LeavesOldGroup()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string oldSession = "switch-old";
        const string newSession = "switch-new";

        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, oldSession, cts.Token);
        await connection.InvokeAsync("LeaveSession", oldSession, cts.Token);
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, newSession, cts.Token);

        var newSessionEvent = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSessionEventReceived = false;
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.ContentDelta == "old-session-event")
                oldSessionEventReceived = true;

            if (payload.ContentDelta == "new-session-event")
                newSessionEvent.TrySetResult(payload);
        });

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(oldSession, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "old-session-event"
        }, cts.Token);

        await Task.Delay(250, cts.Token);
        oldSessionEventReceived.Should().BeFalse();

        await adapter.SendStreamEventAsync(newSession, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "new-session-event"
        }, cts.Token);

        (await newSessionEvent.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("new-session-event");
    }

    [Fact]
    public async Task SessionSwitch_JoinsNewGroup()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionId = "switch-join-group";
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionId, cts.Token);

        var eventReceived = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload => eventReceived.TrySetResult(payload));

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(sessionId, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "new-group-event"
        }, cts.Token);

        (await eventReceived.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("new-group-event");
    }

    [Fact]
    public async Task SessionSwitch_NoOrphanSessionCreated()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionA = "no-orphan-a";
        const string sessionB = "no-orphan-b";

        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionA, cts.Token);
        await connection.InvokeAsync("LeaveSession", sessionA, cts.Token);
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionB, cts.Token);

        var store = factory.Services.GetRequiredService<ISessionStore>();
        var sessions = await store.ListAsync(TestAgentId, cts.Token);

        sessions.Select(s => s.SessionId.Value).Should().BeEquivalentTo([sessionA, sessionB]);
    }

    [Fact]
    public async Task StreamEvents_ScopedToSession()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connectionA = await CreateStartedConnection(factory, cts.Token);
        await using var connectionB = await CreateStartedConnection(factory, cts.Token);
        const string sessionA = "scoped-a";
        const string sessionB = "scoped-b";

        await connectionA.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionA, cts.Token);
        await connectionB.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionB, cts.Token);

        var contentDeltaForA = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolStartForA = new TaskCompletionSource<AgentStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReceived = false;

        using var _ = connectionA.On<AgentStreamEvent>("ContentDelta", payload => contentDeltaForA.TrySetResult(payload));
        using var __ = connectionA.On<AgentStreamEvent>("ToolStart", payload => toolStartForA.TrySetResult(payload));
        using var ___ = connectionB.On<AgentStreamEvent>("ContentDelta", _ => bReceived = true);
        using var ____ = connectionB.On<AgentStreamEvent>("ToolStart", _ => bReceived = true);

        var adapter = factory.Services.GetRequiredService<SignalRChannelAdapter>();
        await adapter.SendStreamEventAsync(sessionA, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "session-a-delta"
        }, cts.Token);
        await adapter.SendStreamEventAsync(sessionA, new AgentStreamEvent
        {
            Type = AgentStreamEventType.ToolStart,
            ToolCallId = "tool-1",
            ToolName = "search"
        }, cts.Token);

        (await contentDeltaForA.Task.WaitAsync(cts.Token)).ContentDelta.Should().Be("session-a-delta");
        (await toolStartForA.Task.WaitAsync(cts.Token)).Type.Should().Be(AgentStreamEventType.ToolStart);
        await Task.Delay(250, cts.Token);
        bReceived.Should().BeFalse();
    }

    [Fact]
    public async Task AgentContinues_AfterClientSwitches()
    {
        var supervisor = new PersistentSessionSupervisor(TestAgentId, "running-session");
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IAgentSupervisor>();
            services.AddSingleton<IAgentSupervisor>(supervisor);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string runningSession = "running-session";
        const string switchedSession = "switched-session";

        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, runningSession, cts.Token);
        await connection.InvokeAsync("LeaveSession", runningSession, cts.Token);
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, switchedSession, cts.Token);

        var status = await connection.InvokeAsync<JsonElement>("GetAgentStatus", TestAgentId, runningSession, cts.Token);

        status.GetProperty("sessionId").GetString().Should().Be(runningSession);
        supervisor.StopCalled.Should().BeFalse();
    }

    [Fact]
    public async Task SessionState_IndependentPerSession()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        const string sessionA = "independent-a";
        const string sessionB = "independent-b";

        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionA, cts.Token);
        await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionB, cts.Token);

        var store = factory.Services.GetRequiredService<ISessionStore>();
        var first = await store.GetAsync(sessionA, cts.Token);
        first.Should().NotBeNull();
        first!.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello a" });
        await store.SaveAsync(first, cts.Token);

        var aJoin = await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionA, cts.Token);
        var bJoin = await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, sessionB, cts.Token);

        aJoin.GetProperty("messageCount").GetInt32().Should().Be(1);
        aJoin.GetProperty("isResumed").GetBoolean().Should().BeTrue();
        bJoin.GetProperty("messageCount").GetInt32().Should().Be(0);
        bJoin.GetProperty("isResumed").GetBoolean().Should().BeFalse();
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

                    services.AddSignalRChannelForTests();

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
        await connection.StartAsync(cancellationToken);
        return connection;
    }

    private static async Task RegisterAgentAsync(WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient();
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From(TestAgentId),
            DisplayName = "Test Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };

        var response = await client.PostAsJsonAsync("/api/agents", descriptor, cancellationToken);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }

    private static CancellationTokenSource CreateTimeout()
        => new(TimeSpan.FromSeconds(15));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class PersistentSessionSupervisor(string agentId, string sessionId) : IAgentSupervisor
    {
        private readonly AgentInstance _instance = new()
        {
            AgentId = AgentId.From(agentId),
            SessionId = SessionId.From(sessionId),
            InstanceId = $"{agentId}::{sessionId}",
            IsolationStrategy = "in-process",
            Status = AgentInstanceStatus.Running
        };

        public bool StopCalled { get; private set; }

        public Task<IAgentHandle> GetOrCreateAsync(AgentId requestedAgentId, SessionId requestedSessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentHandle>(new NoOpAgentHandle(requestedAgentId, requestedSessionId));

        public Task StopAsync(AgentId requestedAgentId, SessionId requestedSessionId, CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            return Task.CompletedTask;
        }

        public AgentInstance? GetInstance(AgentId requestedAgentId, SessionId requestedSessionId)
            => requestedAgentId == _instance.AgentId && requestedSessionId == _instance.SessionId ? _instance : null;

        public IReadOnlyList<AgentInstance> GetAllInstances() => [_instance];

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpAgentHandle(AgentId agentId, SessionId sessionId) : IAgentHandle
    {
        public BotNexus.Domain.Primitives.AgentId AgentId { get; } = agentId;
        public BotNexus.Domain.Primitives.SessionId SessionId { get; } = sessionId;
        public bool IsRunning => true;

        public Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Content = string.Empty });

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SteerAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FollowUpAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FollowUpAsync(AgentMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
