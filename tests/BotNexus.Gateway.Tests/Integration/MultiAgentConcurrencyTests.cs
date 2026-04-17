using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.AgentCore.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api;
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
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Integration;

/// <summary>
/// Integration tests that exercise the full SignalR → Gateway → Agent pipeline
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
        supervisor.RegisterAgent("agent-slow", TimeSpan.FromSeconds(3), "slow-response");
        supervisor.RegisterAgent("agent-fast", TimeSpan.FromMilliseconds(100), "fast-response");

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

        var responses = new ConcurrentDictionary<string, (string Content, DateTimeOffset ReceivedAt)>();

        // Register handler BEFORE sending messages
        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.SessionId is { } sid && payload.ContentDelta is not null)
            {
                responses.TryAdd(sid.Value, (payload.ContentDelta, DateTimeOffset.UtcNow));
            }
        });

        // Act - Send messages concurrently
        var slowResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-slow", "signalr", "test-slow", cts.Token);
        var fastResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-fast", "signalr", "test-fast", cts.Token);

        var slowSessionId = slowResult.GetProperty("sessionId").GetString();
        var fastSessionId = fastResult.GetProperty("sessionId").GetString();

        slowSessionId.Should().NotBeNullOrWhiteSpace();
        fastSessionId.Should().NotBeNullOrWhiteSpace();

        // Assert - Wait for both responses with timeout
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (responses.Count < 2 && DateTimeOffset.UtcNow < deadline && !cts.Token.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token);
        }

        responses.Should().ContainKey(slowSessionId!);
        responses.Should().ContainKey(fastSessionId!);

        responses[slowSessionId!].Content.Should().Be("slow-response");
        responses[fastSessionId!].Content.Should().Be("fast-response");

        // CRITICAL: fast agent should respond before slow agent
        responses[fastSessionId!].ReceivedAt.Should().BeBefore(responses[slowSessionId!].ReceivedAt);
    }

    /// <summary>
    /// Test three agents with different delays responding independently.
    /// </summary>
    [Fact]
    public async Task SendMessage_ThreeAgentsParallel_AllRespondIndependently()
    {
        // Arrange
        var supervisor = new DelayedStreamingSupervisor();
        supervisor.RegisterAgent("agent-slow", TimeSpan.FromSeconds(2), "slow-response");
        supervisor.RegisterAgent("agent-medium", TimeSpan.FromSeconds(1), "medium-response");
        supervisor.RegisterAgent("agent-fast", TimeSpan.FromMilliseconds(500), "fast-response");

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

        var responses = new ConcurrentDictionary<string, (string Content, DateTimeOffset ReceivedAt)>();

        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.SessionId is { } sid && payload.ContentDelta is not null)
            {
                responses.TryAdd(sid.Value, (payload.ContentDelta, DateTimeOffset.UtcNow));
            }
        });

        // Act - Send messages to all 3 agents concurrently
        var slowResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-slow", "signalr", "test", cts.Token);
        var mediumResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-medium", "signalr", "test", cts.Token);
        var fastResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-fast", "signalr", "test", cts.Token);

        var slowSessionId = slowResult.GetProperty("sessionId").GetString()!;
        var mediumSessionId = mediumResult.GetProperty("sessionId").GetString()!;
        var fastSessionId = fastResult.GetProperty("sessionId").GetString()!;

        // Assert - Wait for all 3 responses
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (responses.Count < 3 && DateTimeOffset.UtcNow < deadline && !cts.Token.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token);
        }

        responses.Should().HaveCount(3);
        responses.Should().ContainKey(slowSessionId);
        responses.Should().ContainKey(mediumSessionId);
        responses.Should().ContainKey(fastSessionId);

        // Verify responses came in the right order (fastest first)
        responses[fastSessionId].ReceivedAt.Should().BeBefore(responses[mediumSessionId].ReceivedAt);
        responses[mediumSessionId].ReceivedAt.Should().BeBefore(responses[slowSessionId].ReceivedAt);
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
        supervisor.RegisterAgent("agent-a", TimeSpan.FromSeconds(2), "response-a");
        supervisor.RegisterAgent("agent-b", TimeSpan.FromMilliseconds(100), "response-b");

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

        var responses = new ConcurrentDictionary<string, (string Content, DateTimeOffset ReceivedAt)>();

        using var _ = connection.On<AgentStreamEvent>("ContentDelta", payload =>
        {
            if (payload.SessionId is { } sid && payload.ContentDelta is not null)
            {
                responses.TryAdd(sid.Value, (payload.ContentDelta, DateTimeOffset.UtcNow));
            }
        });

        // Act
        var resultA = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-a", "signalr", "start processing", cts.Token);
        
        // Wait 100ms to ensure agent-a is processing
        await Task.Delay(100, cts.Token);
        
        var resultB = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-b", "signalr", "quick response", cts.Token);

        var sessionA = resultA.GetProperty("sessionId").GetString()!;
        var sessionB = resultB.GetProperty("sessionId").GetString()!;

        // Assert - Wait for both responses
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (responses.Count < 2 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, cts.Token);
        }

        responses.Should().HaveCount(2);
        responses[sessionA].Content.Should().Be("response-a");
        responses[sessionB].Content.Should().Be("response-b");

        // Assert agent-b responded before agent-a (proves no serialization)
        responses[sessionB].ReceivedAt.Should().BeBefore(responses[sessionA].ReceivedAt);
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
        supervisor.RegisterAgent("agent-reset", TimeSpan.FromMilliseconds(100), "reset-response");

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
        var firstResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-reset", "signalr", "first message", cts.Token);
        var firstSessionId = firstResult.GetProperty("sessionId").GetString()!;

        // Wait for first response
        await WaitForResponseCount(responses, 1, cts.Token);
        responses.Should().ContainSingle(r => r.SessionId == firstSessionId && r.Content == "reset-response");

        // Reset session
        await connection.InvokeAsync("ResetSession", "agent-reset", firstSessionId, cts.Token);

        // Send second message to same agent
        var secondResult = await connection.InvokeAsync<JsonElement>("SendMessage", "agent-reset", "signalr", "second message", cts.Token);
        var secondSessionId = secondResult.GetProperty("sessionId").GetString()!;

        // Assert - Should create new session and respond
        secondSessionId.Should().NotBe(firstSessionId);
        
        await WaitForResponseCount(responses, 2, cts.Token);
        responses.Should().Contain(r => r.SessionId == secondSessionId && r.Content == "reset-response");
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
            supervisor.RegisterAgent(agentId, TimeSpan.FromMilliseconds(200 + (agentIds.IndexOf(agentId) * 50)), $"response-{agentId}");
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
            var result = await connection.InvokeAsync<JsonElement>("SendMessage", agentId, "signalr", "concurrent start", cts.Token);
            return (AgentId: agentId, SessionId: result.GetProperty("sessionId").GetString()!);
        });

        var results = await Task.WhenAll(sendTasks);

        // Assert - All agents should respond
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (responses.Count < agentCount && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, cts.Token);
        }

        responses.Should().HaveCount(agentCount);
        
        foreach (var (agentId, sessionId) in results)
        {
            responses.Should().ContainKey(sessionId);
            responses[sessionId].Should().Be($"response-{agentId}");
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

        public DelayedStreamingHandle RegisterAgent(string agentId, TimeSpan responseDelay, string responseContent)
        {
            var handle = new DelayedStreamingHandle(agentId, responseDelay, responseContent);
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
        public IReadOnlyList<AgentInstance> GetAllInstances() => [];
        public Task StopAllAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Agent handle that streams a response after a configurable delay.
    /// </summary>
    private sealed class DelayedStreamingHandle : IAgentHandle
    {
        private readonly string _agentId;
        private readonly TimeSpan _delay;
        private readonly string _content;
        private SessionId _sessionId;
        private readonly TaskCompletionSource _promptStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DelayedStreamingHandle(string agentId, TimeSpan delay, string content)
        {
            _agentId = agentId;
            _delay = delay;
            _content = content;
        }

        public AgentId AgentId => AgentId.From(_agentId);
        public SessionId SessionId => _sessionId;
        public bool IsRunning { get; private set; }
        public Task WaitForPromptStart => _promptStarted.Task;

        public void SetSessionId(SessionId sessionId) => _sessionId = sessionId;

        public Task<AgentResponse> PromptAsync(string message, CancellationToken ct)
        {
            IsRunning = true;
            _promptStarted.TrySetResult();
            // Simulate LLM processing delay
            return Task.Run(async () =>
            {
                await Task.Delay(_delay, ct);
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

            // Simulate LLM processing delay
            await Task.Delay(_delay, ct);

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

        public Task AbortAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
        public Task SteerAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public Task FollowUpAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public Task FollowUpAsync(AgentMessage message, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Helper method to wait for a specific number of responses.
    /// </summary>
    private static async Task WaitForResponseCount(List<(string SessionId, string Content)> responses, int expectedCount, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (deadline > DateTimeOffset.UtcNow && !ct.IsCancellationRequested)
        {
            lock (responses)
            {
                if (responses.Count >= expectedCount)
                    return;
            }
            await Task.Delay(50, ct);
        }
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
    private static async Task<HubConnection> CreateStartedConnection(WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        var connection = CreateHubConnection(factory);
        await connection.StartAsync(cancellationToken);
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
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }

    /// <summary>
    /// Creates a cancellation token with 30-second timeout for concurrency tests.
    /// </summary>
    private static CancellationTokenSource CreateTimeout()
        => new(TimeSpan.FromSeconds(30));

    #endregion

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}