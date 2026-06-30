# Security-Event Diagnostics (Trusted-Only Read Path)

**Version:** 1.0  
**Last Updated:** 2026-06-30  
**Status:** Stable

---

## Table of Contents

1. [Overview](#overview)
2. [Why a Separate Stream](#why-a-separate-stream)
3. [The Read Path](#the-read-path)
4. [Authorization (Trusted/Admin Only)](#authorization-trusted-admin-only)
5. [Response Shape](#response-shape)
6. [What Is Never Exposed](#what-is-never-exposed)
7. [OTLP Export Seam (Not Implemented)](#otlp-export-seam-not-implemented)
8. [Related Components](#related-components)

---

## Overview

The gateway records security-relevant events (authentication handshakes, authorization
decisions, human-in-the-loop approvals, tool-execution boundary decisions, secret-reference
access, and similar) as typed `SecurityEvent` records. These events are captured by a trusted,
bounded, in-memory sink -- `RingBufferSecurityEventSink` -- which keeps a recent window of events
without unbounded memory growth.

This page documents the trusted-only READ path that lets an administrator review those buffered
events. It is the final step (Step 5/5) of the security-event taxonomy (issue #1526).

## Why a Separate Stream

Security events are deliberately kept OFF the public activity/diagnostic stream
(`IActivityBroadcaster`, surfaced over SignalR). An untrusted consumer must never be able to read
who was denied, which secret references were touched, or how an authorization decision was made.

For that reason the security-event surface is a SEPARATE stream from general diagnostics:

- General diagnostics (log patterns, memory pressure, threadpool, activity, loops) are served by
  `DiagnosticsController` at `GET /api/diagnostics/*` and are authenticated but not admin-gated.
- Security events are served by a DIFFERENT controller, `SecurityDiagnosticsController`, and are
  additionally admin-gated. They are stored and reviewed independently of general diagnostics.
- No security event is ever published onto the public activity stream, and the read path itself
  never publishes to `IActivityBroadcaster`.

## The Read Path

```
GET /api/diagnostics/security-events
```

Returns a point-in-time snapshot of the buffered security events, most-recent first. The endpoint
reads the trusted sink via `ISecurityEventSink.Snapshot()` and maps each event to a non-sensitive
DTO. When the sink is not enabled, the endpoint returns `404 Not Found`.

This path is independent of the general diagnostics read path: it queries the security ring buffer
only, never the `LogDiagnosticsRingBuffer`, and is reviewable on its own.

## Authorization (Trusted/Admin Only)

Authentication for all `/api/*` paths is enforced by `GatewayAuthMiddleware`, which validates the
caller and stamps the resolved `GatewayCallerIdentity` into `HttpContext.Items`.

The security-event read path mirrors the gateway's admin gate. Where general diagnostics require
only an authenticated caller, this endpoint additionally requires
`GatewayCallerIdentity.IsAdmin == true` -- the same trusted/admin flag the middleware uses in its
agent-authorization check. The gate fails closed:

- Admin caller -> `200 OK` with the buffered events.
- Authenticated but non-admin caller -> `403 Forbidden`, and no event payload is returned.
- No resolved caller identity -> `403 Forbidden` (fail-closed).

## Response Shape

`200 OK` returns a `SecurityEventsResponse`:

```json
{
  "events": [
    {
      "timestampUtc": "2026-06-30T00:00:00+00:00",
      "category": "Approval",
      "action": "exec.command.denied",
      "outcome": "Denied",
      "severity": "Medium",
      "policy": "Deny",
      "control": "Approval",
      "actor": { "kind": "Operator", "id": "a1b2c3d4e5f60718" },
      "target": { "kind": "Tool", "reference": "shell" }
    }
  ],
  "total": 1
}
```

Enum values are surfaced as their string names so the wire shape is stable and human-readable.
Events are ordered most-recent first.

## What Is Never Exposed

`SecurityEvent` records are non-sensitive by construction, and the DTOs preserve that contract:

- Actor ids are pre-hashed pseudonyms (for example a salted, truncated SHA-256 of a caller id),
  never raw identities.
- Targets are non-sensitive references (a tool name, a config section, or a secret REFERENCE) --
  never a secret value.

Combined with the admin gate and the separate stream, this ensures security events are reviewable
by a trusted operator while remaining invisible to the public activity/diagnostic surface.

## OTLP Export Seam (Not Implemented)

A future OpenTelemetry (OTLP) exporter could subscribe to the same trusted sink and push
`SecurityEvent` records to a collector for long-term storage or alerting. This is left as a
documented seam only -- no OTLP wiring is implemented.

If added, the exporter MUST remain a trusted, out-of-band path: it must read from the trusted sink
and must never route security events through `IActivityBroadcaster` or any public diagnostic
channel.

## Related Components

| Component | Location | Role |
| --- | --- | --- |
| `SecurityEvent` | `src/gateway/BotNexus.Gateway.Contracts/Security/SecurityEvent.cs` | The typed security-event record and its enums. |
| `ISecurityEventSink` | `src/gateway/BotNexus.Gateway.Contracts/Security/ISecurityEventSink.cs` | Trusted sink contract (`Record` / `Snapshot`). |
| `RingBufferSecurityEventSink` | `src/gateway/BotNexus.Gateway.Contracts/Security/RingBufferSecurityEventSink.cs` | Bounded, in-memory trusted ring buffer. |
| `SecurityDiagnosticsController` | `src/gateway/BotNexus.Gateway.Api/Controllers/SecurityDiagnosticsController.cs` | Trusted-only read path (`GET /api/diagnostics/security-events`). |
| `GatewayAuthMiddleware` | `src/gateway/BotNexus.Gateway.Api/GatewayAuthMiddleware.cs` | Authentication and the admin gate (`GatewayCallerIdentity.IsAdmin`). |
