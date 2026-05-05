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
/// Tests verifying that GatewayHub correctly passes conversationId as ThreadId on
/// the dispatched InboundMessage, enabling the conversation router to route to the
/// correct conversation rather than always falling back to the default portal conversation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class SignalRThreadRoutingTests : IAsyncDisposable
{
    private const string TestAgentId = "thread-routing-agent";

    /// <summary>
    /// When SendMessageToConversation is called with a specific conversationId, the
    /// dispatched InboundMessage.ThreadId must equal that conversationId so the router
    /// can distinguish secondary conversations from the default one.
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
            "SendMessageToConversation", TestAgentId, "signalr", "message to alpha", conv1Id, cts.Token);
        var sessionId1 = result1.GetProperty("sessionId").GetString()!;

        // Send to second conversation
        var result2 = await connection.InvokeAsync<JsonElement>(
            "SendMessageToConversation", TestAgentId, "signalr", "message to beta", conv2Id, cts.Token);
        var sessionId2 = result2.GetProperty("sessionId").GetString()!;

        // The sessions should be different — each conversation has its own session
        sessionId1.ShouldNotBe(sessionId2,
            "messages to different conversations should route to different sessions");

        // The dispatched messages should carry the correct threadId
        dispatcher.Messages.Count.ShouldBe(2);

        var msg1 = dispatcher.Messages.FirstOrDefault(m => m.Content == "message to alpha");
        var msg2 = dispatcher.Messages.FirstOrDefault(m => m.Content == "message to beta");

        msg1.ShouldNotBeNull();
        msg2.ShouldNotBeNull();

        msg1!.ThreadId.ShouldBe(conv1Id,
            "InboundMessage.ThreadId for conv1 message should be the conversationId");
        msg2!.ThreadId.ShouldBe(conv2Id,
            "InboundMessage.ThreadId for conv2 message should be the conversationId");
    }

    /// <summary>
    /// When SendMessage (no conversationId) is called, the dispatched InboundMessage.ThreadId
    /// must be null so the router resolves the default (signalr, agentId, null) conversation.
    /// </summary>
    [Fact]
    public async Task SignalRHub_SendMessage_DefaultConversation_UsesNullThread()
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

        await connection.InvokeAsync<JsonElement>("SendMessage", TestAgentId, "signalr", "default hello", cts.Token);

        var dispatched = dispatcher.Messages.ShouldHaveSingleItem();
        dispatched.ThreadId.ShouldBeNull(
            "SendMessage without conversationId must dispatch with ThreadId=null to target the default portal conversation");
        dispatched.ChannelAddress.ShouldBe(TestAgentId);
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
            "SendMessage", TestAgentId, "signalr", "default message", cts.Token);
        var defaultSessionId = defaultResult.GetProperty("sessionId").GetString()!;

        // Create a new conversation via the API
        var newConvId = await CreateConversationAsync(client, "Brand New Conversation", cts.Token);

        // Send to the new conversation
        var newResult = await connection.InvokeAsync<JsonElement>(
            "SendMessageToConversation", TestAgentId, "signalr", "new conv message", newConvId, cts.Token);
        var newSessionId = newResult.GetProperty("sessionId").GetString()!;

        newSessionId.ShouldNotBe(defaultSessionId,
            "a new conversation created via API must get its own distinct session, separate from the default portal conversation session");

        // And the dispatched message for the new conversation should carry the conversationId as threadId
        var newMsg = dispatcher.Messages.LastOrDefault();
        newMsg.ShouldNotBeNull();
        newMsg!.ThreadId.ShouldBe(newConvId,
            "the new conversation's message should be dispatched with ThreadId=conversationId");
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
