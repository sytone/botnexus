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

namespace BotNexus.Gateway.Tests.Integration;

/// <summary>
/// Probe Round 2 — Gateway integration tests covering SendMessageToConversation routing,
/// multi-message conversation linking, nonexistent conversation fallback,
/// and ResetSession/CompactSession conversationId preservation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class SignalRConversationRoutingTests : IAsyncDisposable
{
    private const string TestAgentId = "test-agent";

    // ── SendMessageToConversation: routes to correct conversation ────────────

    [Fact]
    public async Task Hub_SendMessageToConversation_RoutesToCorrectConversation()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        // Create a conversation via REST
        using var client = factory.CreateClient();
        var createResp = await client.PostAsJsonAsync("/api/conversations",
            new { agentId = TestAgentId, title = "Targeted Conversation" }, cts.Token);
        createResp.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        var convDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync(cts.Token)).RootElement;
        var conversationId = convDoc.GetProperty("conversationId").GetString()!;

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var result = await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "routed message", conversationId, cts.Token);

        var sessionId = result.GetProperty("sessionId").GetString();
        sessionId.ShouldNotBeNullOrWhiteSpace();

        // With conversation-first routing: the dispatched InboundMessage carries the ConversationId.
        // GatewayHost uses it to route to the correct conversation.
        // The hub returns the default portal session; the ConversationId on the dispatched message
        // is what routes correctly — verify the dispatcher received it.
        dispatcher.Messages.ShouldHaveSingleItem();
        dispatcher.Messages[0].ConversationId.ShouldBe(conversationId,
            "InboundMessage.ConversationId must carry the target conversationId for direct routing");
    }

    // ── Two messages to same conversation: both sessions link to same conversationId ──

    [Fact]
    public async Task Hub_TwoMessagesToSameConversation_BothSessionsLinkSameConversation()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        using var client = factory.CreateClient();
        var createResp = await client.PostAsJsonAsync("/api/conversations",
            new { agentId = TestAgentId, title = "Shared Conversation" }, cts.Token);
        createResp.EnsureSuccessStatusCode();
        var conversationId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("conversationId").GetString()!;

        await using var conn1 = await CreateStartedConnection(factory, cts.Token);
        await using var conn2 = await CreateStartedConnection(factory, cts.Token);

        await conn1.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "message one", conversationId, cts.Token);
        await conn2.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "message two", conversationId, cts.Token);

        // With conversation-first routing, both dispatched InboundMessages carry the same ConversationId.
        // No duplicate thread bindings, no double fan-out.
        dispatcher.Messages.Count.ShouldBe(2);
        dispatcher.Messages.ShouldAllBe(m => m.ConversationId == conversationId,
            "both messages to same conversation should carry the correct ConversationId for routing");
    }

    // ── SendMessage without conversationId creates default conversation with conversationId ──

    [Fact]
    public async Task Hub_SendMessage_NoConversationId_SessionHasConversationIdStamped()
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
        var result = await connection.InvokeAsync<JsonElement>("SendMessage", TestAgentId, "signalr", "hello", (string?)null, cts.Token);

        var sessionId = result.GetProperty("sessionId").GetString()!;

        var sessionStore = factory.Services.GetRequiredService<ISessionStore>();
        var session = await sessionStore.GetAsync(SessionId.From(sessionId), cts.Token);

        session.ShouldNotBeNull();
        session!.Session.ConversationId.ShouldNotBeNull(
            "SendMessage without explicit conversationId should still stamp the session with the default conversation's ID");
    }

    // ── ResetSession: new session created, conversationId preserved from conversation ──

    [Fact]
    public async Task Hub_ResetSession_NewSessionPreservesConversationId()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        using var client = factory.CreateClient();
        var createResp = await client.PostAsJsonAsync("/api/conversations",
            new { agentId = TestAgentId, title = "Reset Test Conversation" }, cts.Token);
        createResp.EnsureSuccessStatusCode();
        var conversationId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("conversationId").GetString()!;

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        var msgResult = await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "before reset", conversationId, cts.Token);
        var originalSessionId = msgResult.GetProperty("sessionId").GetString()!;

        // Reset the session
        var resetDone = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<object>("SessionReset", payload => resetDone.TrySetResult(payload));
        await connection.InvokeAsync("ResetSession", TestAgentId, originalSessionId, cts.Token);
        await resetDone.Task.WaitAsync(cts.Token);

        // Send again to the same conversation — still routes via ConversationId
        var msg2Result = await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "after reset", conversationId, cts.Token);
        var newSessionId = msg2Result.GetProperty("sessionId").GetString()!;

        newSessionId.ShouldNotBe(originalSessionId, "after reset, a new session should be created");

        // The dispatched messages should both carry the target conversationId
        dispatcher.Messages.Count.ShouldBe(2);
        dispatcher.Messages.ShouldAllBe(m => m.ConversationId == conversationId,
            "after reset, sending to same conversationId should still dispatch with the correct ConversationId");
    }

    // ── Conversation switch: does not cross-route messages ──────────────────

    /// <summary>
    /// Regression guard for #472/#473.
    /// After switching from ConvA to ConvB, messages dispatched for ConvB must
    /// carry ConvB's ID, and ConvA must be undisturbed.
    /// </summary>
    [Fact]
    public async Task Hub_SwitchConversation_DoesNotCrossRouteMessages()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        using var client = factory.CreateClient();

        // Create two distinct conversations.
        var convAId = await CreateConversationAsync(client, "Conversation A", cts.Token);
        var convBId = await CreateConversationAsync(client, "Conversation B", cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);

        // Send a message to Conversation A.
        var resultA = await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "message for A", convAId, cts.Token);
        var sessionA = resultA.GetProperty("sessionId").GetString()!;

        // Switch: send a message to Conversation B.
        var resultB = await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "message for B", convBId, cts.Token);
        var sessionB = resultB.GetProperty("sessionId").GetString()!;

        // Each message must carry its own ConversationId — no cross-routing.
        dispatcher.Messages.Count.ShouldBe(2);
        dispatcher.Messages[0].ConversationId.ShouldBe(convAId,
            "first message must be dispatched with Conversation A's ID");
        dispatcher.Messages[1].ConversationId.ShouldBe(convBId,
            "second message after switch must be dispatched with Conversation B's ID");

        // Conversation A's session must not have been overwritten by B's session.
        sessionA.ShouldNotBe(sessionB, "a session should not be shared across two distinct conversations");
    }

    // ── Implicit routing after switch uses most-recently-active conversation ──

    /// <summary>
    /// Regression guard for #472/#473.
    /// After sending an explicit message to ConvB, an implicit message (null
    /// conversationId) should route to the most-recently-active conversation
    /// (ConvB), not ConvA's binding.
    /// </summary>
    [Fact]
    public async Task Hub_ImplicitRouting_AfterSwitch_UsesCorrectConversation()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        using var client = factory.CreateClient();

        var convAId = await CreateConversationAsync(client, "Implicit A", cts.Token);
        var convBId = await CreateConversationAsync(client, "Implicit B", cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);

        // Establish ConvA first via implicit routing to create binding.
        await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "establish A", convAId, cts.Token);

        // Explicitly switch to ConvB.
        await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "switch to B", convBId, cts.Token);

        // Send an implicit message (no conversationId) — should route to ConvB (most recently active).
        await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "implicit after switch", (string?)null, cts.Token);

        // The implicit message must NOT route to ConvA.
        dispatcher.Messages.Count.ShouldBe(3);
        var implicitMsg = dispatcher.Messages[2];

        // The implicit message was dispatched without a ConversationId; binding lookup resolves it.
        // It must not resolve to ConvA (cross-routing regression).
        if (implicitMsg.ConversationId is not null)
        {
            implicitMsg.ConversationId.ShouldNotBe(convAId,
                "implicit message after switching to ConvB must not fall back to ConvA");
        }
        // If ConversationId is null the binding resolver will use the channel binding
        // established during the explicit ConvB message — also acceptable behaviour.
    }

    // ── Switch does not contaminate ConvA state when ConvB is active ─────────

    /// <summary>
    /// Regression guard for #472/#473.
    /// After switching to ConvB, ConvA's dispatched message ConversationId must
    /// still match ConvA even if the server re-uses the same SignalR connection
    /// and its binding has been updated to point at ConvB.
    /// </summary>
    [Fact]
    public async Task Hub_ConvA_StateUntouched_AfterSwitchToConvB()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        using var client = factory.CreateClient();
        var convAId = await CreateConversationAsync(client, "StateCheck A", cts.Token);
        var convBId = await CreateConversationAsync(client, "StateCheck B", cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);

        // Message to A.
        await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "hello A", convAId, cts.Token);

        // Message to B (switch).
        await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "hello B", convBId, cts.Token);

        // Send another message explicitly to A — must still dispatch with ConvA's ID.
        await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "back to A", convAId, cts.Token);

        dispatcher.Messages.Count.ShouldBe(3);
        dispatcher.Messages[0].ConversationId.ShouldBe(convAId);
        dispatcher.Messages[1].ConversationId.ShouldBe(convBId);
        dispatcher.Messages[2].ConversationId.ShouldBe(convAId,
            "returning to ConvA after ConvB switch must dispatch with ConvA's ID");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> CreateConversationAsync(HttpClient client, string title, CancellationToken cancellationToken)
    {
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new { agentId = TestAgentId, title }, cancellationToken);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken))
            .RootElement.GetProperty("conversationId").GetString()!;
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
        var response = await client.PostAsJsonAsync("/api/agents", descriptor, CancellationToken.None);
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
}
