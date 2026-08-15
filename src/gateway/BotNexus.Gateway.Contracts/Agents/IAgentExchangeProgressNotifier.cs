using BotNexus.Domain.AgentExchange;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Publishes agent-to-agent handoff milestones back into the <em>initiating</em> conversation
/// (#3176), so the thread that delegated the work can see it start, finish, fail, or be halted
/// instead of going silent until the blocking call returns.
/// </summary>
/// <remarks>
/// <para>
/// Emission is strictly advisory: an implementation must never throw into the exchange path and
/// must never alter the blocking result. A caller that ignores progress observes byte-identical
/// behaviour to the pre-#3176 exchange (AC6), which is why every call site swallows failures.
/// </para>
/// <para>
/// The default implementation delivers through the existing outbound fan-out path
/// (<c>IOutboundResponseDeliverer</c>) rather than inventing a second delivery mechanism.
/// </para>
/// </remarks>
public interface IAgentExchangeProgressNotifier
{
    /// <summary>
    /// Delivers one progress milestone to the initiating conversation. Implementations are
    /// expected to be fire-and-forget-safe: failures are contained, not propagated.
    /// </summary>
    Task PublishAsync(AgentExchangeProgressEvent progressEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op <see cref="IAgentExchangeProgressNotifier"/>. Used by the many direct-construction test
/// call sites and as the DI fallback when nothing is registered, so the exchange path never has to
/// null-check before emitting.
/// </summary>
public sealed class NullAgentExchangeProgressNotifier : IAgentExchangeProgressNotifier
{
    /// <summary>Shared singleton no-op instance.</summary>
    public static readonly NullAgentExchangeProgressNotifier Instance = new();

    private NullAgentExchangeProgressNotifier() { }

    /// <inheritdoc />
    public Task PublishAsync(AgentExchangeProgressEvent progressEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
