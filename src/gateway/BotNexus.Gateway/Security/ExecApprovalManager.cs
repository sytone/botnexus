using System.Collections.Concurrent;
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
    /// The canonical PowerShell parameter name whose unambiguous prefixes select encoded-command mode.
    /// </summary>
    private const string EncodedCommandParameter = "EncodedCommand";

    /// <summary>
    /// Every spelling of the encoded-command flag PowerShell itself accepts, longest first so the
    /// regex alternation prefers the longest match.
    /// <para>
    /// PowerShell resolves a parameter from any unambiguous prefix of its name, so every prefix of
    /// <c>EncodedCommand</c> from <c>e</c> through <c>EncodedCommand</c> selects it (verified
    /// empirically against <c>pwsh</c> 7: <c>-e</c>, <c>-en</c>, <c>-enc</c>, <c>-enco</c>,
    /// <c>-encod</c>, <c>-encode</c>, <c>-encoded</c>, <c>-encodedc</c> ... all execute the payload).
    /// <c>-e</c> is not ambiguous with <c>-ExecutionPolicy</c> because PowerShell's own host parser
    /// special-cases it; <c>-ex</c> and longer are ExecutionPolicy and are deliberately excluded.
    /// <c>ec</c> is PowerShell's documented alias for the same parameter and is not a prefix of the
    /// parameter name, so it is listed separately.
    /// </para>
    /// </summary>
    private static readonly string[] EncodedCommandSpellings =
        Enumerable.Range(1, EncodedCommandParameter.Length)
            .Select(len => EncodedCommandParameter[..len])
            .Reverse()
            .Append("ec")
            .ToArray();

    /// <summary>
    /// Matches PowerShell <c>-EncodedCommand</c>, its alias <c>-ec</c>, and every unambiguous prefix
    /// of the parameter name (<c>-e</c> / <c>-en</c> / <c>-enc</c> / <c>-enco</c> ... ) anywhere in the
    /// command line, so that inline flags like <c>-NoProfile</c> before the encoded flag are handled.
    /// Matching is case-insensitive and accepts both <c>-</c> and <c>/</c> as the flag prefix.
    /// <para>
    /// The base64 run terminates at the first character that cannot appear in base64 (whitespace,
    /// <c>|</c>, <c>;</c>, <c>&amp;</c>, redirection, quote ...) rather than at end-of-string, so a
    /// payload followed by further command text is still decoded. The trailing negative lookahead
    /// pins that boundary so a partial base64 run can never be captured.
    /// </para>
    /// Group 1 captures the base64 payload.
    /// </summary>
    private static readonly Regex PowerShellEncodedPattern = new(
        $@"(?i)(?:^|\s)(?:-|/)(?:{string.Join('|', EncodedCommandSpellings)})\s+([A-Za-z0-9+/]+=*)(?![A-Za-z0-9+/=])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Default lifetime of an unanswered approval before it is pruned and refused.</summary>
    public static readonly TimeSpan DefaultPendingTtl = TimeSpan.FromMinutes(15);

    /// <summary>Default hard cap on concurrently pending approvals.</summary>
    public const int DefaultMaxPending = 256;

    /// <param name="SessionId">The session the token is bound to (invariant C).</param>
    /// <param name="CanonicalCommand">The decoded command the token authorises (invariants A and B).</param>
    /// <param name="IssuedAt">
    /// The instant the approval was issued. An entry strictly older than the configured TTL is
    /// never redeemable and is pruned opportunistically on the next <see cref="Issue"/> (#2746).
    /// </param>
    private sealed record PendingApproval(string SessionId, string CanonicalCommand, DateTimeOffset IssuedAt);

    private readonly ConcurrentDictionary<string, PendingApproval> _pending =
        new(StringComparer.Ordinal);

    private readonly ISecurityEventSink? _securityEvents;
    private readonly ILogger<ExecApprovalManager>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pendingTtl;
    private readonly int _maxPending;

    /// <summary>The number of approvals currently pending (expired-but-unpruned entries included).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Creates an approval manager. When a trusted <paramref name="securityEvents"/> sink is
    /// supplied, every allow/deny/ask decision emits one <see cref="SecurityEvent"/>; without it
    /// the manager behaves exactly as before (no emission). The sink is optional so existing
    /// callers and tests that only exercise token behaviour need no changes.
    /// </summary>
    /// <param name="securityEvents">Trusted security-event sink, or null to disable emission.</param>
    /// <param name="logger">Optional logger for swallowed sink faults.</param>
    /// <param name="timeProvider">Clock used to stamp and expire pending approvals; defaults to the system clock.</param>
    /// <param name="pendingTtl">Lifetime of an unanswered approval; defaults to <see cref="DefaultPendingTtl"/>.</param>
    /// <param name="maxPending">Hard cap on pending approvals; defaults to <see cref="DefaultMaxPending"/>.</param>
    public ExecApprovalManager(
        ISecurityEventSink? securityEvents = null,
        ILogger<ExecApprovalManager>? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? pendingTtl = null,
        int? maxPending = null)
    {
        if (pendingTtl is { } ttl && ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pendingTtl), ttl, "TTL must be positive.");
        if (maxPending is { } cap && cap <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPending), cap, "Cap must be positive.");

        _securityEvents = securityEvents;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pendingTtl = pendingTtl ?? DefaultPendingTtl;
        _maxPending = maxPending ?? DefaultMaxPending;
    }

    /// <inheritdoc />
    public ExecApprovalRequest Issue(string sessionId, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var now = _timeProvider.GetUtcNow();

        // Opportunistic sweep - no timer. Abandoned approvals are reclaimed on the next issuance.
        PruneExpired(now);

        if (_pending.Count >= _maxPending)
        {
            // Refusal is observable on the existing trusted sink rather than being silent growth.
            EmitDecision("tool.execution.approval.refused", SecurityPolicyDecision.Deny, sessionId);
            throw new ExecApprovalCapacityExceededException(_maxPending);
        }

        var canonical = DecodeIfPowerShellEncoded(command);
        var tokenId = Guid.NewGuid().ToString("N");
        _pending[tokenId] = new PendingApproval(sessionId, canonical, now);

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

        // Expiry - an approval older than the configured TTL is never redeemable (#2746).
        if (_timeProvider.GetUtcNow() - pending.IssuedAt > _pendingTtl)
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
    /// Removes every pending approval older than the configured TTL. Called opportunistically from
    /// <see cref="Issue"/> so no background timer is required.
    /// </summary>
    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var entry in _pending)
        {
            if (now - entry.Value.IssuedAt > _pendingTtl)
                _pending.TryRemove(entry.Key, out _);
        }
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
                actor: new SecurityEventActor(SecurityActorKind.Agent, ActorPseudonym.For(sessionId)),
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
    /// Decodes a PowerShell encoded-command payload (<c>-EncodedCommand</c>, <c>-ec</c>, or any
    /// unambiguous prefix such as <c>-e</c> / <c>-en</c> / <c>-enc</c>) to its plaintext form.
    /// PowerShell encodes commands as UTF-16 LE base64, so that encoding is used for decoding.
    /// If the command does not match the encoded-command pattern, it is returned unchanged.
    /// If the base64 payload is malformed, the original command is returned unchanged.
    /// <para>
    /// Any command text following the payload (a pipe, <c>;</c>, <c>&amp;&amp;</c>, redirection or a
    /// further argument) is preserved verbatim after the decoded plaintext so nothing an operator
    /// would be approving is silently dropped.
    /// </para>
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
            var decoded = Encoding.Unicode.GetString(bytes);
            var trailing = command[(match.Index + match.Length)..];
            return decoded + trailing;
        }
        catch (FormatException)
        {
            // Malformed base64 - return command unchanged rather than throwing.
            return command;
        }
    }
}
