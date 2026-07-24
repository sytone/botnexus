# Dev-Mode Browser-Origin Guard

**Version:** 1.0
**Status:** Enabled by default — operators may explicitly opt out

---

## Overview

When BotNexus runs with **no API key configured** (development / no-key mode), the gateway
auto-grants every caller a full admin identity (`gateway-dev`, `Permissions=["*"]`,
`IsAdmin=true`). That convenience is also an attack surface: without an Origin check a malicious
web page loaded in the operator's browser could silently drive the admin identity from an
arbitrary origin (a DNS-rebind / CSRF class of attack).

The **dev-mode browser-origin guard** (`#1931`) closes that hole by rejecting requests in
no-key mode whose `Origin` header is present and **not** on an allow-list, *before* the admin
identity is granted. As of `#1946` the guard is **enabled by default** so keyless gateways
are protected out-of-the-box.

- Requests with **no** `Origin` header (curl, CLI, native SignalR / non-browser clients) are
  always allowed — the guard only constrains browser-originated requests.
- A **present** `Origin` must exactly match one of the allow-listed origins.
- The allow-list is sourced from `Gateway.Cors.AllowedOrigins` (the same list the CORS policy
  uses), defaulting to `http://localhost:5005` when unconfigured.

The guard has no effect once an API key is configured — keyed requests are authenticated by the
key, not the origin.

## Feature flag (enabled by default)

The guard is controlled by a
[`Microsoft.FeatureManagement`](https://github.com/microsoft/FeatureManagement-Dotnet) flag,
`GatewayDevOriginEnforcement`. As of `#1946` the flag is **ON when unspecified**: a fresh keyless
gateway enforces the browser-origin allow-list without any configuration.

> **Upgrade impact:** if you run a keyless gateway and reach the UI over a **non-localhost
> origin** (a LAN hostname, a reverse proxy, a [netbird](https://netbird.io/) domain, or any
> `https://` fronting), you **must** add that origin to `Gateway.Cors.AllowedOrigins` before
> upgrading, or the browser will be rejected on the next gateway start. Localhost
> (`http://localhost:5005`) is allowed by default.

Operators who need the previous behavior can **explicitly opt out**:

```jsonc
{
  "FeatureManagement": {
    // Explicitly disable the browser-origin guard on the dev-mode (no-key) admin grant.
    // Omit this line (or set it to true) to keep the secure default.
    "GatewayDevOriginEnforcement": false
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

### Default-on resolution

`IFeatureManager.IsEnabledAsync` reports both an *absent* flag and an explicit `false` as
disabled, so the handler also consults `IConfiguration` to tell them apart:

- Flag **unspecified** — guard **enabled** (the `#1946` default).
- Flag explicitly `true` — guard enabled.
- Flag explicitly `false` — guard disabled (operator opt-out).

### Fail-open by design

Two conditions deliberately treat the guard as disabled so a misconfiguration can never brick a
keyless gateway:

1. The flag is explicitly set to `false` (operator opt-out).
2. Feature-flag evaluation throws — the fault is logged and the guard is skipped.

## Doctor recommendation

`botnexus doctor config` includes a `devmode-origin-enforcement` check that fires when the
gateway is keyless and the flag has been **explicitly disabled** (`GatewayDevOriginEnforcement:
false`) — the only state that leaves a keyless gateway exposed now that the guard is on by
default. Applying its fix:

- seeds `gateway.cors.allowedOrigins` with `http://localhost:5005` **only if unset** (existing
  origins are preserved), and
- sets `FeatureManagement.GatewayDevOriginEnforcement` to `true`.

> **Before re-enabling:** if you reach the UI over a non-localhost origin, add that origin to
> `gateway.cors.allowedOrigins` first, or you will be locked out on the next restart.

## Rollout status

1. ~~Off by default~~ — initial staged rollout (`#1931`).
2. **On by default** (current, `#1946`) — keyless gateways are protected out-of-the-box;
   operators may explicitly opt out with `GatewayDevOriginEnforcement: false`.

## Related components

- `ApiKeyGatewayAuthHandler` — implements the guard and the `GatewayDevOriginEnforcement` flag
  check.
- `DevOriginEnforcementCheck` — the `botnexus doctor config` recommendation.
- [Security-Event Diagnostics](./security-event-diagnostics.md) — rejected requests emit a
  `gateway.auth.rejected` security event.
