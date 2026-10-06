namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Classifies whether one completed tool result establishes observable progress.</summary>
/// <param name="IsProgress">True when the result establishes progress; false when it does not.</param>
/// <param name="ScopeIdentity">Stable, payload-free identity for the operation or target. Required for non-progress.</param>
/// <param name="EvidenceIdentity">
/// Stable, payload-free identity for observed evidence. A changed identity at the same scope establishes new evidence.
/// </param>
/// <param name="Kind">Safe diagnostic category for a non-progress decision.</param>
/// <param name="Guidance">Optional safe guidance injected when the loop's warning threshold is reached.</param>
/// <remarks>
/// The policy classifies one result; the loop owns run-local counting, resets, warning injection,
/// and terminal disposition. Identities and guidance must not contain tool arguments, result text,
/// credentials, or other payload data.
/// </remarks>
public sealed record ToolProgressDecision(
    bool IsProgress,
    string? ScopeIdentity = null,
    string? EvidenceIdentity = null,
    string? Kind = null,
    string? Guidance = null)
{
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
