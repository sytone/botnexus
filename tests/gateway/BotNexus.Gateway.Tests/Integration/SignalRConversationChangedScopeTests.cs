using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
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
/// Observable end-to-end coverage for the scoped <c>ConversationChanged</c> fan-out (#2541 AC3).
/// </summary>
/// <remarks>
/// The unit-level notifier tests pin WHICH addressing method is chosen; this pins the property the
/// acceptance criterion actually states: a client subscribed to agent A receives no notification
/// for activity on agent B. That distinction matters because the notifier could be scoped correctly
/// and still be undeliverable if no connection ever joins the corresponding group — the hub verb
/// and the notifier have to agree on the group key, and only a live round-trip proves they do.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class SignalRConversationChangedScopeTests : IAsyncDisposable
{
    private const string AgentA = "scope-agent-a";
    private const string AgentB = "scope-agent-b";

    /// <summary>
    /// Happy path: a client that subscribed to agent A DOES receive agent A's change. Without this,
    /// the negative test below would pass just as well against a notifier that delivers to nobody.
    /// </summary>
    [Fact]
    public async Task ConversationChanged_ReachesAClientSubscribedToThatAgent()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, AgentA, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SubscribeAgents", new[] { AgentA }, cts.Token);

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<JsonElement>("ConversationChanged", payload => received.TrySetResult(payload));

        var notifier = factory.Services.GetRequiredService<IConversationChangeNotifier>();
        await notifier.NotifyConversationChangedAsync("created", AgentA, "conv-a", cts.Token);

        var payload = await received.Task.WaitAsync(cts.Token);
        payload.GetProperty("agentId").GetString().ShouldBe(AgentA);
        payload.GetProperty("conversationId").GetString().ShouldBe("conv-a");
    }

    /// <summary>
    /// The #2541 AC3 assertion: a client subscribed to agent A receives NO notification for
    /// activity on agent B. Before the fix the notifier used <c>Clients.All</c>, so this client
    /// woke up and re-fetched its conversation list on entirely unrelated activity.
    /// </summary>
    /// <remarks>
    /// The agent-A notification is sent AFTER the agent-B one on the same connection and awaited as
    /// a barrier. That makes the negative assertion sound without a sleep: SignalR preserves
    /// per-connection ordering, so once the later A event has arrived, the earlier B event has
    /// definitively been delivered-or-dropped. A bare timeout would only prove B was slow.
    /// </remarks>
    [Fact]
    public async Task ConversationChanged_ForAnotherAgent_DoesNotReachThisClient()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, AgentA, cts.Token);
        await RegisterAgentAsync(factory, AgentB, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("SubscribeAgents", new[] { AgentA }, cts.Token);

        var foreignAgentIds = new List<string>();
        var barrier = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<JsonElement>("ConversationChanged", payload =>
        {
            var agentId = payload.GetProperty("agentId").GetString();
            if (agentId == AgentA)
                barrier.TrySetResult(payload);
            else
                foreignAgentIds.Add(agentId ?? "<null>");
        });

        var notifier = factory.Services.GetRequiredService<IConversationChangeNotifier>();
        await notifier.NotifyConversationChangedAsync("created", AgentB, "conv-b", cts.Token);
        await notifier.NotifyConversationChangedAsync("created", AgentA, "conv-a", cts.Token);

        await barrier.Task.WaitAsync(cts.Token);

        foreignAgentIds.ShouldBeEmpty(
            "a client subscribed to agent A must receive no ConversationChanged for agent B (#2541 AC3); "
            + "an unscoped broadcast makes every client re-fetch on unrelated activity");
    }

    /// <summary>
    /// Sad path: a connection that never called <c>SubscribeAgents</c> is in no agent group and so
    /// receives nothing. Pins that delivery follows the subscription rather than the connection.
    /// </summary>
    [Fact]
    public async Task ConversationChanged_DoesNotReachAConnectionThatNeverSubscribed()
    {
        await using var factory = CreateTestFactory();
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, AgentA, cts.Token);

        await using var subscriber = await CreateStartedConnection(factory, cts.Token);
        await using var bystander = await CreateStartedConnection(factory, cts.Token);
        await subscriber.InvokeAsync("SubscribeAgents", new[] { AgentA }, cts.Token);

        var bystanderEvents = new List<string>();
        var subscriberBarrier = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = bystander.On<JsonElement>("ConversationChanged", payload =>
            bystanderEvents.Add(payload.GetProperty("agentId").GetString() ?? "<null>"));
        using var __ = subscriber.On<JsonElement>("ConversationChanged", payload => subscriberBarrier.TrySetResult(payload));

        var notifier = factory.Services.GetRequiredService<IConversationChangeNotifier>();
        await notifier.NotifyConversationChangedAsync("created", AgentA, "conv-a", cts.Token);

        // The subscriber receiving it proves the event was actually emitted, so the bystander's
        // empty list is a real negative rather than "nothing was sent at all".
        await subscriberBarrier.Task.WaitAsync(cts.Token);
        bystanderEvents.ShouldBeEmpty("only connections that subscribed to the agent may receive its notifications (#2541 AC3)");
    }

    private static WebApplicationFactory<Program> CreateTestFactory()
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureServices(services =>
                {
                    var hostedServices = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
                    foreach (var descriptor in hostedServices)
                        services.Remove(descriptor);

                    services.RemoveAll<IAgentConfigurationWriter>();
                    services.AddSingleton<IAgentConfigurationWriter, NoOpAgentConfigurationWriter>();

                    services.AddSignalRChannelForTests();

                    services.Replace(ServiceDescriptor.Singleton<ISessionStore, InMemorySessionStore>());
                    services.Replace(ServiceDescriptor.Singleton<IConversationStore, InMemoryConversationStore>());
                });
            });

    private static HubConnection CreateHubConnection(WebApplicationFactory<Program> factory)
    {
        var handler = factory.Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/hub/gateway", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    private static async Task<HubConnection> CreateStartedConnection(
        WebApplicationFactory<Program> factory,
        CancellationToken cancellationToken,
        [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var connection = CreateHubConnection(factory);
        await HubFixtureGuard.StartGuardedAsync(connection, "SignalRConversationChangedScopeTests", cancellationToken, testName: testName);
        return connection;
    }

    private static async Task RegisterAgentAsync(WebApplicationFactory<Program> factory, string agentId, CancellationToken cancellationToken)
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

    private static CancellationTokenSource CreateTimeout() => new(TimeSpan.FromSeconds(15));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
