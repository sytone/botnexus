using System.Collections.Immutable;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Extensions.Channels.Test.Tests;

/// <summary>
/// Minimal scenario DSL for conversation-event tests that need multiple real channel projections.
/// It composes the production publisher and production test-channel adapters; only their external
/// transports are replaced by the adapters' in-memory recorders.
/// </summary>
internal sealed class TestChannelConversationScenario : IAsyncDisposable
{
    private readonly Dictionary<string, TestChannelAdapter> _adapters;
    private readonly List<ScenarioBinding> _bindings = [];
    private readonly ConversationEventPublisher _publisher;
    private readonly AgentId _agentId = AgentId.From("probe");
    private readonly SessionId _sessionId = SessionId.Create();

    public TestChannelConversationScenario(params ChannelDefinition[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        _adapters = channels.ToDictionary(
            channel => channel.ChannelId,
            channel => new TestChannelAdapter(
                NullLogger<TestChannelAdapter>.Instance,
                Options.Create(new TestChannelOptions { ChannelId = channel.ChannelId })),
            StringComparer.Ordinal);

        _publisher = new ConversationEventPublisher(_adapters.Values);
        ConversationId = ConversationId.Create();
    }

    public ConversationId ConversationId { get; }

    public static ChannelDefinition Channel(string channelId) => new(channelId);

    public BindingId Bind(
        string channelId,
        string address,
        BindingMode mode = BindingMode.Interactive,
        ConversationId? conversationId = null)
    {
        if (!_adapters.ContainsKey(channelId))
            throw new ArgumentException($"Scenario channel '{channelId}' is not registered.", nameof(channelId));

        var bindingId = BindingId.Create();
        _bindings.Add(new ScenarioBinding(
            conversationId ?? ConversationId,
            new ConversationBindingSnapshot(
                bindingId,
                ChannelKey.From(channelId),
                AdapterId: null,
                ChannelAddress.From(address),
                mode,
                ThreadingMode.Single)));
        return bindingId;
    }

    public async Task<SessionEntry> PublishCompactionAsync()
    {
        var (conversations, sessions, session) = await CreatePersistedConversationAsync();

        var coordinator = new SessionCompactionCoordinator(
            Substitute.For<ISessionCompactor>(),
            sessions,
            Substitute.For<IAgentSupervisor>(),
            _publisher,
            conversations,
            new OptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<SessionCompactionCoordinator>.Instance);

        var accepted = await coordinator.TryPublishNotificationAsync(
            new SessionCompactionOutcome(true, true, HistoryReplaceOutcome.Applied, 4, 2, 600, 200, null),
            _agentId,
            session,
            CancellationToken.None);
        if (!accepted)
            throw new InvalidOperationException("The production conversation-event publisher refused the compaction event.");

        await _publisher.WaitForDrainAsync(CancellationToken.None);
        return session.History.ShouldHaveSingleItem();
    }

    public async Task<ResetScenarioResult> ResetActiveSessionAsync()
    {
        var (conversations, sessions, session) = await CreatePersistedConversationAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();
        services.Replace(ServiceDescriptor.Singleton<IConversationStore>(conversations));
        services.Replace(ServiceDescriptor.Singleton<ISessionStore>(sessions));
        services.Replace(ServiceDescriptor.Singleton(Substitute.For<IAgentSupervisor>()));
        services.Replace(ServiceDescriptor.Singleton<IConversationEventPublisher>(_publisher));
        services.Replace(ServiceDescriptor.Singleton<IOptionsMonitor<CompactionOptions>>(
            new OptionsMonitor<CompactionOptions>(new CompactionOptions())));
        services.Replace(ServiceDescriptor.Singleton(Substitute.For<ISessionEndMemoryFlusher>()));
        services.Replace(ServiceDescriptor.Singleton(Substitute.For<IAskUserResponseRegistry>()));
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IConversationResetService>();

        var reset = await service.ResetActiveSessionAsync(ConversationId, _sessionId);
        await _publisher.WaitForDrainAsync(CancellationToken.None);

        return new ResetScenarioResult(
            reset,
            (await conversations.GetAsync(ConversationId)).ShouldNotBeNull(),
            (await sessions.GetAsync(_sessionId)).ShouldNotBeNull());
    }

    public async Task PublishAsync(
        BindingId originBindingId,
        string? correlationId,
        AgentStreamEvent streamEvent,
        ConversationId? conversationId = null)
    {
        var targetConversationId = conversationId ?? ConversationId;
        var accepted = await _publisher.PublishAsync(new ConversationAgentEvent
        {
            AgentId = _agentId,
            ConversationId = targetConversationId,
            SessionId = _sessionId,
            Origin = new ConversationEventOrigin(originBindingId, CorrelationId: correlationId),
            Bindings = _bindings
                .Where(binding => binding.ConversationId == targetConversationId)
                .Select(binding => binding.Snapshot)
                .ToImmutableArray(),
            StreamEvent = streamEvent with
            {
                AgentId = _agentId,
                ConversationId = targetConversationId,
                SessionId = _sessionId,
            },
        });

        if (!accepted)
            throw new InvalidOperationException("The production conversation-event publisher refused the scenario event.");

        await _publisher.WaitForDrainAsync(CancellationToken.None);
    }

    public IReadOnlyList<TestChannelConversationEventRecord> Events(string channelId, string address)
        => _adapters[channelId].GetConversationEvents(address);

    public IReadOnlyList<TestChannelLifecycleEventRecord> LifecycleEvents(string channelId, string address)
        => _adapters[channelId].GetLifecycleEvents(address);

    public ValueTask DisposeAsync() => _publisher.DisposeAsync();

    private async Task<(InMemoryConversationStore Conversations, InMemorySessionStore Sessions, GatewaySession Session)>
        CreatePersistedConversationAsync()
    {
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId,
            AgentId = _agentId,
            ActiveSessionId = _sessionId,
            ChannelBindings = [.. _bindings.Select(binding => new ChannelBinding
            {
                BindingId = binding.Snapshot.BindingId,
                ChannelType = binding.Snapshot.ChannelType,
                AdapterId = binding.Snapshot.AdapterId,
                ChannelAddress = binding.Snapshot.ChannelAddress,
                Mode = binding.Snapshot.Mode,
                ThreadingMode = binding.Snapshot.ThreadingMode,
            })],
        });

        var sessions = new InMemorySessionStore(redactor: null, conversations);
        var session = new GatewaySession
        {
            SessionId = _sessionId,
            AgentId = _agentId,
            ConversationId = ConversationId,
        };
        await sessions.SaveAsync(session);
        return (conversations, sessions, session);
    }

    internal sealed record ResetScenarioResult(
        ConversationResetResult Reset,
        Conversation Conversation,
        GatewaySession Session);

    internal sealed record ChannelDefinition(string ChannelId);

    private sealed record ScenarioBinding(
        ConversationId ConversationId,
        ConversationBindingSnapshot Snapshot);

    private sealed class OptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
