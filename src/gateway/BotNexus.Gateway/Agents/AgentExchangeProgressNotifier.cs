using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default <see cref="IAgentExchangeProgressNotifier"/>: renders a handoff milestone as a single
/// status line and delivers it to the initiating session's channel bindings through the existing
/// outbound fan-out path (#3176).
/// </summary>
/// <remarks>
/// <para>
/// Reusing <see cref="IOutboundResponseDeliverer"/> is deliberate. It already owns binding
/// resolution, non-deliverable channel-type skipping, per-binding failure containment and
/// stale-binding self-heal. A bespoke delivery path for progress would have had to re-derive all
/// four, and would have diverged the first time one of them was fixed.
/// </para>
/// <para>
/// <strong>Never throws.</strong> Progress is observability, not a contract: a failure to report
/// that a handoff started must not fail the handoff. Everything is caught and logged at DEBUG /
/// WARNING, which is what keeps blocking-caller parity (AC6) true by construction rather than by
/// convention.
/// </para>
/// </remarks>
internal sealed class AgentExchangeProgressNotifier(
    IOutboundResponseDeliverer deliverer,
    ILogger<AgentExchangeProgressNotifier> logger) : IAgentExchangeProgressNotifier
{
    private readonly IOutboundResponseDeliverer _deliverer = deliverer;
    private readonly ILogger<AgentExchangeProgressNotifier> _logger = logger;

    /// <inheritdoc />
    public async Task PublishAsync(AgentExchangeProgressEvent progressEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);

        // No initiating session means no thread to report into (cron-driven and test call sites).
        // Not an error - just nothing to observe. The conversation id is required alongside it
        // because ConversationId is a Vogen value object with no legal uninitialized literal, and
        // fan-out needs a real one to self-heal a stale binding.
        if (progressEvent.InitiatorSessionId is not { } initiatorSessionId
            || progressEvent.InitiatorConversationId is not { } initiatorConversationId)
        {
            _logger.LogDebug(
                "Agent exchange progress '{Phase}' not delivered: the request carries no initiating session/conversation.",
                progressEvent.Phase);
            return;
        }

        try
        {
            // A synthetic source whose BindingId is null so fan-out excludes nothing: the progress
            // line did not arrive on a binding, so every interactive/notify binding should see it.
            var source = new InboundMessage
            {
                ChannelType = ChannelKey.From("internal"),
                SenderId = $"agent-exchange:{progressEvent.TargetId.Value}",
                Sender = CitizenId.Of(progressEvent.InitiatorId),
                ChannelAddress = ChannelAddress.From(initiatorSessionId.Value),
                Content = string.Empty,
                Kind = MessageKind.AgentExchangeProgress,
                Metadata = new Dictionary<string, object?>
                {
                    ["messageType"] = "agent-exchange-progress",
                    ["phase"] = progressEvent.Phase.ToString(),
                    ["childConversationId"] = progressEvent.ChildConversationId?.Value,
                    ["childSessionId"] = progressEvent.ChildSessionId?.Value
                }
            };

            await _deliverer.FanOutAsync(
                source,
                initiatorSessionId,
                progressEvent.ToStatusLine(),
                initiatorConversationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed publishing agent exchange progress '{Phase}' for {Initiator} -> {Target}. Continuing.",
                progressEvent.Phase,
                progressEvent.InitiatorId.Value,
                progressEvent.TargetId.Value);
        }
    }
}
