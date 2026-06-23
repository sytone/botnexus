namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// The broad classification of a security-relevant event. Each value maps to a
/// distinct area of the gateway's security posture so that events can be filtered
/// and aggregated by concern (e.g. all <see cref="Approval"/> decisions, or every
/// <see cref="Auth"/> handshake outcome).
/// </summary>
/// <remarks>
/// Part of the security-event taxonomy (#1526). This is an internal observability
/// vocabulary: it records who did what, under which control, and with what outcome,
/// at the gateway's enforcement boundaries. It is intentionally NOT placed on the
/// public activity/diagnostic stream -- see <see cref="ISecurityEventSink"/>.
/// </remarks>
public enum SecurityEventCategory
{
    /// <summary>Authentication: identity handshakes and credential validation.</summary>
    Auth,
    /// <summary>Authorization: access/scope checks on a known identity.</summary>
    Authorization,
    /// <summary>Approval: human-in-the-loop allow/deny/ask decisions (e.g. exec approval).</summary>
    Approval,
    /// <summary>Tool execution boundary events (requested, blocked, vetoed).</summary>
    Tool,
    /// <summary>Plugin / extension lifecycle (install, load, enable).</summary>
    Plugin,
    /// <summary>Secret handling (redaction triggers, secret-reference access).</summary>
    Secret,
    /// <summary>Channel boundary events (inbound/outbound message trust decisions).</summary>
    Channel,
    /// <summary>Configuration access or change with a security dimension.</summary>
    Config,
    /// <summary>General audit records that do not fit a more specific category.</summary>
    Audit,
    /// <summary>Telemetry/observability-internal events about the security pipeline itself.</summary>
    Telemetry
}

/// <summary>
/// The result of the action that produced a <see cref="SecurityEvent"/>.
/// </summary>
public enum SecurityEventOutcome
{
    /// <summary>The action completed and was permitted.</summary>
    Success,
    /// <summary>The action failed for a non-policy reason (e.g. a handshake error).</summary>
    Failure,
    /// <summary>The action was explicitly denied by a control/policy.</summary>
    Denied,
    /// <summary>The action errored unexpectedly (an exceptional condition).</summary>
    Error
}

/// <summary>
/// The relative importance of a <see cref="SecurityEvent"/> for triage and alerting.
/// </summary>
public enum SecurityEventSeverity
{
    /// <summary>Informational; routine, expected security activity.</summary>
    Info,
    /// <summary>Low: minor, rarely actionable on its own.</summary>
    Low,
    /// <summary>Medium: worth review (e.g. a denied approval).</summary>
    Medium,
    /// <summary>High: a likely security-relevant failure (e.g. a failed auth handshake).</summary>
    High,
    /// <summary>Critical: an event that demands immediate attention.</summary>
    Critical
}

/// <summary>
/// The decision a control rendered, when the event represents a policy evaluation.
/// </summary>
public enum SecurityPolicyDecision
{
    /// <summary>No policy decision applies to this event.</summary>
    None,
    /// <summary>The control permitted the action.</summary>
    Allow,
    /// <summary>The control rejected the action.</summary>
    Deny,
    /// <summary>The control deferred to a human (allow-with-approval).</summary>
    Ask
}

/// <summary>
/// The control family responsible for the event -- which kind of security mechanism
/// produced or evaluated it.
/// </summary>
public enum SecurityControlFamily
{
    /// <summary>No specific control family.</summary>
    None,
    /// <summary>Authentication controls.</summary>
    Auth,
    /// <summary>Authorization / scope controls.</summary>
    Authorization,
    /// <summary>Human-in-the-loop approval controls.</summary>
    Approval,
    /// <summary>Sandboxing / isolation controls.</summary>
    Sandbox,
    /// <summary>Secret-handling controls (redaction, vaulting).</summary>
    Secret,
    /// <summary>Supply-chain controls (plugin/extension integrity).</summary>
    SupplyChain
}

/// <summary>The kind of principal that initiated a security event.</summary>
public enum SecurityActorKind
{
    /// <summary>A human operator (e.g. Jon approving an exec command).</summary>
    Operator,
    /// <summary>A satellite/remote node.</summary>
    Node,
    /// <summary>An agent acting within the gateway.</summary>
    Agent,
    /// <summary>A plugin / extension.</summary>
    Plugin,
    /// <summary>A channel sender (inbound message origin).</summary>
    ChannelSender,
    /// <summary>The system itself (no external principal).</summary>
    System
}

/// <summary>The kind of resource a security event acted upon.</summary>
public enum SecurityTargetKind
{
    /// <summary>The gateway process / host.</summary>
    Gateway,
    /// <summary>A paired device.</summary>
    Device,
    /// <summary>A tool invocation.</summary>
    Tool,
    /// <summary>A reference to a secret (never the secret value itself).</summary>
    SecretRef,
    /// <summary>A configuration section or key.</summary>
    Config,
    /// <summary>A session.</summary>
    Session
}

