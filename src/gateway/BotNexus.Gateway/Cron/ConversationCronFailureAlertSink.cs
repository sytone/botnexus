using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Cron;

/// <summary>
/// Delivers cron failure alerts (#2557) into a configured conversation by reusing the existing
/// conversation-message seam: resolve the conversation, route inbound through
/// <see cref="IConversationRouter"/>, and post via <see cref="IInboundMessageOrchestrator"/> --
/// exactly the path <c>ConversationTool</c>'s "message" action already takes. No webhook, no
/// per-channel or per-account routing (both explicitly out of scope for #2557).
/// </summary>
public sealed class ConversationCronFailureAlertSink(
    IConversationStore conversationStore,
    IConversationRouter conversationRouter,
    IInboundMessageOrchestrator messageOrchestrator,
    ILogger<ConversationCronFailureAlertSink> logger) : ICronFailureAlertSink
{
    /// <inheritdoc/>
    public async Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var conversation = await conversationStore.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
        {
            // Surfaced to the scheduler, which logs it and leaves the cron run untouched (AC7).
            throw new InvalidOperationException(
                $"Cron failure alert target conversation '{conversationId.Value}' does not exist.");
        }

        var routing = await conversationRouter.ResolveInboundAsync(
            conversation.AgentId,
            ChannelKey.From("internal"),
            ChannelAddress.From(conversation.AgentId.Value),
            conversation.ConversationId,
            ct,
            CitizenId.Of(conversation.AgentId)).ConfigureAwait(false);

        messageOrchestrator.Post(new InboundMessage
        {
            ChannelType = ChannelKey.From("internal"),
            SenderId = conversation.AgentId.Value,
            Sender = CitizenId.Of(conversation.AgentId),
            ChannelAddress = ChannelAddress.From(conversation.AgentId.Value),
            Content = alert.FormatMessage(),
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: routing.Conversation.AgentId,
                RequestedSessionId: routing.SessionId,
                RequestedConversationId: routing.Conversation.ConversationId),
            Metadata = new Dictionary<string, object?>
            {
                ["messageType"] = "message",
                ["source"] = "cron-failure-alert",
                ["cronJobId"] = alert.JobId.Value,
                ["cronScheduledRunTime"] = alert.ScheduledRunTime.ToString("O"),
                ["cronConsecutiveErrors"] = alert.ConsecutiveErrorCount,
            }
        });

        logger.LogInformation(
            "Cron failure alert posted to conversation '{ConversationId}' for job '{JobId}'.",
            conversationId.Value, alert.JobId.Value);
    }
}