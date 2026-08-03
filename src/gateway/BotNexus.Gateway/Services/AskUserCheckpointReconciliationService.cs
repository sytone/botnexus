using System.Text.Json;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Services;

/// <summary>
/// On gateway start, rebuilds the in-memory ask_user waiter map from durable
/// <see cref="Conversation.PendingAskUserJson"/> checkpoints whose original waiter did not survive a
/// restart, reload, or reconfiguration (issue #2047). Without this, a prompt that was pending when the
/// gateway stopped would still render (the portal hydrates it via the pending-ask REST endpoint) but an
/// inbound free-text answer would be mis-dispatched as a fresh turn because
/// <see cref="IAskUserResponseRegistry.TryGetPendingRequestId"/> reported nothing.
/// </summary>
/// <remarks>
/// Rehydrated entries are non-completable placeholders (see <see cref="IAskUserResponseRegistry.Rehydrate"/>);
/// the actual resolution flows through <see cref="IAskUserCheckpointService"/>, which claims and clears the
/// durable checkpoint and dispatches a continuation. This service only restores the interception mapping so
/// ordinary messages are not swallowed and real answers are captured.
/// </remarks>
public sealed class AskUserCheckpointReconciliationService(
    IConversationStore conversationStore,
    IAskUserResponseRegistry registry,
    ILogger<AskUserCheckpointReconciliationService> logger) : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var conversations = await conversationStore.ListAsync(agentId: null, cancellationToken).ConfigureAwait(false);
            var rehydrated = 0;
            foreach (var conversation in conversations)
            {
                if (string.IsNullOrEmpty(conversation.PendingAskUserJson))
                    continue;

                AskUserRequest? pending;
                try
                {
                    pending = JsonSerializer.Deserialize<AskUserRequest>(conversation.PendingAskUserJson, JsonOptions);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex,
                        "Skipping unparseable pending ask_user checkpoint for conversation {ConversationId} during reconciliation.",
                        conversation.ConversationId);
                    continue;
                }

                if (pending is null || string.IsNullOrWhiteSpace(pending.RequestId))
                    continue;

                if (registry.Rehydrate(conversation.ConversationId, pending.RequestId))
                    rehydrated++;
            }

            if (rehydrated > 0)
            {
                logger.LogInformation(
                    "Rehydrated {Count} durable ask_user checkpoint(s) into the response registry on startup (#2047).",
                    rehydrated);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reconciliation is best-effort: a failure here must never block gateway startup. A
            // missed rehydration only means the first inbound answer after restart could be treated
            // as a normal turn; the durable checkpoint itself is untouched and still visible.
            logger.LogWarning(ex, "ask_user checkpoint reconciliation failed on startup; continuing.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
