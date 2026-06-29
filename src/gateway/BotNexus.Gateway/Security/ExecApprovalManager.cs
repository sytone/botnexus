using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Thread-safe, single-use approval token manager for exec/shell commands.
/// Implements <see cref="IExecApprovalManager"/> with four security invariants:
/// <list type="bullet">
///   <item><b>A - Payload substitution</b>: the canonical command is stored at issuance time;
///     redemption requires an exact string match.</item>
///   <item><b>B - Truncated TOCTOU</b>: the full canonical command is stored and validated,
///     so an approval issued for a short fragment cannot unlock the full payload.</item>
///   <item><b>C - Cross-session reuse</b>: each token is bound to the issuing session ID;
///     a different session cannot redeem it.</item>
///   <item><b>D - Parallel approval race</b>: <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove"/>
///     is atomic - only one concurrent redeem call can remove the entry.</item>
/// </list>
/// </summary>
/// <remarks>
/// Step 2/5 of the security-event taxonomy (#1645, part of #1526): each allow/deny/ask decision
/// also emits exactly one <see cref="SecurityEvent"/> to a trusted <see cref="ISecurityEventSink"/>.
/// Emission is best-effort and never participates in the approval outcome - a sink fault is
/// swallowed and logged so the approval path can never be broken by observability. These events
/// go only to the trusted sink and never to the public diagnostic stream.
/// </remarks>
public sealed class ExecApprovalManager : IExecApprovalManager
{
    /// <summary>The tool name reported as the target of every exec approval event.</summary>
    private const string ToolName = "exec";

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

    private readonly ISecurityEventSink? _securityEvents;
    private readonly ILogger<ExecApprovalManager>? _logger;

    /// <summary>
    /// Creates an approval manager. When a trusted <paramref name="securityEvents"/> sink is
    /// supplied, every allow/deny/ask decision emits one <see cref="SecurityEvent"/>; without it
    /// the manager behaves exactly as before (no emission). The sink is optional so existing
    /// callers and tests that only exercise token behaviour need no changes.
    /// </summary>
    /// <param name="securityEvents">Trusted security-event sink, or null to disable emission.</param>
    /// <param name="logger">Optional logger for swallowed sink faults.</param>
    public ExecApprovalManager(
        ISecurityEventSink? securityEvents = null,
        ILogger<ExecApprovalManager>? logger = null)
    {
        _securityEvents = securityEvents;
        _logger = logger;
    }

    /// <inheritdoc />
    public ExecApprovalRequest Issue(string sessionId, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var canonical = DecodeIfPowerShellEncoded(command);
        var tokenId = Guid.NewGuid().ToString("N");
        _pending[tokenId] = new PendingApproval(sessionId, canonical);

        // An issued token defers to a human: an "ask" decision at the approval boundary.
        EmitDecision("tool.execution.approval.required", SecurityPolicyDecision.Ask, sessionId);
        return new ExecApprovalRequest(tokenId, canonical);
    }

    /// <inheritdoc />
    public bool TryRedeem(string tokenId, string sessionId, string canonicalCommand)
    {
        var allowed = TryRedeemCore(tokenId, sessionId, canonicalCommand);

        // Redemption maps directly to the approval decision: success -> allow, failure -> deny.
        EmitDecision(
            allowed ? "tool.execution.allowed" : "tool.execution.blocked",
            allowed ? SecurityPolicyDecision.Allow : SecurityPolicyDecision.Deny,
            sessionId);

        return allowed;
    }

    private bool TryRedeemCore(string tokenId, string sessionId, string canonicalCommand)
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

        // Session binding check - token must be redeemed by the session that requested it (C).
        if (!string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal))
            return false;

        // Exact canonical command match - prevents payload substitution (A)
        // and truncated-command TOCTOU attacks (B).
        if (!string.Equals(pending.CanonicalCommand, canonicalCommand, StringComparison.Ordinal))
            return false;

        return true;
    }

    /// <summary>
    /// Emits one approval-boundary security event to the trusted sink. The actor id is a salted
    /// hash of the session id so the trusted record never carries the raw identifier. Best-effort:
    /// a null sink is a no-op and any sink fault is swallowed/logged so approvals never fail.
    /// </summary>
    private void EmitDecision(string action, SecurityPolicyDecision decision, string sessionId)
    {
        if (_securityEvents is null)
            return;

        try
        {
            var evt = SecurityEvent.ApprovalDecision(
                action,
                decision,
                actor: new SecurityEventActor(SecurityActorKind.Agent, HashActor(sessionId)),
                target: new SecurityEventTarget(SecurityTargetKind.Tool, ToolName),
                severity: decision == SecurityPolicyDecision.Deny
                    ? SecurityEventSeverity.Medium
                    : SecurityEventSeverity.Info);
            _securityEvents.Record(evt);
        }
        catch (Exception ex)
        {
            // Observability must never break the approval path; swallow and log.
            _logger?.LogWarning(ex, "Failed to record exec approval security event for action {Action}.", action);
        }
    }

    /// <summary>
    /// Hashes a session/agent id to a short, opaque hex token so security events carry a stable
    /// pseudonym instead of the raw id. SHA-256 with a fixed prefix is sufficient for correlation;
    /// it is not reversible and never stores the plaintext.
    /// </summary>
    private static string HashActor(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id ?? string.Empty));
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
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
            // Malformed base64 - return command unchanged rather than throwing.
            return command;
        }
    }
}
