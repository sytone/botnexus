using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Background service that periodically archives conversations that have been inactive
/// for longer than the configured retention threshold.
/// <para>
/// Auto-archive is opt-in: the world-level <c>gateway.conversations.autoArchiveEnabled</c>
/// must be <c>true</c> before any archiving occurs. Per-agent overrides can extend or
/// reduce the window, or disable auto-archive entirely for a specific agent.
/// </para>
/// <para>Pinned conversations are always excluded from auto-archive.</para>
/// </summary>
public sealed class ConversationRetentionHostedService(
    IConversationStore conversationStore,
    IEnumerable<IConversationChangeNotifier>? changeNotifiers,
    IAgentRegistry agentRegistry,
    IOptions<ConversationRetentionOptions> optionsAccessor,
    ILogger<ConversationRetentionHostedService> logger) : BackgroundService
{
    private readonly IConversationStore _conversationStore = conversationStore;
    private readonly IReadOnlyList<IConversationChangeNotifier> _changeNotifiers = changeNotifiers?.ToArray() ?? [];
    private readonly IAgentRegistry _agentRegistry = agentRegistry;
    private readonly ILogger<ConversationRetentionHostedService> _logger = logger;

    private ConversationRetentionOptions Options => optionsAccessor.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRetentionOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Conversation retention iteration failed.");
            }

            var delay = Options.CheckInterval > TimeSpan.Zero
                ? Options.CheckInterval
                : TimeSpan.FromHours(1);
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a single retention pass: archives all conversations that have exceeded the
    /// configured inactivity threshold. Returns the count of archived conversations.
    /// </summary>
    public async Task<int> RunRetentionOnceAsync(CancellationToken cancellationToken = default)
    {
        var worldOptions = Options;

        // World-level auto-archive must be enabled as the opt-in gate.
        if (!worldOptions.AutoArchiveEnabled)
            return 0;

        var worldThresholdDays = worldOptions.AutoArchiveAfterDays > 0
            ? worldOptions.AutoArchiveAfterDays
            : 0;

        if (worldThresholdDays <= 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var conversations = await _conversationStore.ListAsync(ct: cancellationToken)
            .ConfigureAwait(false);

        var archivedCount = 0;

        foreach (var conv in conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only archive Active conversations.
            if (conv.Status != ConversationStatus.Active)
                continue;

            // Exclude pinned conversations. IsPinned is always false until #780 is implemented;
            // this guard is a forward-compatible no-op that prevents regressions once pinning lands.
            if (IsConversationPinned(conv))
                continue;

            // Resolve per-agent effective threshold.
            var effectiveThresholdDays = ResolveEffectiveThreshold(conv.AgentId, worldThresholdDays);
            if (effectiveThresholdDays <= 0)
                continue;

            var inactiveFor = now - conv.UpdatedAt;
            if (inactiveFor < TimeSpan.FromDays(effectiveThresholdDays))
                continue;

            await _conversationStore.ArchiveAsync(conv.ConversationId, "retention", conv.ConversationId.Value, "system", cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Auto-archived conversation {ConversationId} (agent {AgentId}) after {InactiveDays:F1} days of inactivity (threshold: {ThresholdDays}d).",
                conv.ConversationId,
                conv.AgentId,
                inactiveFor.TotalDays,
                effectiveThresholdDays);

            await NotifyBestEffortAsync(conv, cancellationToken).ConfigureAwait(false);
            archivedCount++;
        }

        if (archivedCount > 0)
            _logger.LogInformation("Conversation retention: archived {Count} conversation(s).", archivedCount);

        return archivedCount;
    }

    private int ResolveEffectiveThreshold(AgentId agentId, int worldDefault)
    {
        var descriptor = _agentRegistry.Get(agentId);
        if (descriptor?.ConversationRetention is not { } agentRetention)
            return worldDefault;

        // Per-agent override: disabled flag wins first.
        if (!agentRetention.AutoArchiveEnabled)
            return 0;

        // Per-agent AutoArchiveAfterDays overrides world default when explicitly set.
        if (agentRetention.AutoArchiveAfterDays.HasValue)
            return agentRetention.AutoArchiveAfterDays.Value > 0 ? agentRetention.AutoArchiveAfterDays.Value : 0;

        return worldDefault;
    }

    /// <summary>
    /// Returns <c>true</c> when the conversation is pinned and should be excluded from retention.
    /// </summary>
    private static bool IsConversationPinned(Conversation conversation) =>
        conversation.IsPinned;

    private async Task NotifyBestEffortAsync(Conversation conv, CancellationToken cancellationToken)
    {
        if (_changeNotifiers.Count == 0)
            return;

        foreach (var notifier in _changeNotifiers)
        {
            try
            {
                await notifier.NotifyConversationChangedAsync(
                    "archived",
                    conv.AgentId.Value,
                    conv.ConversationId.Value,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "SignalR notify failed for auto-archive of conversation {ConversationId}; portal will refresh on next poll.",
                    conv.ConversationId);
            }
        }
    }
}
