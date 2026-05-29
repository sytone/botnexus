using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Scenarios.Harness;

/// <summary>
/// In-process gateway harness for end-to-end citizen scenarios. Boots the production
/// <c>AddBotNexusGateway()</c> service graph against an isolated temp <c>BotNexusHome</c>,
/// substitutes the LLM round-trip with <see cref="ScenarioFakeApiProvider"/>, and exposes
/// a single channel — <see cref="VirtualChannelAdapter"/> — so scenarios drive the
/// platform exactly as a real channel would, but deterministically and with zero
/// network IO.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="StartAsync(VirtualWorldOptions?, CancellationToken)"/> to construct an
/// instance. The world is fully isolated: every instance has its own temp home, its own
/// in-memory stores, its own fake provider, and its own virtual channel adapter — so
/// tests can run in parallel without contamination.
/// </para>
/// <para>
/// The world's public surface is intentionally narrow: scenarios call typed verbs
/// (<see cref="GivenAgentAsync"/>, <see cref="WhenSendsAsync"/>,
/// <see cref="WaitForOutboundAsync"/>) and never reach into DI. This is enforced by the
/// <c>ScenarioHarness_PublicSurface_DoesNotLeakDiPrimitives</c> architecture rule.
/// </para>
/// </remarks>
public sealed class VirtualWorld : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly VirtualChannelAdapter _adapter;
    private readonly ScenarioFakeApiProvider _provider;
    private readonly string _tempHomePath;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IConversationStore _conversationStore;
    private readonly ISessionStore _sessionStore;
    private readonly IConversationResetService _resetService;
    private readonly TimeSpan _defaultOutboundWaitTimeout;

    private VirtualWorld(
        IHost host,
        VirtualChannelAdapter adapter,
        ScenarioFakeApiProvider provider,
        string tempHomePath,
        IAgentRegistry agentRegistry,
        IConversationStore conversationStore,
        ISessionStore sessionStore,
        IConversationResetService resetService,
        TimeSpan defaultOutboundWaitTimeout)
    {
        _host = host;
        _adapter = adapter;
        _provider = provider;
        _tempHomePath = tempHomePath;
        _agentRegistry = agentRegistry;
        _conversationStore = conversationStore;
        _sessionStore = sessionStore;
        _resetService = resetService;
        _defaultOutboundWaitTimeout = defaultOutboundWaitTimeout;
    }

    /// <summary>
    /// The virtual channel adapter every scenario drives through. Capability flags
    /// (streaming / steering / etc.) are baked at <see cref="StartAsync"/> time via
    /// <see cref="VirtualWorldOptions.ChannelOptions"/>.
    /// </summary>
    public VirtualChannelAdapter Adapter => _adapter;

    /// <summary>The fake provider — exposes turn count and the canned response factory.</summary>
    public ScenarioFakeApiProvider Provider => _provider;

    /// <summary>
    /// Bootstraps a fully wired in-process gateway, registers the virtual channel + fake
    /// provider, waits for the channel adapter to start, and returns a ready-to-use world.
    /// </summary>
    public static async Task<VirtualWorld> StartAsync(
        VirtualWorldOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new VirtualWorldOptions();
        var tempHomePath = Path.Combine(
            Path.GetTempPath(),
            "botnexus-vworld",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHomePath);

        var adapter = new VirtualChannelAdapter(options.ChannelOptions);
        var provider = options.ResponseFactory is null
            ? new ScenarioFakeApiProvider()
            : new ScenarioFakeApiProvider(options.ResponseFactory);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.IncludeScopes = false; });
        builder.Logging.SetMinimumLevel(options.LogLevel);

        var services = builder.Services;

        // Substitute BotNexusHome to a fresh temp directory so no scenario writes into
        // the real ~/.botnexus and parallel scenarios don't see each other's files.
        services.AddSingleton(new BotNexusHome(tempHomePath));

        // PlatformConfig is consumed by GatewayAuthManager + isolation strategies. Register a
        // default-shaped options monitor (no provider configuration) so DI resolves cleanly
        // without reaching for a real config.json on disk.
        services.AddOptions<PlatformConfig>();

        // GatewayAuthManager is registered by AddPlatformConfiguration in production, NOT by
        // AddBotNexusGateway. Scenarios don't call AddPlatformConfiguration (it touches the
        // real ~/.botnexus/config.json), so we must register it ourselves before the gateway
        // wires up InProcessIsolationStrategy which depends on it.
        services.AddSingleton<GatewayAuthManager>();

        // Empty in-memory agent config source so AddBotNexusGateway's default file-based
        // source is skipped. Scenarios register agents programmatically via GivenAgentAsync.
        services.AddSingleton<IAgentConfigurationSource, EmptyAgentConfigurationSource>();

        services.AddBotNexusGateway();

        // The single virtual channel — register the concrete type AND the IChannelAdapter
        // contract that ChannelManager scans for.
        services.AddSingleton(adapter);
        services.AddSingleton<IChannelAdapter>(adapter);

        // LLM stack — gateway does NOT auto-register these (Program.cs does in production).
        services.AddSingleton<ApiProviderRegistry>();
        services.AddSingleton<ModelRegistry>();
        services.AddSingleton<LlmClient>(sp =>
        {
            var apis = sp.GetRequiredService<ApiProviderRegistry>();
            var models = sp.GetRequiredService<ModelRegistry>();
            provider.Register(apis, models);
            return new LlmClient(apis, models);
        });

        // Strip ALL background hosted services and re-add only GatewayHost — it owns the
        // channel-adapter startup loop and the dispatch pipeline that scenarios drive. The
        // rest (UpdateCheckService, MemoryIndexer, SessionWarmupService, SessionCleanupService,
        // AgentConfigurationHostedService) are noise that slows startup and drags in extra
        // dependencies (HttpClient, file watchers) that scenarios don't exercise.
        var hostedDescriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .ToList();
        foreach (var descriptor in hostedDescriptors)
            services.Remove(descriptor);
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BotNexus.Gateway.GatewayHost>());

        var host = builder.Build();
        await host.StartAsync(cancellationToken);

        // Force LlmClient construction so the fake provider/model are registered before
        // the first inbound triggers an agent supervisor lookup.
        _ = host.Services.GetRequiredService<LlmClient>();

        // Wait for the channel adapter to be started by GatewayHost.ExecuteAsync.
        await WaitForAdapterStartAsync(adapter, TimeSpan.FromSeconds(10), cancellationToken);

        return new VirtualWorld(
            host,
            adapter,
            provider,
            tempHomePath,
            host.Services.GetRequiredService<IAgentRegistry>(),
            host.Services.GetRequiredService<IConversationStore>(),
            host.Services.GetRequiredService<ISessionStore>(),
            host.Services.GetRequiredService<IConversationResetService>(),
            options.DefaultOutboundWaitTimeout);
    }

    /// <summary>
    /// Registers an agent that responds via <see cref="ScenarioFakeApiProvider"/>. The
    /// agent runs in-process and is started lazily when the first inbound hits it.
    /// </summary>
    public Task<RegisteredAgent> GivenAgentAsync(
        string agentId,
        string? systemPrompt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = ScenarioFakeApiProvider.ModelId,
            ApiProvider = ScenarioFakeApiProvider.ProviderName,
            SystemPrompt = systemPrompt,
            IsolationStrategy = "in-process",
        };
        _agentRegistry.Register(descriptor);
        return Task.FromResult(new RegisteredAgent(descriptor.AgentId.Value, descriptor.DisplayName));
    }

    /// <summary>
    /// Drives a citizen-originated inbound through the virtual channel exactly as a real
    /// channel would. The router resolves the conversation, the supervisor starts the
    /// agent if needed, the fake provider produces a reply, and the outbound lands back
    /// on the virtual channel (observable via <see cref="WaitForOutboundAsync"/>).
    /// </summary>
    /// <param name="fromUser">The originating user — also used as the channel address (one address per user by default).</param>
    /// <param name="toAgent">The target agent id (must have been registered via <see cref="GivenAgentAsync"/>).</param>
    /// <param name="content">The user message.</param>
    /// <param name="channelAddress">Optional explicit channel address — defaults to the user id (one conversation per user).</param>
    /// <param name="conversationId">Optional explicit conversation id for the conversation-first routing path.</param>
    public async Task<DispatchedInbound> WhenSendsAsync(
        string fromUser,
        string toAgent,
        string content,
        string? channelAddress = null,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(toAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var address = string.IsNullOrWhiteSpace(channelAddress) ? fromUser : channelAddress;
        var inbound = new InboundMessage
        {
            ChannelType = ChannelKey.From(VirtualChannelAdapter.VirtualChannelType),
            SenderId = fromUser,
            Sender = CitizenId.Of(UserId.From(fromUser)),
            ChannelAddress = ChannelAddress.From(address),
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(toAgent, null, conversationId),
            Content = content,
        };
        await _adapter.SimulateInboundAsync(inbound, cancellationToken);
        return new DispatchedInbound(
            FromUser: fromUser,
            ToAgent: toAgent,
            Content: content,
            ChannelAddress: address,
            ConversationId: conversationId);
    }

    /// <summary>
    /// Blocks until an outbound message matching <paramref name="predicate"/> is observed
    /// or <paramref name="timeout"/> elapses. Useful for "the agent replied" assertions
    /// where the reply is async to the inbound dispatch.
    /// </summary>
    public async Task<OutboundMessage> WaitForOutboundAsync(
        Func<OutboundMessage, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return await _adapter.WaitForOutboundAsync(
            predicate,
            timeout ?? _defaultOutboundWaitTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Convenience overload: waits for any outbound message addressed to a specific channel
    /// address (i.e. delivered back to a specific virtual citizen).
    /// </summary>
    public Task<OutboundMessage> WaitForOutboundToAsync(
        string channelAddress,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => WaitForOutboundAsync(
            m => string.Equals(m.ChannelAddress.Value, channelAddress, StringComparison.Ordinal),
            timeout,
            cancellationToken);

    /// <summary>
    /// Channel-agnostic reply assertion: waits for the agent's response on
    /// <paramref name="channelAddress"/> regardless of whether the channel delivers
    /// the reply via <see cref="IChannelAdapter.SendAsync(OutboundMessage, CancellationToken)"/>
    /// (non-streaming) or via stream events (streaming). Returns the assembled reply text
    /// + the delivery mechanism. Scenarios should prefer this over
    /// <see cref="WaitForOutboundAsync"/> when they don't care how the reply got there.
    /// </summary>
    /// <param name="channelAddress">The channel address the reply is addressed to.</param>
    /// <param name="timeout">Optional override for the default wait timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The accumulated reply content + the delivery channel (<c>outbound</c> or <c>stream</c>).</returns>
    /// <exception cref="TimeoutException">Thrown when no completed reply is observed before the timeout.</exception>
    public async Task<AgentReply> WaitForReplyAsync(
        string channelAddress,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelAddress);
        var deadline = DateTimeOffset.UtcNow + (timeout ?? _defaultOutboundWaitTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Non-streaming delivery — completed OutboundMessage on the channel address.
            var outbound = _adapter.Outbound.FirstOrDefault(m =>
                string.Equals(m.ChannelAddress.Value, channelAddress, StringComparison.Ordinal));
            if (outbound is not null)
                return new AgentReply(outbound.Content, channelAddress, AgentReplyDelivery.Outbound);

            // Streaming delivery — the channel saw TurnEnd or MessageEnd for this address,
            // meaning the agent has emitted a complete reply over the stream.
            if (_adapter.StreamEvents.TryGetValue(channelAddress, out var events) && events.Count > 0)
            {
                var ended = events.Any(e =>
                    e.Type == AgentStreamEventType.TurnEnd
                    || e.Type == AgentStreamEventType.MessageEnd);
                if (ended)
                {
                    var content = string.Concat(events
                        .Where(e => e.Type == AgentStreamEventType.ContentDelta && e.ContentDelta is not null)
                        .Select(e => e.ContentDelta));

                    // Fall back to raw deltas if the channel only saw legacy delta events.
                    if (string.IsNullOrEmpty(content)
                        && _adapter.StreamDeltas.TryGetValue(channelAddress, out var deltas))
                    {
                        content = string.Concat(deltas);
                    }

                    return new AgentReply(content, channelAddress, AgentReplyDelivery.Stream);
                }
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException(
            $"No agent reply observed on channel address '{channelAddress}' within " +
            $"{(timeout ?? _defaultOutboundWaitTimeout).TotalMilliseconds:F0}ms " +
            $"(outbound={_adapter.Outbound.Count}, streamEvents={(_adapter.StreamEvents.TryGetValue(channelAddress, out var e) ? e.Count : 0)}, " +
            $"providerTurns={_provider.TurnCount}).");
    }

    /// <summary>
    /// Returns a read-only snapshot view of the conversation for assertion purposes.
    /// </summary>
    public async Task<ConversationView?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var conversation = await _conversationStore.GetAsync(ConversationId.From(conversationId), cancellationToken);
        return conversation is null ? null : ToView(conversation);
    }

    /// <summary>
    /// Returns the full list of conversations owned by an agent (regardless of channel binding).
    /// </summary>
    public async Task<IReadOnlyList<ConversationView>> ListConversationsForAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var conversations = await _conversationStore.ListAsync(AgentId.From(agentId), cancellationToken);
        return [.. conversations.Select(ToView)];
    }

    /// <summary>
    /// Returns a read-only snapshot view of the session for assertion purposes.
    /// </summary>
    public async Task<SessionView?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var snapshot = await _sessionStore.GetAsync(SessionId.From(sessionId), cancellationToken);
        if (snapshot is null) return null;
        return new SessionView(
            SessionId: snapshot.Session.SessionId.Value,
            AgentId: snapshot.AgentId.Value,
            ConversationId: snapshot.Session.ConversationId.IsInitialized() ? snapshot.Session.ConversationId.Value : null,
            Status: snapshot.Session.Status.ToString(),
            HistoryCount: snapshot.Session.History.Count);
    }

    /// <summary>
    /// Drives the canonical reset flow for an active session: stops the agent, flushes
    /// memory, cancels any pending ask-user, seals the old session, and clears the
    /// conversation's <c>ActiveSessionId</c> so the next inbound creates a fresh session.
    /// </summary>
    public Task ResetSessionAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return _resetService.ResetActiveSessionAsync(ConversationId.From(conversationId), cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort shutdown.
        }
        _host.Dispose();
        try
        {
            if (Directory.Exists(_tempHomePath))
                Directory.Delete(_tempHomePath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; tests should not fail on temp dir cleanup races.
        }
    }

    private static async Task WaitForAdapterStartAsync(VirtualChannelAdapter adapter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (adapter.IsRunning) return;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
        throw new TimeoutException(
            $"VirtualChannelAdapter did not start within {timeout.TotalSeconds:F0}s. " +
            "Likely the gateway's BackgroundService failed to start its channel adapters.");
    }

    private static ConversationView ToView(Conversation conversation)
        => new(
            ConversationId: conversation.ConversationId.Value,
            AgentId: conversation.AgentId.Value,
            Title: conversation.Title,
            ActiveSessionId: conversation.ActiveSessionId?.Value,
            ChannelBindings: [.. conversation.ChannelBindings.Select(b => new ChannelBindingView(b.ChannelType.Value, b.ChannelAddress.Value))],
            Status: conversation.Status.ToString(),
            InitiatorId: conversation.Initiator?.ToString());

    private sealed class EmptyAgentConfigurationSource : IAgentConfigurationSource
    {
        public Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentDescriptor>>([]);

        public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged) => null;
    }
}