/// <summary>
/// The principal that initiated a <see cref="SecurityEvent"/>.
/// </summary>
/// <param name="Kind">The category of principal.</param>
/// <param name="Id">
/// An opaque or already-hashed identifier for the principal. Callers are responsible
/// for hashing/anonymising before construction -- this record never stores a raw secret
/// or reverses an id.
/// </param>
public sealed record SecurityEventActor(SecurityActorKind Kind, string Id);

/// <summary>
/// The resource a <see cref="SecurityEvent"/> acted upon.
/// </summary>
/// <param name="Kind">The category of target.</param>
/// <param name="Reference">
/// A non-sensitive reference to the target (e.g. a tool name, a config section name,
/// or a secret <em>reference</em> -- never a secret value).
/// </param>
public sealed record SecurityEventTarget(SecurityTargetKind Kind, string Reference);

/// <summary>
/// A canonical, typed record of a security-relevant event observed at a gateway
/// enforcement boundary. Captures who (<see cref="Actor"/>) did what (<see cref="Action"/>)
/// to what (<see cref="Target"/>), under which control (<see cref="Control"/>), with what
/// policy decision (<see cref="Policy"/>) and outcome (<see cref="Outcome"/>).
/// </summary>
/// <remarks>
/// Step 1/5 of the security-event taxonomy (#1532, part of #1526). These events are
/// emitted only via a trusted <see cref="ISecurityEventSink"/> and are deliberately kept
/// off the public activity/diagnostic stream. Enforcement-point wiring is added in
/// follow-up steps; this type and its sink are the foundational primitives.
/// </remarks>
/// <param name="Category">The broad classification of the event.</param>
/// <param name="Action">
/// A dotted, stable action identifier (e.g. <c>tool.execution.blocked</c>,
/// <c>gateway.auth.failed</c>) describing precisely what happened.
/// </param>
/// <param name="Outcome">The result of the action.</param>
/// <param name="Severity">The triage importance of the event.</param>
/// <param name="Actor">The initiating principal, if known.</param>
/// <param name="Target">The acted-upon resource, if applicable.</param>
/// <param name="Policy">The control's decision, if the event is a policy evaluation.</param>
/// <param name="Control">The control family responsible for the event.</param>
public sealed record SecurityEvent(
    SecurityEventCategory Category,
    string Action,
    SecurityEventOutcome Outcome,
    SecurityEventSeverity Severity,
    SecurityEventActor? Actor = null,
    SecurityEventTarget? Target = null,
    SecurityPolicyDecision Policy = SecurityPolicyDecision.None,
    SecurityControlFamily Control = SecurityControlFamily.None)
{
    /// <summary>
    /// When the event occurred (UTC). Defaults to the construction time; set explicitly via an
    /// object initializer (e.g. <c>new SecurityEvent(...) { TimestampUtc = ts }</c>) when replaying.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Builds an <see cref="SecurityEventCategory.Approval"/> event for a human-in-the-loop
    /// allow/deny/ask decision. The <paramref name="decision"/> drives the outcome:
    /// <see cref="SecurityPolicyDecision.Allow"/> -> <see cref="SecurityEventOutcome.Success"/>,
    /// <see cref="SecurityPolicyDecision.Deny"/> -> <see cref="SecurityEventOutcome.Denied"/>,
    /// anything else -> <see cref="SecurityEventOutcome.Success"/> (an ask that was satisfied).
    /// </summary>
    public static SecurityEvent ApprovalDecision(
        string action,
        SecurityPolicyDecision decision,
        SecurityEventActor? actor = null,
        SecurityEventTarget? target = null,
        SecurityEventSeverity severity = SecurityEventSeverity.Medium) =>
        new(
            SecurityEventCategory.Approval,
            action,
            decision == SecurityPolicyDecision.Deny ? SecurityEventOutcome.Denied : SecurityEventOutcome.Success,
            severity,
            Actor: actor,
            Target: target,
            Policy: decision,
            Control: SecurityControlFamily.Approval);

    /// <summary>
    /// Builds an <see cref="SecurityEventCategory.Auth"/> event for an authentication
    /// handshake outcome. A failure defaults to <see cref="SecurityEventSeverity.High"/>;
    /// a success defaults to <see cref="SecurityEventSeverity.Info"/> unless overridden.
    /// </summary>
    public static SecurityEvent AuthOutcome(
        string action,
        bool success,
        SecurityEventActor? actor = null,
        SecurityEventSeverity? severity = null) =>
        new(
            SecurityEventCategory.Auth,
            action,
            success ? SecurityEventOutcome.Success : SecurityEventOutcome.Failure,
            severity ?? (success ? SecurityEventSeverity.Info : SecurityEventSeverity.High),
            Actor: actor,
            Control: SecurityControlFamily.Auth);
}
