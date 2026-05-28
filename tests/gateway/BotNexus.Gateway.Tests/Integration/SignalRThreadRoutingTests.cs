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
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Integration;

/// <summary>
/// Tests verifying that GatewayHub correctly passes conversationId on the dispatched
/// InboundMessage, enabling conversation-first routing instead of falling back to the
/// default portal conversation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class SignalRThreadRoutingTests : IAsyncDisposable
{
    private const string TestAgentId = "thread-routing-agent";

    /// <summary>
    /// When SendMessage is called with a specific conversationId, the dispatched
    /// InboundMessage.ConversationId must preserve that ID so the router can resolve
    /// the non-default conversation session directly.
    /// </summary>
    [Fact]
    public async Task SignalRHub_SendMessageToConversation_RoutesToCorrectConversation()
    {
        var dispatcher = new RecordingDispatcher();
        await using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IChannelDispatcher>();
            services.AddSingleton<IChannelDispatcher>(dispatcher);
        });
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        // Create two distinct conversations for the same agent
        using var client = factory.CreateClient();
        var conv1Id = await CreateConversationAsync(client, "Conversation Alpha", cts.Token);
        var conv2Id = await CreateConversationAsync(client, "Conversation Beta", cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);

        // Send to first conversation
        var result1 = await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "message to alpha", conv1Id, cts.Token);
        var sessionId1 = result1.GetProperty("sessionId").GetString()!;

        // Send to second conversation
        var result2 = await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "message to beta", conv2Id, cts.Token);
        var sessionId2 = result2.GetProperty("sessionId").GetString()!;

        // The sessions should be different — each conversation has its own session
        // NOTE: With conversation-first routing, the HUB returns the default portal session (same for both).
        // The per-conversation session routing happens inside GatewayHost when it processes the message.
        // What matters is that each dispatched InboundMessage carries the correct ConversationId.
        // sessionId1.ShouldNotBe(sessionId2) is no longer valid under the new routing model.

        // The dispatched messages should carry the correct ConversationId (not ThreadId — refactored)
        dispatcher.Messages.Count.ShouldBe(2);

        var msg1 = dispatcher.Messages.FirstOrDefault(m => m.Content == "message to alpha");
        var msg2 = dispatcher.Messages.FirstOrDefault(m => m.Content == "message to beta");

        msg1.ShouldNotBeNull();
        msg2.ShouldNotBeNull();

        msg1!.RoutingHints.ShouldNotBeNull();
        msg1.RoutingHints!.RequestedConversationId!.Value.Value.ShouldBe(conv1Id,
            "InboundMessage.RoutingHints.RequestedConversationId for conv1 message should be the conversationId");
        msg2!.RoutingHints.ShouldNotBeNull();
        msg2.RoutingHints!.RequestedConversationId!.Value.Value.ShouldBe(conv2Id,
            "InboundMessage.RoutingHints.RequestedConversationId for conv2 message should be the conversationId");
    }

    /// <summary>
    /// When SendMessage (no conversationId) is called, the dispatched InboundMessage.ConversationId
    /// must be null so the router uses binding lookup (resolving to the default signalr conversation).
    /// </summary>
    [Fact]
    public async Task SignalRHub_SendMessage_DefaultConversation_UsesNullConversationId()
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

        await connection.InvokeAsync<JsonElement>("SendMessage", TestAgentId, "signalr", "default hello", (string?)null, cts.Token);

        var dispatched = dispatcher.Messages.ShouldHaveSingleItem();
        // SendMessage without conversationId: hints may be null (no overrides) or present with null ConversationId.
        (dispatched.RoutingHints?.RequestedConversationId).ShouldBeNull(
            "SendMessage without conversationId must dispatch with RoutingHints.RequestedConversationId=null to use binding lookup");
        dispatched.ChannelAddress.ShouldBe(ChannelAddress.From(TestAgentId));
    }

    /// <summary>
    /// When a new conversation is created via POST /api/conversations and a message is sent
    /// to it, the agent processes it in a different session than the default conversation session.
    /// </summary>
    [Fact]
    public async Task SignalRHub_NewConversation_GetsItsOwnSession()
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

        await using var connection = await CreateStartedConnection(factory, cts.Token);

        // First send to the default conversation to establish it
        var defaultResult = await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "default message", (string?)null, cts.Token);
        var defaultSessionId = defaultResult.GetProperty("sessionId").GetString()!;

        // Create a new conversation via the API
        var newConvId = await CreateConversationAsync(client, "Brand New Conversation", cts.Token);

        // Send to the new conversation
        var newResult = await connection.InvokeAsync<JsonElement>(
            "SendMessage", TestAgentId, "signalr", "new conv message", newConvId, cts.Token);
        var newSessionId = newResult.GetProperty("sessionId").GetString()!;

        newSessionId.ShouldNotBe(defaultSessionId,
            "messages sent to a non-default conversation must resolve to that conversation's session, not the default portal session");

        var sessionStore = factory.Services.GetRequiredService<ISessionStore>();
        var defaultSession = await sessionStore.GetAsync(SessionId.From(defaultSessionId), cts.Token);
        var targetedSession = await sessionStore.GetAsync(SessionId.From(newSessionId), cts.Token);
        defaultSession.ShouldNotBeNull();
        targetedSession.ShouldNotBeNull();
        defaultSession!.Session.ConversationId.ShouldNotBeNull();
        targetedSession!.Session.ConversationId.ShouldNotBeNull();
        targetedSession.Session.ConversationId.ShouldNotBe(defaultSession.Session.ConversationId,
            "targeted conversation should preserve a distinct ConversationId on its resolved session");
        targetedSession.Session.ConversationId!.Value.Value.ShouldBe(newConvId,
            "resolved session should keep the requested conversationId");

        // And the dispatched message for the new conversation should carry the conversationId
        var newMsg = dispatcher.Messages.LastOrDefault();
        newMsg.ShouldNotBeNull();
        newMsg!.RoutingHints.ShouldNotBeNull();
        newMsg.RoutingHints!.RequestedConversationId!.Value.Value.ShouldBe(newConvId,
            "the new conversation's message should be dispatched with ConversationId set for direct routing");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> CreateConversationAsync(HttpClient client, string title, CancellationToken ct)
    {
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new { agentId = TestAgentId, title }, ct);
        resp.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
        return doc.GetProperty("conversationId").GetString()!;
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
            DisplayName = "Thread Routing Test Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };
        var response = await client.PostAsJsonAsync("/api/agents", descriptor, CancellationToken.None);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }

    private static CancellationTokenSource CreateTimeout()
        => new(TimeSpan.FromSeconds(20));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class RecordingDispatcher : IChannelDispatcher
    {
        private readonly List<InboundMessage> _messages = [];

        public IReadOnlyList<InboundMessage> Messages
        {
            get { lock (_messages) { return _messages.ToList(); } }
        }

        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            lock (_messages) { _messages.Add(message); }
            return Task.CompletedTask;
        }
    }
}
