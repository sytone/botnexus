using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Actions;

/// <summary>
/// Outcome of an authorization check for a cron command job.
/// </summary>
/// <param name="Allowed"><c>true</c> when the command may be executed.</param>
/// <param name="Reason">Human-readable reason. Always populated for a denial; never empty.</param>
public sealed record CommandAuthorizationDecision(bool Allowed, string Reason)
{
    /// <summary>Creates an allow decision.</summary>
    public static CommandAuthorizationDecision Allow(string reason) => new(true, reason);

    /// <summary>Creates a deny decision.</summary>
    public static CommandAuthorizationDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Authorization seam consulted before a cron <c>command</c> job spawns a shell subprocess (issue #2462).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is gated:</b> this seam gates <b>FIRING</b> - every scheduled or manual execution of a
/// command job, immediately before <c>Process.Start()</c>. It deliberately does <b>not</b> gate
/// <b>AUTHORING</b> (creating or updating a job that carries a <c>shellCommand</c> via the cron tool
/// or HTTP API): authoring is unchanged by this change, and a job whose command is denied can still
/// be created - it will simply fail every run with a recorded, logged denial rather than executing.
/// Gating firing is the security-relevant boundary because a stored command can be executed long
/// after, and repeatedly, by the gateway identity.
/// </para>
/// <para>
/// <b>Vocabulary reuse:</b> this seam does not invent a second policy model. It delegates to the
/// existing tool-boundary policy surface - <see cref="IToolPolicyProvider"/>,
/// <see cref="ToolRiskLevel"/> and <see cref="ToolApprovalFallback"/> - using the same
/// <c>exec</c> tool name that the interactive exec/shell tool boundary is classified under. An
/// operator who sets <c>askFallback: deny</c> for an agent therefore closes the interactive exec
/// path and the unattended cron command path with one switch.
/// </para>
/// </remarks>
public interface ICommandCronAuthorizer
{
    /// <summary>
    /// Decides whether a cron command job may execute <b>this firing</b>.
    /// Implementations must fail closed: any unrecognised, unclassifiable, or unresolvable
    /// situation returns a denial, never an allow.
    /// </summary>
    /// <param name="job">The job about to fire.</param>
    /// <param name="command">The raw shell command string that would be passed to the shell.</param>
    CommandAuthorizationDecision AuthorizeFiring(CronJob job, string command);
}

/// <summary>
/// Default <see cref="ICommandCronAuthorizer"/>. Classifies the command and then applies the
/// existing <see cref="IToolPolicyProvider"/> exec-tool policy, failing closed at every ambiguity.
/// </summary>
/// <remarks>
/// Decision order (firing only - see <see cref="ICommandCronAuthorizer"/> for the
/// authoring/firing distinction):
/// <list type="number">
///   <item>Extract the leading executable token. If none can be extracted the command is
///     <b>unclassifiable</b> and is denied (fail closed, criterion 4).</item>
///   <item>Resolve <see cref="IToolPolicyProvider"/>. If it is unavailable the policy cannot be
///     evaluated, so the command is denied (fail closed - never fail open).</item>
///   <item>If <see cref="IToolPolicyProvider.RequiresApproval"/> is <c>true</c> for the
///     <c>exec</c> tool, consult <see cref="IToolPolicyProvider.GetApprovalFallback"/>: a cron
///     firing is unattended, so no approval workflow can ever service the request.
///     <see cref="ToolApprovalFallback.Deny"/> denies the run;
///     <see cref="ToolApprovalFallback.Allow"/> permits it with an audit log entry, preserving the
///     historical behaviour that existing deployments depend on (#2391).</item>
/// </list>
/// </remarks>
public sealed class ToolPolicyCommandCronAuthorizer : ICommandCronAuthorizer
{
    /// <summary>
    /// The tool name under which the interactive exec/shell boundary is classified by
    /// <see cref="IToolPolicyProvider"/>. Reused verbatim so cron commands and interactive exec
    /// share one policy vocabulary.
    /// </summary>
    internal const string ExecToolName = "exec";

    private readonly IToolPolicyProvider? _policy;
    private readonly ILogger<ToolPolicyCommandCronAuthorizer>? _logger;

    /// <summary>Creates the authorizer.</summary>
    /// <param name="policy">
    /// The shared tool policy provider. May be <c>null</c> when the gateway security stack is not
    /// registered; in that case every command is denied (fail closed).
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public ToolPolicyCommandCronAuthorizer(
        IToolPolicyProvider? policy = null,
        ILogger<ToolPolicyCommandCronAuthorizer>? logger = null)
    {
        _policy = policy;
        _logger = logger;
    }

    /// <inheritdoc />
    public CommandAuthorizationDecision AuthorizeFiring(CronJob job, string command)
    {
        ArgumentNullException.ThrowIfNull(job);

        var executable = TryExtractExecutable(command);
        if (executable is null)
        {
            return CommandAuthorizationDecision.Deny(
                "command is unclassifiable (no executable token could be extracted); failing closed");
        }

        if (_policy is null)
        {
            return CommandAuthorizationDecision.Deny(
                $"no {nameof(IToolPolicyProvider)} is registered, so command '{executable}' cannot be "
                + "classified; failing closed");
        }

        var agentId = job.AgentId?.Value;
        var risk = _policy.GetRiskLevel(ExecToolName);

        if (!_policy.RequiresApproval(ExecToolName, agentId))
        {
            return CommandAuthorizationDecision.Allow(
                $"exec-tool policy does not require approval for agent '{agentId ?? "(none)"}' (risk={risk})");
        }

        // A cron firing is unattended: there is no conversation to route an approval prompt to,
        // so the ask-fallback posture is the decisive policy - exactly as at the tool boundary.
        var fallback = _policy.GetApprovalFallback(ExecToolName, agentId);
        if (fallback == ToolApprovalFallback.Deny)
        {
            return CommandAuthorizationDecision.Deny(
                $"exec-tool policy requires approval (risk={risk}) and the agent's approval fallback is "
                + $"'{ToolApprovalFallback.Deny}'; an unattended cron firing cannot obtain approval");
        }

        _logger?.LogInformation(
            "CommandCronAuthorizer: allowing unattended command '{Executable}' for job '{JobId}' under "
            + "exec-tool approval fallback '{Fallback}' (risk={Risk}). Set askFallback='deny' for agent "
            + "'{AgentId}' to close this path.",
            executable, job.Id, fallback, risk, agentId ?? "(none)");

        return CommandAuthorizationDecision.Allow(
            $"exec-tool approval fallback is '{fallback}' (risk={risk}); allowed with audit record");
    }

    /// <summary>
    /// Extracts the leading executable token from a shell command line, or <c>null</c> when the
    /// command cannot be classified (empty, whitespace, or leading with a shell operator /
    /// redirection / substitution rather than an identifiable program).
    /// </summary>
    internal static string? TryExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var trimmed = command.TrimStart();

        // A command that opens with a shell metacharacter has no identifiable leading program.
        // We refuse to guess: unclassifiable means denied.
        if (trimmed.Length == 0 || "|&;<>()$`{}".Contains(trimmed[0], StringComparison.Ordinal))
            return null;

        var quote = trimmed[0] is '"' or '\'' ? trimmed[0] : '\0';
        if (quote != '\0')
        {
            var close = trimmed.IndexOf(quote, 1);
            if (close <= 1)
                return null;
            var quoted = trimmed[1..close];
            return string.IsNullOrWhiteSpace(quoted) ? null : quoted;
        }

        var end = trimmed.AsSpan().IndexOfAny(" \t\r\n");
        var token = end < 0 ? trimmed : trimmed[..end];
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
