using System.Collections.Immutable;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    public ValueTask DisposeAsync() => _publisher.DisposeAsync();

    internal sealed record ChannelDefinition(string ChannelId);

    private sealed record ScenarioBinding(
        ConversationId ConversationId,
        ConversationBindingSnapshot Snapshot);
}
