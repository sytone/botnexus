using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Trusted-only read path for buffered <see cref="SecurityEvent"/> records (Step 5/5 of the
/// security-event taxonomy, issue #1648, part of #1526).
/// </summary>
/// <remarks>
/// <para>
/// This controller is the SEPARATE, trusted security-event surface called for in #1526: it lets
/// an administrator review the recent security events captured by the trusted
/// <see cref="ISecurityEventSink"/> (the <c>RingBufferSecurityEventSink</c> from Step 1/#1532),
/// independently of the general diagnostics surface exposed by
/// <see cref="DiagnosticsController"/>. It is deliberately a DIFFERENT controller and a DIFFERENT
/// stream: security events are never published onto the public activity/diagnostic SignalR stream
/// (<c>IActivityBroadcaster</c>), and this read path never publishes to it either.
/// </para>
/// <para>
/// Authorization mirrors the gateway's admin gate. Authentication for all <c>/api/*</c> paths is
/// enforced by <c>GatewayAuthMiddleware</c>, which stamps the resolved
/// <see cref="GatewayCallerIdentity"/> into <c>HttpContext.Items</c>. Unlike the general
/// diagnostics endpoints (authenticated-but-not-admin), this read path additionally requires
/// <see cref="GatewayCallerIdentity.IsAdmin"/> -- the same trusted/admin flag the middleware uses
/// in its agent-authorization check -- and fails closed (403) when no admin identity is present.
/// </para>
/// <para>
/// OTLP export seam (not implemented, #1648 leaves this as a documented seam): a future exporter
/// could subscribe to the same trusted sink and push <see cref="SecurityEvent"/> records to an
/// OpenTelemetry collector. It MUST remain a trusted, out-of-band path -- it must never route
/// security events through <c>IActivityBroadcaster</c> or any public diagnostic channel. No OTLP
/// wiring is added here; only the seam is recorded.
/// </para>
/// </remarks>
[ApiController]
[Route("api/diagnostics")]
public sealed class SecurityDiagnosticsController(
    ISecurityEventSink? securityEvents = null) : ControllerBase
{
    private const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";

    private readonly ISecurityEventSink? _securityEvents = securityEvents;

    /// <summary>
    /// Returns a point-in-time snapshot of the buffered security events, most-recent first.
    /// Trusted/admin only: a non-admin caller (or a request with no resolved caller identity)
    /// receives 403 Forbidden and no event payload.
    /// </summary>
    /// <remarks>
    /// This is the trusted-only review surface for the security-event taxonomy. It reads the
    /// in-memory ring buffer via <see cref="ISecurityEventSink.Snapshot"/> and maps each event to
    /// a non-sensitive DTO. <see cref="SecurityEvent"/> records are already free of secret values
    /// by construction (actor ids are pre-hashed; targets are references, never secret values).
    /// </remarks>
    [HttpGet("security-events")]
    public IActionResult GetSecurityEvents()
    {
        if (!IsAdminCaller())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller is not authorized for security diagnostics." });

        if (_securityEvents is null)
            return NotFound("Security-event diagnostics not enabled.");

        var snapshot = _securityEvents.Snapshot();
        var events = new List<SecurityEventDto>(snapshot.Count);
        foreach (var evt in snapshot)
            events.Add(ToDto(evt));

        return Ok(new SecurityEventsResponse
        {
            Events = events,
            Total = events.Count
        });
    }

    /// <summary>
    /// True only when the request carries an authenticated caller identity flagged as an
    /// administrator. Fails closed: a missing or non-admin identity returns false. Mirrors the
    /// admin gate used by <c>GatewayAuthMiddleware</c> and the caller-identity lookup used by
    /// <c>SessionsController</c>.
    /// </summary>
    private bool IsAdminCaller()
    {
        var items = HttpContext?.Items;
        return items is not null &&
               items.TryGetValue(CallerIdentityItemKey, out var identityValue) &&
               identityValue is GatewayCallerIdentity identity &&
               identity.IsAdmin;
    }

    private static SecurityEventDto ToDto(SecurityEvent evt) => new()
    {
        TimestampUtc = evt.TimestampUtc,
        Category = evt.Category.ToString(),
        Action = evt.Action,
        Outcome = evt.Outcome.ToString(),
        Severity = evt.Severity.ToString(),
        Policy = evt.Policy.ToString(),
        Control = evt.Control.ToString(),
        Actor = evt.Actor is null
            ? null
            : new SecurityEventActorDto { Kind = evt.Actor.Kind.ToString(), Id = evt.Actor.Id },
        Target = evt.Target is null
            ? null
            : new SecurityEventTargetDto { Kind = evt.Target.Kind.ToString(), Reference = evt.Target.Reference }
    };
}

/// <summary>
/// A non-sensitive DTO for a single buffered <see cref="SecurityEvent"/> returned by the trusted
/// security-event read path. All enum values are surfaced as their string names so the wire shape
/// is stable and human-readable.
/// </summary>
public sealed class SecurityEventDto
{
    /// <summary>When the event occurred (UTC).</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The broad classification of the event (e.g. <c>Approval</c>, <c>Auth</c>).</summary>
    public required string Category { get; init; }

    /// <summary>The dotted, stable action identifier (e.g. <c>tool.execution.blocked</c>).</summary>
    public required string Action { get; init; }

    /// <summary>The result of the action (e.g. <c>Success</c>, <c>Denied</c>).</summary>
    public required string Outcome { get; init; }

    /// <summary>The triage importance of the event (e.g. <c>Medium</c>, <c>High</c>).</summary>
    public required string Severity { get; init; }

    /// <summary>The control's decision when the event is a policy evaluation (e.g. <c>Deny</c>).</summary>
    public required string Policy { get; init; }

    /// <summary>The control family responsible for the event (e.g. <c>Approval</c>).</summary>
    public required string Control { get; init; }

    /// <summary>The initiating principal, if known. Ids are pre-hashed pseudonyms, never raw identities.</summary>
    public SecurityEventActorDto? Actor { get; init; }

    /// <summary>The acted-upon resource, if applicable. References are non-sensitive, never secret values.</summary>
    public SecurityEventTargetDto? Target { get; init; }
}

/// <summary>
/// A non-sensitive DTO for the initiating principal of a <see cref="SecurityEvent"/>.
/// </summary>
public sealed class SecurityEventActorDto
{
    /// <summary>The category of principal (e.g. <c>Operator</c>, <c>Agent</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>An opaque, already-hashed identifier for the principal -- never a raw secret.</summary>
    public required string Id { get; init; }
}

/// <summary>
/// A non-sensitive DTO for the resource a <see cref="SecurityEvent"/> acted upon.
/// </summary>
public sealed class SecurityEventTargetDto
{
    /// <summary>The category of target (e.g. <c>Session</c>, <c>Tool</c>, <c>SecretRef</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>A non-sensitive reference to the target -- never a secret value.</summary>
    public required string Reference { get; init; }
}

/// <summary>
/// Response wrapper for the trusted security-event read path.
/// </summary>
public sealed class SecurityEventsResponse
{
    /// <summary>The buffered security events, most-recent first.</summary>
    public required IReadOnlyList<SecurityEventDto> Events { get; init; }

    /// <summary>The number of events returned.</summary>
    public required int Total { get; init; }
}
