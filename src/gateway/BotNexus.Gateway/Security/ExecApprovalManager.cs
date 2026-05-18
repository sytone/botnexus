using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Thread-safe, single-use approval token manager for exec/shell commands.
/// Implements <see cref="IExecApprovalManager"/> with four security invariants:
/// <list type="bullet">
///   <item><b>A — Payload substitution</b>: the canonical command is stored at issuance time;
///     redemption requires an exact string match.</item>
///   <item><b>B — Truncated TOCTOU</b>: the full canonical command is stored and validated,
///     so an approval issued for a short fragment cannot unlock the full payload.</item>
///   <item><b>C — Cross-session reuse</b>: each token is bound to the issuing session ID;
///     a different session cannot redeem it.</item>
///   <item><b>D — Parallel approval race</b>: <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove"/>
///     is atomic — only one concurrent redeem call can remove the entry.</item>
/// </list>
/// </summary>
public sealed class ExecApprovalManager : IExecApprovalManager
{
    /// <summary>
    /// Matches PowerShell <c>-EncodedCommand</c> or <c>-ec</c> (and legacy <c>-e</c> / <c>-en</c> / <c>-enc</c>)
    /// anywhere in the command line so that inline flags like <c>-NoProfile</c> before the encoded flag
    /// are handled correctly.
    /// Group 1 captures the base64 payload.
    /// </summary>
    private static readonly Regex PowerShellEncodedPattern = new(
        @"(?i)(?:^|\s)(?:-|/)(?:EncodedCommand|ec)\s+([A-Za-z0-9+/]+=*)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record PendingApproval(string SessionId, string CanonicalCommand);

    private readonly ConcurrentDictionary<string, PendingApproval> _pending =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ExecApprovalRequest Issue(string sessionId, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var canonical = DecodeIfPowerShellEncoded(command);
        var tokenId = Guid.NewGuid().ToString("N");
        _pending[tokenId] = new PendingApproval(sessionId, canonical);
        return new ExecApprovalRequest(tokenId, canonical);
    }

    /// <inheritdoc />
    public bool TryRedeem(string tokenId, string sessionId, string canonicalCommand)
    {
        if (string.IsNullOrEmpty(tokenId)
            || string.IsNullOrEmpty(sessionId)
            || string.IsNullOrEmpty(canonicalCommand))
        {
            return false;
        }

        // Atomic removal prevents parallel redemption of the same token (D).
        if (!_pending.TryRemove(tokenId, out var pending))
            return false;

        // Session binding check — token must be redeemed by the session that requested it (C).
        if (!string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal))
            return false;

        // Exact canonical command match — prevents payload substitution (A)
        // and truncated-command TOCTOU attacks (B).
        if (!string.Equals(pending.CanonicalCommand, canonicalCommand, StringComparison.Ordinal))
            return false;

        return true;
    }

    /// <summary>
    /// Decodes a PowerShell <c>-EncodedCommand</c> / <c>-ec</c> payload to its plaintext form.
    /// PowerShell encodes commands as UTF-16 LE base64, so that encoding is used for decoding.
    /// If the command does not match the encoded-command pattern, it is returned unchanged.
    /// If the base64 payload is malformed, the original command is returned unchanged.
    /// </summary>
    internal static string DecodeIfPowerShellEncoded(string command)
    {
        var match = PowerShellEncodedPattern.Match(command);
        if (!match.Success)
            return command;

        var base64 = match.Groups[1].Value;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            // PowerShell -EncodedCommand always uses UTF-16 LE (Unicode).
            return Encoding.Unicode.GetString(bytes);
        }
        catch (FormatException)
        {
            // Malformed base64 — return command unchanged rather than throwing.
            return command;
        }
    }
}