/// <summary>Read-only view of a registered agent — exposed to scenarios so they can refer back to the id without going through DI.</summary>
public sealed record RegisteredAgent(string AgentId, string DisplayName);

/// <summary>Read-only record of an inbound that was just dispatched — useful for chained assertions.</summary>
public sealed record DispatchedInbound(string FromUser, string ToAgent, string Content, string ChannelAddress, string? ConversationId);

/// <summary>Read-only view of a conversation — minimum surface for assertion-style scenarios.</summary>
public sealed record ConversationView(
    string ConversationId,
    string AgentId,
    string? Title,
    string? ActiveSessionId,
    IReadOnlyList<ChannelBindingView> ChannelBindings,
    string Status,
    string? InitiatorId);

/// <summary>Read-only view of a single channel binding on a conversation.</summary>
public sealed record ChannelBindingView(string ChannelType, string ChannelAddress);

/// <summary>Read-only view of a session — exposes the fields scenarios assert against.</summary>
public sealed record SessionView(
    string SessionId,
    string AgentId,
    string? ConversationId,
    string Status,
    int HistoryCount);

/// <summary>
/// How the gateway delivered the agent's reply back to the channel. Streaming channels
/// receive deltas + a terminating event; non-streaming channels receive a single
/// completed <c>OutboundMessage</c>. Scenarios that just want to assert on the reply
/// content shouldn't care which path was used — see
/// <see cref="VirtualWorld.WaitForReplyAsync"/>.
/// </summary>
public enum AgentReplyDelivery
{
    /// <summary>The reply landed via <see cref="IChannelAdapter.SendAsync(OutboundMessage, CancellationToken)"/> as one completed message.</summary>
    Outbound,

    /// <summary>The reply was assembled from streaming events / deltas captured on the channel.</summary>
    Stream
}

/// <summary>
/// The agent's reply as observed by the virtual channel, regardless of delivery path.
/// Returned by <see cref="VirtualWorld.WaitForReplyAsync"/>.
/// </summary>
/// <param name="Content">The accumulated reply text the citizen would see.</param>
/// <param name="ChannelAddress">The address the reply was delivered to.</param>
/// <param name="Delivery">Whether the reply came via the streaming or non-streaming path.</param>
public sealed record AgentReply(string Content, string ChannelAddress, AgentReplyDelivery Delivery);
