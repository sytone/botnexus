# Gateway Permission Enforcement

Every caller authenticated by the gateway carries a `Permissions` list. Until #2621 that list was
populated by every authentication path and *required* to be non-empty by config validation, but was
never read to make an authorization decision — a caller holding `["sessions:read"]` reached exactly
the same endpoints as one holding `["*"]`.

That is worse than having no permission model at all: a documented, configured, validated control
that does nothing invites operators to rely on it.

This page describes the scope vocabulary, the enforcement seam, and the deliberately staged
rollout.

## Scope vocabulary

Scopes are declared in exactly one place — `GatewayScopes` in
`src/gateway/BotNexus.Gateway.Contracts/Security/GatewayScopes.cs`. A scope is:

```
<resource>:<access>
```

- **resource** — the first path segment beneath `/api`, e.g. `agents`, `sessions`, `cron`.
- **access** — `read` for safe HTTP methods (`GET`, `HEAD`, `OPTIONS`), `write` for everything else.

Three scopes fall outside that pattern:

| Scope | Meaning |
|---|---|
| `*` | Wildcard. Authorizes every scope. Both operator authentication paths grant it. |
| `satellite:connect` | Authorizes the SignalR hub upgrade (`/hub/**`) a satellite uses to connect. |
| `satellite:heartbeat` | Reserved for satellite liveness reporting. |

Deriving the vocabulary from the routing table rather than hand-curating a second list means the two
cannot silently disagree. A fitness test,
`GatewayScopeCoverageFenceArchitectureTests`, fails the build when a controller declares an
authenticated `/api/<resource>` route whose resource is not in the vocabulary.

### Examples

| Request | Required scope |
|---|---|
| `GET /api/agents` | `agents:read` |
| `POST /api/agents/foo/workspace` | `agents:write` |
| `DELETE /api/sessions/abc` | `sessions:write` |
| `WS /hub/gateway` | `satellite:connect` |

## How the decision is made

`GatewayAuthMiddleware` evaluates the scope on every authenticated request, immediately after the
existing agent-allow-list check:

1. Resolve the required scope from the request path and method.
2. A caller holding `*` is authorized for everything.
3. Otherwise the caller must hold the exact required scope.

Two edges deliberately **fail closed**:

- **Unknown scope.** A permission string outside the declared vocabulary — a typo, a retired scope —
  grants nothing at all. It is never read as a wildcard and never quietly skipped in a way that
  leaves the caller authorized.
- **Unmapped path.** If a request maps onto no known resource, no scope can satisfy it and the
  request is refused. A new authenticated endpoint therefore cannot silently bypass the check; it
  fails visibly and the fence flags it at build time.

## Denial is observable

A refusal returns HTTP **403** with a distinguishable body:

```json
{ "error": "permission_denied", "message": "Caller 'reporting-key' does not hold the required permission scope 'agents:write'." }
```

`permission_denied` is distinct from the pre-existing `forbidden` (agent allow-list) and
`unauthenticated` (401) errors, so an operator can tell the three apart without reading source.

Each denial also logs at **Warning**, naming the caller, the refused scope, the method and path, and
the scopes the caller does hold. A silent denial becomes an unexplainable outage; this one is
greppable.

## Rollout: audit first, enforce second

Enforcement is gated behind the feature flag `GatewayPermissionEnforcement`, which is **off by
default**.

Off does **not** mean "skip the check". The scope decision is computed on every authenticated
request regardless of the flag; when the flag is off, a would-be refusal is logged and the request
is served:

```
Gateway permission audit: caller 'satellite:sat_desktop' lacks scope 'agents:read' for GET /api/agents
and would be REFUSED once 'GatewayPermissionEnforcement' is enabled. Granted scopes:
[satellite:connect, satellite:heartbeat]. No action taken - enforcement is currently off.
```

That is the point of the staged rollout. The satellite identity is narrowed to exactly two scopes,
and nobody knows with certainty what every satellite in the field calls. Flipping enforcement on
blind could restrict a live satellite the moment it shipped. Auditing first turns that guess into
data an operator can read out of their own logs.

**Recommended sequence:**

1. Leave the flag off and run normally for a representative period.
2. Grep the logs for `Gateway permission audit`. Each line names a caller and the scope it lacked.
3. Widen the affected keys' `permissions` in `config.json` — or confirm the denial is correct.
4. Once the audit log is quiet, enable enforcement.

```bash
botnexus config get FeatureManagement.GatewayPermissionEnforcement
botnexus config set FeatureManagement.GatewayPermissionEnforcement true
```

Or by environment variable:

```
FeatureManagement__GatewayPermissionEnforcement=true
```

## Compatibility posture

| Identity | Permissions | Effect when enforcement is enabled |
|---|---|---|
| Dev-mode (`gateway-dev`, no keys configured) | `["*"]` | Unaffected — full access. |
| Legacy single `apiKey` | `["*"]` | Unaffected — full access. |
| `gateway.apiKeys[*]` entries | operator-supplied | **Enforced.** Config validation already requires a non-empty list; now that list means something. |
| Satellite (`gateway.satellites[*]`) | `["satellite:connect", "satellite:heartbeat"]` | Hub connection is authorized. The REST surface is **not** — a satellite calling `/api/**` is refused. |

The satellite posture is explicit rather than assumed: its two scopes cover the connection it
exists to make, and the audit period is how an operator confirms nothing else is needed before
enforcement engages. If your satellites do call REST endpoints, the audit log will say so and the
right response is to widen the grant deliberately, not to disable the control.

A feature-flag evaluation fault is treated as **enforcement disabled** and logged. That is not a
fail-open authorization decision — the scope check itself always fails closed — it only means a
broken flag provider cannot lock an operator out of their own gateway.

## Related

- [Dev-Mode Origin Guard](./dev-origin-guard.md) — the sibling opt-in gateway security flag.
- [Configuration](../configuration.md#feature-flags) — the feature-flag inventory.
