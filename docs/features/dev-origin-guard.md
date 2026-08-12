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
loaded into, so no separate provider or registration is needed. Because the handler reads the
flag through `IFeatureManager` on every request, toggling it takes effect **without a gateway
restart** — which also serves as a safety valve if enforcement ever locks someone out.

::: warning The section name is PascalCase, and that is load-bearing
`config.json` is validated at startup against a **closed** schema generated from `PlatformConfig`
(`additionalProperties: false` at the root), so an unrecognised top-level key aborts the gateway
rather than being ignored. `FeatureManagement` is a modelled property pinned to PascalCase with an
explicit `[JsonPropertyName]`, because that is the section name `Microsoft.FeatureManagement`
binds. Writing it as `featureManagement` — or having tooling camelCase it on your behalf — used to
produce `NoAdditionalPropertiesAllowed: #/featureManagement` and a gateway that would not start
(#3036, fixed in 0.44.0: schema normalisation now canonicalises the section name and preserves the
casing of the flag names inside it). Flag names inside the section are matched **verbatim**, so a
misspelling evaluates as absent rather than erroring.
:::

### Setting the flag without hand-editing JSON

The CLI addresses the flag by dotted key and writes it in the correct shape and casing:

```bash
botnexus config get FeatureManagement.GatewayDevOriginEnforcement
botnexus config set FeatureManagement.GatewayDevOriginEnforcement true
```

The environment-variable form is equivalent and is not subject to `config.json` schema validation
at all, which makes it the safest way to toggle the flag on a gateway you cannot afford to have
fail to start:

```bash
FeatureManagement__GatewayDevOriginEnforcement=true
```

### Fail-open by design

Two conditions deliberately treat the guard as disabled at **flag-evaluation time**, so a
misconfigured *allow-list* can never lock you out of a keyless gateway:

1. The flag is absent or `false` (the default).
2. Feature-flag evaluation throws — the fault is logged and the guard is skipped.

This guarantee is scoped to evaluation of the flag on a running gateway. It is not a guarantee
about start-up: configuration that fails schema validation aborts the process before any flag is
ever evaluated, and no fail-open path in this feature can rescue that. Use `botnexus config set`
or the environment-variable form above rather than hand-editing, and see
[Configuration](../configuration.md#feature-flags-featuremanagement) for the validation rules.

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
