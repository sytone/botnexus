namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Preserves an engine-owned exchange deadline across the service/tool boundary, where the
/// later tool backstop cannot establish the cause of an earlier cancellation.
/// </summary>
/// <remarks>
/// Raised after the engine's deadline seal/archive handling. Remaining a cancellation exception
/// preserves existing cancellation catches; ambient caller cancellation still takes precedence.
/// </remarks>
public sealed class AgentExchangeDeadlineExceededException : OperationCanceledException
{
    /// <summary>
    /// Retains the original cancellation and token for diagnostics without asking callers to
    /// infer deadline ownership from elapsed time or exception text.
    /// </summary>
    public AgentExchangeDeadlineExceededException(OperationCanceledException innerException)
        : base("Agent exchange exceeded its deadline.", innerException, innerException.CancellationToken)
    {
    }
}
