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

    // ── Regression #472: switching conversations does not cross-route messages ──

    [Fact]
    public async Task Hub_SwitchConversation_DoesNotCrossRouteMessages()
    {
        // Regression guard for #472:
        // Sending to conv-A then conv-B must not stamp the session with conv-B's ID,
        // which would cause subsequent implicit-route messages to land in conv-B.
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        using var client = factory.CreateClient();

        // Create two conversations
        var convAResp = await client.PostAsJsonAsync("/api/conversations",
            new { agentId = TestAgentId, title = "Conversation A" }, cts.Token);
        convAResp.EnsureSuccessStatusCode();
        var convAId = JsonDocument.Parse(await convAResp.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("conversationId").GetString()!;

        var convBResp = await client.PostAsJsonAsync("/api/conversations",
            new { agentId = TestAgentId, title = "Conversation B" }, cts.Token);
        convBResp.EnsureSuccessStatusCode();
        var convBId = JsonDocument.Parse(await convBResp.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("conversationId").GetString()!;

        await using var connection = await CreateStartedConnection(factory, cts.Token);

        // Send to conv-A first
        await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "message to A", convAId, cts.Token);

        // Switch: send to conv-B
        await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "message to B", convBId, cts.Token);

        // Both messages must carry their respective conversationIds
        dispatcher.Messages.Count.ShouldBe(2);
        dispatcher.Messages[0].ConversationId.ShouldBe(convAId,
            "first message should be routed to conv-A");
        dispatcher.Messages[1].ConversationId.ShouldBe(convBId,
            "second message should be routed to conv-B, not conv-A");

        // Conv-A bindings must not have been polluted with conv-B's channel address
        var convStore = factory.Services.GetRequiredService<IConversationStore>();
        var convA = await convStore.GetAsync(ConversationId.From(convAId), cts.Token);
        convA.ShouldNotBeNull();
        var convB = await convStore.GetAsync(ConversationId.From(convBId), cts.Token);
        convB.ShouldNotBeNull();

        // Both conversations should have at most one binding each (not cross-contaminated)
        convA!.ChannelBindings.Count.ShouldBeLessThanOrEqualTo(1,
            "conv-A must not accumulate bindings from other conversations");
        convB!.ChannelBindings.Count.ShouldBeLessThanOrEqualTo(1,
            "conv-B must not accumulate bindings from other conversations");
    }

    [Fact]
    public async Task Hub_ImplicitRouting_AfterSwitch_DoesNotRouteToOldConversation()
    {
        // Regression guard for #472:
        // A REST-created conversation opened via explicit conversationId must NOT
        // accumulate a channel binding. Without the fix, bind-on-first-use would add
        // a signalr binding to conv-B, which would then intercept subsequent implicit
        // reconnects that should route to the default conversation.
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        using var client = factory.CreateClient();

        // Create conv-B via REST (no channel binding)
        var convBResp = await client.PostAsJsonAsync("/api/conversations",
            new { agentId = TestAgentId, title = "Explicit Conv B" }, cts.Token);
        convBResp.EnsureSuccessStatusCode();
        var convBId = JsonDocument.Parse(await convBResp.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("conversationId").GetString()!;

        await using var connection = await CreateStartedConnection(factory, cts.Token);

        // Send to conv-B via explicit conversationId
        await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "explicit to B", convBId, cts.Token);

        // conv-B must NOT have acquired a channel binding from the explicit path
        var convStore = factory.Services.GetRequiredService<IConversationStore>();
        var convB = await convStore.GetAsync(ConversationId.From(convBId), cts.Token);
        convB.ShouldNotBeNull(
            "conv-B must exist in the store after being opened via explicit conversationId");
        convB!.ChannelBindings.ShouldBeEmpty(
            "explicit conversationId path must not add bindings -- would intercept future implicit reconnects");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
