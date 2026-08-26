using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
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
/// Integration tests that exercise the full SignalR → Gateway → BotNexus.Agent.Core.Agent pipeline
/// to verify multi-agent concurrency works correctly and agents don't block each other.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class MultiAgentConcurrencyTests : IAsyncDisposable
{
    private const string TestAgentId = "test-agent";

    /// <summary>
    /// THE CRITICAL TEST — proves agents don't block each other.
    /// Fast agent (100ms) should complete before slow agent (3s).
    /// </summary>
    [Fact]
    public async Task SendMessage_TwoAgentsConcurrently_BothReceiveResponses()
    {
        // Arrange
        var supervisor = new DelayedStreamingSupervisor();
        var slowAgent = supervisor.RegisterGatedAgent("agent-slow", "slow-response");
        var fastAgent = supervisor.RegisterGatedAgent("agent-fast", "fast-response");

        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IAgentSupervisor>();
            services.AddSingleton<IAgentSupervisor>(supervisor);
        });

        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token, "agent-slow");
        await RegisterAgentAsync(factory, cts.Token, "agent-fast");

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        // Arrival is recorded as a monotonic ordinal rather than a wall-clock timestamp:
        // DateTimeOffset.UtcNow has ~15ms granularity on a contended runner, so two deltas that
        // genuinely arrived in order can carry identical timestamps and make a strict-inequality
        // ordering assertion flake (#3372). The ordinal is strictly increasing by construction.
        var responses = new ConcurrentDictionary<string, (string Content, long Arrival)>();
        var arrivalCounter = 0L;

        // Register handler BEFORE sending messages
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.SessionId is { } sid && payload.ContentDelta is not null)
            {
                responses.TryAdd(sid.Value, (payload.ContentDelta, Interlocked.Increment(ref arrivalCounter)));
            }
        });

        // Act - Send messages concurrently
        var slowResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-slow", "signalr", "test-slow", (string?)null, cts.Token);
        var fastResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-fast", "signalr", "test-fast", (string?)null, cts.Token);

        var slowSessionId = slowResult.GetProperty("sessionId").GetString();
        var fastSessionId = fastResult.GetProperty("sessionId").GetString();

        slowSessionId.ShouldNotBeNullOrWhiteSpace();
        fastSessionId.ShouldNotBeNullOrWhiteSpace();

        await slowAgent.WaitForPromptStart;
        await fastAgent.WaitForPromptStart;
        fastAgent.ReleaseResponse();
        await TestAwait.EventuallyAsync(
            () => responses.ContainsKey(fastSessionId!),
            "the explicitly released fast agent response to arrive",
            cancellationToken: cts.Token);
        responses.ShouldNotContainKey(slowSessionId!);

        slowAgent.ReleaseResponse();
        await TestAwait.EventuallyAsync(
            () => responses.Count == 2,
            "both explicitly released agent responses to arrive",
            cancellationToken: cts.Token);

        responses.ShouldContainKey(slowSessionId!);
        responses.ShouldContainKey(fastSessionId!);

        responses[slowSessionId!].Content.ShouldBe("slow-response");
        responses[fastSessionId!].Content.ShouldBe("fast-response");

        // CRITICAL: fast agent should respond before slow agent
        responses[fastSessionId!].Arrival.ShouldBeLessThan(responses[slowSessionId!].Arrival);
    }

    /// <summary>
    /// Test three agents with different delays responding independently.
    /// </summary>
    [Fact]
    public async Task SendMessage_ThreeAgentsParallel_AllRespondIndependently()
    {
        // Arrange
        var supervisor = new DelayedStreamingSupervisor();
        var slowAgent = supervisor.RegisterGatedAgent("agent-slow", "slow-response");
        var mediumAgent = supervisor.RegisterGatedAgent("agent-medium", "medium-response");
        var fastAgent = supervisor.RegisterGatedAgent("agent-fast", "fast-response");

        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IAgentSupervisor>();
            services.AddSingleton<IAgentSupervisor>(supervisor);
        });

        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token, "agent-slow");
        await RegisterAgentAsync(factory, cts.Token, "agent-medium");
        await RegisterAgentAsync(factory, cts.Token, "agent-fast");

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        // Monotonic arrival ordinal — see the note in SendMessage_TwoAgentsConcurrently_BothReceiveResponses (#3372).
        var responses = new ConcurrentDictionary<string, (string Content, long Arrival)>();
        var arrivalCounter = 0L;

        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.SessionId is { } sid && payload.ContentDelta is not null)
            {
                responses.TryAdd(sid.Value, (payload.ContentDelta, Interlocked.Increment(ref arrivalCounter)));
            }
        });

        // Act - Send messages to all 3 agents concurrently
        var slowResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-slow", "signalr", "test", (string?)null, cts.Token);
        var mediumResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-medium", "signalr", "test", (string?)null, cts.Token);
        var fastResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-fast", "signalr", "test", (string?)null, cts.Token);

        var slowSessionId = slowResult.GetProperty("sessionId").GetString()!;
        var mediumSessionId = mediumResult.GetProperty("sessionId").GetString()!;
        var fastSessionId = fastResult.GetProperty("sessionId").GetString()!;

        await Task.WhenAll(slowAgent.WaitForPromptStart, mediumAgent.WaitForPromptStart, fastAgent.WaitForPromptStart);
        fastAgent.ReleaseResponse();
        await TestAwait.EventuallyAsync(
            () => responses.ContainsKey(fastSessionId),
            "the explicitly released fast agent response to arrive",
            cancellationToken: cts.Token);
        mediumAgent.ReleaseResponse();
        await TestAwait.EventuallyAsync(
            () => responses.ContainsKey(mediumSessionId),
            "the explicitly released medium agent response to arrive",
            cancellationToken: cts.Token);
        slowAgent.ReleaseResponse();
        await TestAwait.EventuallyAsync(
            () => responses.ContainsKey(slowSessionId),
            "the explicitly released slow agent response to arrive",
            cancellationToken: cts.Token);

        responses.Count().ShouldBe(3);
        responses.ShouldContainKey(slowSessionId);
        responses.ShouldContainKey(mediumSessionId);
        responses.ShouldContainKey(fastSessionId);

        // Verify responses came in the right order (fastest first)
        responses[fastSessionId].Arrival.ShouldBeLessThan(responses[mediumSessionId].Arrival);
        responses[mediumSessionId].Arrival.ShouldBeLessThan(responses[slowSessionId].Arrival);
    }

    /// <summary>
    /// Test that using a single connection (like the web UI) doesn't serialize agent responses.
    /// This is the most precise test for the SignalR per-connection serialization bug.
    /// </summary>
    [Fact]
    public async Task SendMessage_SingleConnection_MultipleAgents_NoSerialization()
    {
        // Arrange
        var supervisor = new DelayedStreamingSupervisor();
        var agentA = supervisor.RegisterGatedAgent("agent-a", "response-a");
        var agentB = supervisor.RegisterGatedAgent("agent-b", "response-b");

        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IAgentSupervisor>();
            services.AddSingleton<IAgentSupervisor>(supervisor);
        });

        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token, "agent-a");
        await RegisterAgentAsync(factory, cts.Token, "agent-b");

        // ONE connection — critical for testing per-connection serialization
        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        // Monotonic arrival ordinal — see the note in SendMessage_TwoAgentsConcurrently_BothReceiveResponses (#3372).
        var responses = new ConcurrentDictionary<string, (string Content, long Arrival)>();
        var arrivalCounter = 0L;

        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.SessionId is { } sid && payload.ContentDelta is not null)
            {
                responses.TryAdd(sid.Value, (payload.ContentDelta, Interlocked.Increment(ref arrivalCounter)));
            }
        });

        // Act
        var resultA = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-a", "signalr", "start processing", (string?)null, cts.Token);
        await agentA.WaitForPromptStart;
        var resultB = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-b", "signalr", "quick response", (string?)null, cts.Token);
        await agentB.WaitForPromptStart;

        var sessionA = resultA.GetProperty("sessionId").GetString()!;
        var sessionB = resultB.GetProperty("sessionId").GetString()!;

        agentB.ReleaseResponse();
        await TestAwait.EventuallyAsync(
            () => responses.ContainsKey(sessionB),
            "agent B to respond while agent A remains blocked",
            cancellationToken: cts.Token);
        responses.ShouldNotContainKey(sessionA);

        agentA.ReleaseResponse();
        await TestAwait.EventuallyAsync(
            () => responses.Count == 2,
            "both single-connection agent responses to arrive",
            cancellationToken: cts.Token);

        responses.Count().ShouldBe(2);
        responses[sessionA].Content.ShouldBe("response-a");
        responses[sessionB].Content.ShouldBe("response-b");

        // Assert agent-b responded before agent-a (proves no serialization).
        // agent-a is a 2s agent started first; agent-b is a 100ms agent started 100ms later. If the
        // hub serialized per-connection work, b's delta could only arrive after a's, so a strictly
        // lower arrival ordinal for b is exactly the non-serialization property.
        responses[sessionB].Arrival.ShouldBeLessThan(responses[sessionA].Arrival);
    }

    /// <summary>
    /// Test that ResetSession allows reusing the same agent for a new session
    /// (tests sealed session reactivation).
    /// </summary>
    [Fact]
    public async Task SendMessage_SameAgent_NewSession_AfterReset()
    {
        // Arrange
        var supervisor = new DelayedStreamingSupervisor();
        supervisor.RegisterAgent("agent-reset", "reset-response");

        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IAgentSupervisor>();
            services.AddSingleton<IAgentSupervisor>(supervisor);
        });

        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token, "agent-reset");

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        var responses = new List<(string SessionId, string Content)>();

        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.SessionId is { } sid && payload.ContentDelta is not null)
            {
                lock (responses)
                {
                    responses.Add((sid.Value, payload.ContentDelta));
                }
            }
        });

        // Act - Send first message
        var firstResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-reset", "signalr", "first message", (string?)null, cts.Token);
        var firstSessionId = firstResult.GetProperty("sessionId").GetString()!;

        // Wait for first response
        await WaitForResponseCount(responses, 1, cts.Token);
        responses.Where(r => r.SessionId == firstSessionId && r.Content == "reset-response").ShouldHaveSingleItem();

        // Reset session
        await connection.InvokeAsync("ResetSession", "agent-reset", firstSessionId, cts.Token);

        // Send second message to same agent
        var secondResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-reset", "signalr", "second message", (string?)null, cts.Token);
        var secondSessionId = secondResult.GetProperty("sessionId").GetString()!;

        // Assert - Should create new session and respond
        secondSessionId.ShouldNotBe(firstSessionId);
        
        await WaitForResponseCount(responses, 2, cts.Token);
        responses.ShouldContain(r => r.SessionId == secondSessionId && r.Content == "reset-response");
    }

    /// <summary>
    /// Test that agents can be started concurrently and all complete successfully.
    /// This tests the agent supervisor's ability to handle concurrent GetOrCreateAsync calls.
    /// </summary>
    [Fact]
    public async Task SendMessage_ConcurrentAgentStartup_AllComplete()
    {
        // Arrange
        var supervisor = new DelayedStreamingSupervisor();
        var agentCount = 5;
        var agentIds = Enumerable.Range(0, agentCount).Select(i => $"startup-agent-{i}").ToList();

        foreach (var agentId in agentIds)
        {
            supervisor.RegisterAgent(agentId, $"response-{agentId}");
        }

        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IAgentSupervisor>();
            services.AddSingleton<IAgentSupervisor>(supervisor);
        });

        using var cts = CreateTimeout();
        
        foreach (var agentId in agentIds)
        {
            await RegisterAgentAsync(factory, cts.Token, agentId);
        }

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync<JsonElement>("SubscribeAll", cts.Token);

        var responses = new ConcurrentDictionary<string, string>();

        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.SessionId is { } sid && payload.ContentDelta is not null)
            {
                responses.TryAdd(sid.Value, payload.ContentDelta);
            }
        });

        // Act - Start all agents concurrently
        var sendTasks = agentIds.Select(async agentId =>
        {
            var result = await connection.InvokeAsync<JsonElement>("SendMessage", agentId, "signalr", "concurrent start", (string?)null, cts.Token);
            return (AgentId: agentId, SessionId: result.GetProperty("sessionId").GetString()!);
        });

        var results = await Task.WhenAll(sendTasks);

        await TestAwait.EventuallyAsync(
            () => responses.Count == agentCount,
            "all concurrently started agents to respond",
            cancellationToken: cts.Token);

        responses.Count().ShouldBe(agentCount);
        
        foreach (var (agentId, sessionId) in results)
        {
            responses.ShouldContainKey(sessionId);
            responses[sessionId].ShouldBe($"response-{agentId}");
        }
    }

    #region Test Infrastructure

    /// <summary>
    /// Test supervisor that supports multiple agents, each returning a streaming response
    /// with a configurable delay to simulate LLM processing time.
    /// </summary>
    private sealed class DelayedStreamingSupervisor : IAgentSupervisor
    {
        private readonly ConcurrentDictionary<string, DelayedStreamingHandle> _handles = new();

        public DelayedStreamingHandle RegisterAgent(string agentId, string responseContent)
        {
            var handle = new DelayedStreamingHandle(agentId, responseContent, gated: false);
            _handles[agentId] = handle;
            return handle;
        }

        public DelayedStreamingHandle RegisterGatedAgent(string agentId, string responseContent)
        {
            var handle = new DelayedStreamingHandle(agentId, responseContent, gated: true);
            _handles[agentId] = handle;
            return handle;
        }

        public Task<IAgentHandle> GetOrCreateAsync(AgentId agentId, SessionId sessionId, CancellationToken ct)
        {
            if (_handles.TryGetValue(agentId.Value, out var handle))
            {
                handle.SetSessionId(sessionId);
                return Task.FromResult<IAgentHandle>(handle);
            }
            throw new KeyNotFoundException($"Test agent '{agentId}' not registered in supervisor");
        }

        public Task StopAsync(AgentId agentId, SessionId sessionId, CancellationToken ct) => Task.CompletedTask;
        public AgentInstance? GetInstance(AgentId agentId, SessionId sessionId) => null;
        public IAgentHandle? GetHandle(AgentId agentId, SessionId sessionId) => null;
        public IReadOnlyList<AgentInstance> GetAllInstances() => [];
        public Task StopAllAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Agent handle that streams a response after a configurable delay.
    /// </summary>
    private sealed class DelayedStreamingHandle : IAgentHandle
    {
        private readonly string _agentId;
        private readonly string _content;
        private readonly TaskCompletionSource? _responseRelease;
        private SessionId _sessionId;
        private readonly TaskCompletionSource _promptStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DelayedStreamingHandle(string agentId, string content, bool gated)
        {
            _agentId = agentId;
            _content = content;
            _responseRelease = gated
                ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
        }

        public AgentId AgentId => AgentId.From(_agentId);
        public SessionId SessionId => _sessionId;
        public bool IsRunning { get; private set; }
        public Task WaitForPromptStart => _promptStarted.Task;

        public void ReleaseResponse()
            => (_responseRelease ?? throw new InvalidOperationException("Agent response is not gated."))
                .TrySetResult();

        public void SetSessionId(SessionId sessionId) => _sessionId = sessionId;

        public Task<AgentResponse> PromptAsync(string message, CancellationToken ct)
        {
            IsRunning = true;
            _promptStarted.TrySetResult();
            // Simulate LLM processing delay
            return Task.Run(async () =>
            {
                await WaitForResponseReleaseAsync(ct);
                IsRunning = false;
                return new AgentResponse { Content = _content };
            }, ct);
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, 
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            IsRunning = true;
            _promptStarted.TrySetResult();

            yield return new AgentStreamEvent 
            { 
                Type = AgentStreamEventType.MessageStart, 
                MessageId = Guid.NewGuid().ToString("N") 
            };

            await WaitForResponseReleaseAsync(ct);

            yield return new AgentStreamEvent 
            { 
                Type = AgentStreamEventType.ContentDelta, 
                ContentDelta = _content 
            };

            yield return new AgentStreamEvent 
            { 
                Type = AgentStreamEventType.MessageEnd, 
                MessageId = Guid.NewGuid().ToString("N") 
            };

            IsRunning = false;
        }

        public Task<AgentResponse> PromptAsync(BotNexus.Gateway.Abstractions.Models.AgentUserMessage message, CancellationToken ct)
            => PromptAsync(message.Content, ct);

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(BotNexus.Gateway.Abstractions.Models.AgentUserMessage message, CancellationToken ct)
            => StreamAsync(message.Content, ct);

        public Task AbortAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
        public Task InterruptAndSteerAsync(string message, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SteerAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public Task FollowUpAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public Task FollowUpAsync(AgentTranscriptMessage message, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Task WaitForResponseReleaseAsync(CancellationToken cancellationToken)
            => _responseRelease?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Helper method to wait for a specific number of responses.
    /// </summary>
    private static async Task WaitForResponseCount(List<(string SessionId, string Content)> responses, int expectedCount, CancellationToken ct)
    {
        await TestAwait.EventuallyAsync(
            () =>
            {
                lock (responses)
                    return responses.Count >= expectedCount;
            },
            $"{expectedCount} multi-agent response(s) to arrive",
            cancellationToken: ct);
    }

    /// <summary>
    /// Creates a test factory with optional service configuration.
    /// </summary>
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

                    services.Replace(ServiceDescriptor.Singleton<ISessionStore, InMemorySessionStore>());
                    services.Replace(ServiceDescriptor.Singleton<IConversationStore, InMemoryConversationStore>());

                    services.RemoveAll<IAgentConfigurationWriter>();
                    services.AddSingleton<IAgentConfigurationWriter, NoOpAgentConfigurationWriter>();

                    configureServices?.Invoke(services);
                });
            });

    /// <summary>
    /// Creates a SignalR hub connection for testing.
    /// </summary>
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

    /// <summary>
    /// Creates and starts a SignalR hub connection for testing.
    /// </summary>
    private static async Task<HubConnection> CreateStartedConnection(WebApplicationFactory<Program> factory, CancellationToken cancellationToken, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var connection = CreateHubConnection(factory);
        await HubFixtureGuard.StartGuardedAsync(connection, "MultiAgentConcurrencyTests", cancellationToken, testName: testName);
        return connection;
    }

    /// <summary>
    /// Registers an agent via the API.
    /// </summary>
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

    /// <summary>
    /// Creates a cancellation token with 30-second timeout for concurrency tests.
    /// </summary>
    private static CancellationTokenSource CreateTimeout()
        => new(TimeSpan.FromSeconds(30));

    #endregion

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}