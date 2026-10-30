using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Observed exchange progress, not a claim about the target's current state. Text is explicitly
/// incomplete and sanitised before persistence; null usage means unmeasured, never zero.
/// </summary>
public sealed record AgentExchangeCancellationEvidence
{
    /// <summary>Child conversation that owns the retained evidence.</summary>
    public required ConversationId ConversationId { get; init; }
    /// <summary>Child session that owns the retained evidence.</summary>
    public required SessionId SessionId { get; init; }
    /// <summary>Last observed local phase, such as promptInterrupted or neverDispatched.</summary>
    public required string Phase { get; init; }
    /// <summary>Always incomplete; this is not a final assistant response.</summary>
    public bool IsIncomplete { get; init; } = true;
    /// <summary>Bounded, redacted non-thinking partial response text.</summary>
    public string? PartialText { get; init; }
    /// <summary>Provider-reported last-request usage, if measured.</summary>
    public AgentResponseUsage? Usage { get; init; }
    /// <summary>Provider-reported aggregate usage, if measured.</summary>
    public AgentResponseUsage? RunUsage { get; init; }
    /// <summary>Retained tool results; completion does not imply side-effect rollback.</summary>
    public int CompletedToolCount { get; init; }
    /// <summary>Retained calls with no observed result.</summary>
    public int IncompleteToolCount { get; init; }
}

/// <summary>Carries durable evidence while preserving the existing cancellation contract.</summary>
public class AgentExchangeCancelledException : OperationCanceledException
{
    /// <summary>Constructs a cancellation without exposing provider exception text.</summary>
    public AgentExchangeCancelledException(AgentExchangeCancellationEvidence evidence, CancellationToken token)
        : base("Agent exchange was cancelled; reconcile retained evidence before retrying.", token)
        => Evidence = evidence;

    /// <summary>Observed progress retained by the service, when available.</summary>
    public AgentExchangeCancellationEvidence? Evidence { get; protected init; }

    /// <summary>Preserves evidence when the engine attributes cancellation to its deadline.</summary>
    protected AgentExchangeCancelledException(string message, OperationCanceledException innerException)
        : base(message, innerException, innerException.CancellationToken)
        => Evidence = (innerException as AgentExchangeCancelledException)?.Evidence
            ?? innerException.Data[nameof(AgentExchangeCancellationEvidence)] as AgentExchangeCancellationEvidence;
}
