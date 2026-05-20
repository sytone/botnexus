namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// Manages exec/shell command approvals with session-scoped, single-use tokens.
/// Defends against approval bypass attacks: payload substitution, truncated TOCTOU, cross-session
/// token reuse, and parallel approval race conditions.
/// </summary>
public interface IExecApprovalManager
{
    /// <summary>
    /// Issues an approval token for the given command in the context of a specific session.
    /// PowerShell <c>-EncodedCommand</c> / <c>-ec</c> payloads are base64-decoded before
    /// storage so the canonical command reflects the real intent.
    /// </summary>
    /// <param name="sessionId">The session requesting approval. Tokens are bound to this identity.</param>
    /// <param name="command">The raw command text submitted for approval.</param>
    /// <returns>
    /// An <see cref="ExecApprovalRequest"/> containing the one-time token ID and the canonical
    /// (decoded) command that must be shown to the user for approval.
    /// </returns>
    ExecApprovalRequest Issue(string sessionId, string command);

    /// <summary>
    /// Attempts to redeem a previously issued approval token.
    /// Redemption succeeds only when all three conditions hold:
    /// <list type="bullet">
    ///   <item>The token exists and has not been redeemed before (single-use — prevents race D).</item>
    ///   <item>The <paramref name="sessionId"/> matches the one the token was issued for (prevents cross-session reuse — C).</item>
    ///   <item>The <paramref name="canonicalCommand"/> is an exact match to the stored canonical command (prevents substitution — A, and truncated approval — B).</item>
    /// </list>
    /// </summary>
    /// <param name="tokenId">The token ID returned by <see cref="Issue"/>.</param>
    /// <param name="sessionId">The session attempting to redeem the token.</param>
    /// <param name="canonicalCommand">The canonical command the caller intends to execute.</param>
    /// <returns><c>true</c> if the token was valid and successfully redeemed; <c>false</c> otherwise.</returns>
    bool TryRedeem(string tokenId, string sessionId, string canonicalCommand);
}

/// <summary>
/// Represents a pending approval request issued by <see cref="IExecApprovalManager.Issue"/>.
/// </summary>
/// <param name="TokenId">
/// One-time token ID. Pass this to <see cref="IExecApprovalManager.TryRedeem"/> after user confirms.
/// </param>
/// <param name="CanonicalCommand">
/// The decoded/normalized command text to present to the user for approval.
/// For PowerShell <c>-EncodedCommand</c> / <c>-ec</c> invocations this is the decoded payload,
/// not the opaque base64 string.
/// </param>
public sealed record ExecApprovalRequest(string TokenId, string CanonicalCommand);
