namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Classifies whether one completed tool result establishes observable progress.</summary>
/// <param name="IsProgress">
/// Compatible boolean classification: true establishes Progress; false establishes NoProgress.
/// Use the outcome constructor for Neutral. This property is false for Neutral as well as NoProgress.
/// </param>
/// <param name="ScopeIdentity">Stable, payload-free identity for the operation or target. Required for non-progress.</param>
/// <param name="EvidenceIdentity">
/// Stable, payload-free identity for observed evidence. A changed identity at the same scope establishes new evidence.
/// </param>
/// <param name="Kind">Safe diagnostic category for a non-progress decision.</param>
/// <param name="Guidance">Optional safe guidance injected when the loop's warning threshold is reached.</param>
/// <remarks>
/// The policy classifies one result; the loop owns run-local counting, resets, warning injection,
/// and terminal disposition. Identities and guidance must not contain tool arguments, result text,
/// credentials, or other payload data. Null policy decisions and explicit Neutral leave known
/// repetition evidence unchanged. Malformed NoProgress identities are rejected by the loop;
/// no decision grants execution authority or proves an arbitrary write safe to repeat.
/// </remarks>
public sealed record ToolProgressDecision(
    bool IsProgress,
    string? ScopeIdentity = null,
    string? EvidenceIdentity = null,
    string? Kind = null,
    string? Guidance = null)
{
    private readonly bool _neutral;

    /// <summary>
    /// Creates an explicit classification. NoProgress requires nonempty scope, evidence, and kind
    /// identities, validated by the loop. Neutral and Progress do not require identities.
    /// </summary>
    /// <param name="outcome">The observable classification, not an inference from repeated writes.</param>
    /// <param name="scopeIdentity">Stable payload-free target identity for NoProgress.</param>
    /// <param name="evidenceIdentity">Stable payload-free authoritative evidence identity for NoProgress.</param>
    /// <param name="kind">Safe diagnostic category for NoProgress.</param>
    /// <param name="guidance">Optional payload-free warning guidance.</param>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined classification.</exception>
    public ToolProgressDecision(
        ToolProgressOutcome outcome,
        string? scopeIdentity = null,
        string? evidenceIdentity = null,
        string? kind = null,
        string? guidance = null)
        : this(outcome == ToolProgressOutcome.Progress, scopeIdentity, evidenceIdentity, kind, guidance)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        _neutral = outcome == ToolProgressOutcome.Neutral;
    }

    /// <summary>
    /// Explicit classification. The compatible boolean constructor maps true to Progress and false
    /// to NoProgress; use the outcome constructor or Neutral singleton for an unclassified result.
    /// </summary>
    public ToolProgressOutcome Outcome => IsProgress ? ToolProgressOutcome.Progress
        : _neutral ? ToolProgressOutcome.Neutral : ToolProgressOutcome.NoProgress;

    /// <summary>A decision that neither increments nor resets run-local repetition evidence.</summary>
    public static ToolProgressDecision Neutral { get; } = new(ToolProgressOutcome.Neutral);

    /// <summary>A decision that establishes progress and resets repeated non-progress tracking.</summary>
    public static ToolProgressDecision Progress { get; } = new(true);

    /// <summary>Creates a non-progress decision for validation and aggregation by the loop.</summary>
    public static ToolProgressDecision NoProgress(
        string scopeIdentity,
        string evidenceIdentity,
        string kind,
        string? guidance = null)
        => new(false, scopeIdentity, evidenceIdentity, kind, guidance);
}
