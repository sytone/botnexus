namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// The provider <c>finish_reason</c> values that describe a TRANSPORT failure rather than a model
/// outcome, and therefore belong in the agent loop's retry lane rather than on a returned message
/// (#3567).
/// </summary>
/// <remarks>
/// <para>
/// The layering mismatch this type closes: the provider layer classified <c>network_error</c> as an
/// <em>outcome</em> (<see cref="Models.StopReason.Error"/> plus a human-readable message), while
/// <c>AgentLoopRunner.ExecuteWithRetryAsync</c> consumes only <em>exceptions</em> - it retries inside
/// <c>catch (Exception ex) when (ClassifyFailure(ex) == Transient)</c>. A correct classification
/// therefore never reached the one component able to act on it, so a transient blip spent zero of a
/// four-attempt budget and ended the turn.
/// </para>
/// <para>
/// Raising the reason as an exception at the parse seam reuses the existing retry, backoff and
/// jitter machinery unchanged rather than introducing a second retry path. Upstream OpenCode made
/// the same move in <c>e0b9e68a</c> (fail the effect on <c>rawFinishReason === "network_error"</c>)
/// after widening its text classifier in <c>40282c1d</c>; the message string here is deliberately
/// byte-identical to the one the mapping produced before, so the loop-level
/// <c>TransientErrorClassifier</c> sees exactly the vocabulary it is tested against.
/// </para>
/// <para>
/// The set is deliberately narrow. Every other failure-style finish reason - including
/// <c>content_filter</c> and any unknown value - keeps its pre-existing terminal behaviour and ends
/// the run on the first occurrence. Widening this set is a behaviour change to the retry budget and
/// must be argued for explicitly.
/// </para>
/// </remarks>
public static class TransientFinishReasons
{
    private static readonly HashSet<string> Reasons =
        new(StringComparer.OrdinalIgnoreCase) { "network_error" };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="finishReason"/> names a transport failure
    /// that should engage the agent loop's retry lane.
    /// </summary>
    /// <param name="finishReason">The raw provider <c>finish_reason</c>, or <see langword="null"/>.</param>
    public static bool IsTransient(string? finishReason)
        => finishReason is not null && Reasons.Contains(finishReason);

    /// <summary>
    /// Throws <see cref="ProviderTransientFinishReasonException"/> when
    /// <paramref name="finishReason"/> is a recognised transient transport failure; otherwise
    /// returns without effect so every other reason keeps its existing terminal path.
    /// </summary>
    /// <param name="finishReason">The raw provider <c>finish_reason</c>, or <see langword="null"/>.</param>
    public static void ThrowIfTransient(string? finishReason)
    {
        if (IsTransient(finishReason))
        {
            throw new ProviderTransientFinishReasonException(finishReason!);
        }
    }
}
