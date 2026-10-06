using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Background service implementing the webhook-specific conversation retention policy (issue #2125).
/// <para>
/// Automation-owned webhook conversations - identified by authoritative provenance
/// (<see cref="WebhookConversationProvenance"/>), never by title - age out faster than ordinary
/// human conversations while active registrations remain inspectable. The policy is:
/// </para>
/// <list type="bullet">
///   <item>User-pinned conversations are never archived.</item>
///   <item>The canonical (pinned) conversation of an <em>enabled</em> registration is protected.</item>
///   <item>The canonical conversation of a <em>disabled</em> registration, and the conversation of a
///     <em>deleted</em> registration, become eligible after
///     <see cref="WebhookConversationRetentionOptions.DisabledRegistrationInactivityDays"/>.</item>
///   <item>Unreferenced race/orphan webhook conversations (registration still exists but this is not
///     its canonical conversation) age out aggressively after
///     <see cref="WebhookConversationRetentionOptions.OrphanInactivityDays"/>.</item>
/// </list>
/// <para>
/// Non-webhook conversations are ignored here - the world-level
/// <c>ConversationRetentionHostedService</c> owns those. Webhook <em>run</em> audit records are
/// governed independently by <see cref="WebhookRunRetentionHostedService"/>; this service touches
/// only conversation rows.
/// </para>
/// </summary>
public sealed class WebhookConversationRetentionHostedService(
    IConversationStore conversationStore,
    IWebhookRegistrationStore registrationStore,
    IEnumerable<IConversationChangeNotifier>? changeNotifiers,
    IOptions<WebhookConversationRetentionOptions> optionsAccessor,
    ILogger<WebhookConversationRetentionHostedService> logger) : BackgroundService
{
    private readonly IConversationStore _conversationStore = conversationStore;
    private readonly IWebhookRegistrationStore _registrationStore = registrationStore;
    private readonly IReadOnlyList<IConversationChangeNotifier> _changeNotifiers = changeNotifiers?.ToArray() ?? [];
    private readonly ILogger<WebhookConversationRetentionHostedService> _logger = logger;

    private WebhookConversationRetentionOptions Options => optionsAccessor.Value;

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
                _logger.LogWarning(ex, "Webhook conversation retention iteration failed.");
            }

            var delay = Options.CheckInterval > TimeSpan.Zero
                ? Options.CheckInterval
                : TimeSpan.FromHours(1);
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a single retention pass over active webhook-owned conversations, archiving those that
    /// have exceeded their provenance-derived inactivity threshold. Returns the count archived.
    /// Exposed for testability.
    /// </summary>
    public async Task<int> RunRetentionOnceAsync(CancellationToken cancellationToken = default)
    {
        var options = Options;
        if (!options.Enabled)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var conversations = await _conversationStore
            .GetRetentionCandidatesAsync(ConversationSource.Webhook, cancellationToken)
            .ConfigureAwait(false);

        var archivedCount = 0;

        foreach (var conv in conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The source-partitioned query is active-only. Legacy rows without a source id remain
            // intentionally ineligible because their registration cannot be identified safely.
            if (conv.IsPinned || string.IsNullOrWhiteSpace(conv.SourceId))
            {
                continue;
            }

            var conversationId = conv.ConversationId;
            var agentId = conv.AgentId;
            var webhookId = WebhookId.From(conv.SourceId);

            var thresholdDays = await ResolveThresholdDaysAsync(conv, webhookId, options, cancellationToken)
                .ConfigureAwait(false);
            if (thresholdDays <= 0)
                continue;

            var inactiveFor = now - conv.UpdatedAt;
            if (inactiveFor < TimeSpan.FromDays(thresholdDays))
                continue;

            if (!await _conversationStore.TryArchiveAsync(
                    conversationId,
                    "webhook-retention",
                    conv.ConversationId.Value,
                    "system",
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            _logger.LogInformation(
                "Webhook-retention archived conversation {ConversationId} (webhook {WebhookId}, agent {AgentId}) after {InactiveDays:F1} days inactive (threshold {ThresholdDays}d).",
                conversationId,
                webhookId,
                agentId,
                inactiveFor.TotalDays,
                thresholdDays);

            await NotifyBestEffortAsync(agentId, conversationId, cancellationToken).ConfigureAwait(false);
            archivedCount++;
        }

        if (archivedCount > 0)
            _logger.LogInformation("Webhook conversation retention: archived {Count} conversation(s).", archivedCount);

        return archivedCount;
    }

    /// <summary>
    /// Resolves the effective inactivity threshold (in days) for a webhook conversation based on the
    /// current state of its owning registration. Returns <c>0</c> when the conversation is protected
    /// (canonical conversation of an enabled registration) or the applicable rule is disabled.
    /// </summary>
    private async Task<int> ResolveThresholdDaysAsync(
        ConversationRetentionCandidate conv,
        WebhookId webhookId,
        WebhookConversationRetentionOptions options,
        CancellationToken cancellationToken)
    {
        var registration = await _registrationStore.GetAsync(webhookId, cancellationToken).ConfigureAwait(false);

        if (registration is null)
        {
            // Deleted registration: its conversation ages out on the disabled/deleted window.
            return options.DisabledRegistrationInactivityDays;
        }

        var isCanonical = registration.PinnedConversationId == conv.ConversationId;

        if (isCanonical)
        {
            // Canonical conversation of an enabled registration is protected; a disabled
            // registration's canonical conversation ages out on the disabled/deleted window.
            return registration.Enabled ? 0 : options.DisabledRegistrationInactivityDays;
        }

        // Registration exists but this is not its canonical conversation: it is an unreferenced
        // race/orphan row and ages out aggressively regardless of the registration's enabled state.
        return options.OrphanInactivityDays;
    }

    private async Task NotifyBestEffortAsync(
        AgentId agentId,
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        if (_changeNotifiers.Count == 0)
            return;

        foreach (var notifier in _changeNotifiers)
        {
            try
            {
                await notifier.NotifyConversationChangedAsync(
                    "archived",
                    agentId.Value,
                    conversationId.Value,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "SignalR notify failed for webhook-retention archive of conversation {ConversationId}; portal will refresh on next poll.",
                    conversationId);
            }
        }
    }
}
