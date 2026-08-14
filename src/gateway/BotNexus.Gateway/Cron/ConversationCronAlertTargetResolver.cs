using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Cron;

/// <summary>
/// The production <see cref="ICronAlertTargetResolver"/> (#3168). Answers the single question
/// <c>CronAlertTarget.ValidateAsync</c> asks - "does this conversation exist?" - against the live
/// <see cref="IConversationStore"/>.
///
/// <para>
/// Lives in the gateway assembly for the same reason
/// <see cref="ConversationCronFailureAlertSink"/> does: the cron assembly owns the narrow seam,
/// the gateway owns conversation persistence. Without this type registered, validation had no
/// resolver and fell through to the fail-closed branch, so <b>every</b> attempt to set
/// <c>failureAlertConversationId</c> was rejected and cron failure alerting could never be
/// targeted at all.
/// </para>
///
/// <para>
/// An <see cref="ConversationStatus.Archived"/> conversation is treated as <b>unresolvable</b>.
/// It is still readable, but it is a retired destination: storing it as an alert target would
/// reproduce the exact outcome the fail-closed guard exists to prevent - a job whose alerts are
/// delivered somewhere nobody is looking. Ownership is deliberately <b>not</b> checked: cron jobs
/// legitimately alert into a conversation belonging to another agent (an operator or supervisor
/// channel), so a cross-agent target resolves normally.
/// </para>
/// </summary>
public sealed class ConversationCronAlertTargetResolver(
    IConversationStore conversationStore,
    ILogger<ConversationCronAlertTargetResolver> logger) : ICronAlertTargetResolver
{
    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        if (!conversationId.IsInitialized())
            return false;

        var conversation = await conversationStore.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
        {
            logger.LogDebug(
                "Cron alert target '{ConversationId}' does not resolve to an existing conversation.",
                conversationId.Value);
            return false;
        }

        if (conversation.Status == ConversationStatus.Archived)
        {
            logger.LogDebug(
                "Cron alert target '{ConversationId}' resolves to an archived conversation and is rejected.",
                conversationId.Value);
            return false;
        }

        return true;
    }
}
