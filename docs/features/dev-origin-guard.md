# Dev-Mode Browser-Origin Guard

**Version:** 1.0
**Status:** Opt-in (default off) — staged rollout

---

## Overview

When BotNexus runs with **no API key configured** (development / no-key mode), the gateway
auto-grants every caller a full admin identity (`gateway-dev`, `Permissions=["*"]`,
`IsAdmin=true`). That convenience is also an attack surface: without an Origin check a malicious
web page loaded in the operator's browser could silently drive the admin identity from an
arbitrary origin (a DNS-rebind / CSRF class of attack).

The **dev-mode browser-origin guard** (`#1931`) closes that hole by rejecting requests in
no-key mode whose `Origin` header is present and **not** on an allow-list, *before* the admin
identity is granted.

- Requests with **no** `Origin` header (curl, CLI, native SignalR / non-browser clients) are
  always allowed — the guard only constrains browser-originated requests.
- A **present** `Origin` must exactly match one of the allow-listed origins.
- The allow-list is sourced from `Gateway.Cors.AllowedOrigins` (the same list the CORS policy
  uses), defaulting to `http://localhost:5005` when unconfigured.

The guard has no effect once an API key is configured — keyed requests are authenticated by the
key, not the origin.

## Opt-in via feature flag (default OFF)

Introducing this guard as a hard default would break a real class of keyless users: anyone who
reaches the portal over a non-localhost origin (a LAN hostname, a reverse proxy, a
[netbird](https://netbird.io/) domain, or any `https://` fronting) would be locked out of the
UI on the next gateway restart, with no in-band warning.

To make the rollout safe, the guard is gated behind a
[`Microsoft.FeatureManagement`](https://github.com/microsoft/FeatureManagement-Dotnet) flag that
is **off by default**:

```jsonc
{
  "FeatureManagement": {
    // Enforce the browser-origin allow-list on the dev-mode (no-key) admin grant.
    // OFF by default; enable once Gateway.Cors.AllowedOrigins lists every origin you
    // reach the UI from, or you will be locked out on restart.
    "GatewayDevOriginEnforcement": true
  },
  "gateway": {
    "cors": {
      "allowedOrigins": [
        "http://localhost:5005",
        "https://portal.example.com"
      ]
    }
  }
}
```

The `FeatureManagement` section binds onto the same `IConfiguration` that `config.json` is
loaded into, so no additional plumbing is required. Because the handler reads the flag through
`IFeatureManager` on every request, toggling it takes effect **without a gateway restart** —
which also serves as a safety valve if enforcement ever locks someone out.

### Fail-open by design

Two conditions deliberately treat the guard as disabled so a misconfiguration can never brick a
keyless gateway:

1. The flag is absent or `false` (the default).
2. Feature-flag evaluation throws — the fault is logged and the guard is skipped.

## Doctor recommendation

`botnexus doctor config` includes a `devmode-origin-enforcement` check that fires when the
gateway is keyless and the flag is not yet enabled. Applying its fix:

- seeds `gateway.cors.allowedOrigins` with `http://localhost:5005` **only if unset** (existing
  origins are preserved), and
- sets `FeatureManagement.GatewayDevOriginEnforcement` to `true`.

> **Before enabling:** if you reach the UI over a non-localhost origin, add that origin to
> `gateway.cors.allowedOrigins` first, or you will be locked out on the next restart.

## Rollout plan

1. **Off by default** (current) — behavior identical to pre-guard; doctor surfaces the opt-in.
2. **Flip the default on** in a later release, once deployments have had time to configure
   `allowedOrigins`.

## Related components

- `ApiKeyGatewayAuthHandler` — implements the guard and the `GatewayDevOriginEnforcement` flag
  check.
- `DevOriginEnforcementCheck` — the `botnexus doctor config` recommendation.
- [Security-Event Diagnostics](./security-event-diagnostics.md) — rejected requests emit a
  `gateway.auth.rejected` security event.
