# Agent 365 Observability Export

BotNexus can route its OpenTelemetry **spans** — turn, tool-call, and provider-invocation
spans, plus their child spans such as sub-agent spawns — directly to the
[Microsoft Agent 365 observability](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability)
ingestion endpoint over **raw OTLP/HTTP**.

This is a **direct OTLP integration**: BotNexus takes **no dependency** on any
`Microsoft.Agents.A365.Observability` SDK. It reuses the existing platform telemetry pipeline
(the canonical `BotNexus` meter/activity scope wired by `AddBotNexusTelemetry`) and simply
attaches an **additional** OTLP/HTTP exporter target pointed at the Agent 365 traces route. You
can therefore fan telemetry out to both a private OpenTelemetry collector (via
`telemetry.exporter`) and Agent 365 (via `telemetry.agent365`) at the same time.

## Off by default

Agent 365 export is **disabled by default**. A fresh install never opens any outbound
connection to Agent 365 until an operator explicitly sets `telemetry.agent365.enabled` to
`true` **and** supplies an `endpoint`. No endpoint is ever shipped as a default. This is the
same off-by-default safety contract that governs the generic OTLP exporter.

## Configuration

```json
{
  "telemetry": {
    "enabled": true,
    "agent365": {
      "enabled": true,
      "endpoint": "https://agent365.svc.cloud.microsoft/observabilityService/tenants/<tenantId>/otlp/agents/<agentId>/traces?api-version=1",
      "authHeaderValue": "Bearer <access-token>",
      "headers": {
        "x-custom-header": "value"
      },
      "resource": {
        "serviceName": "my-agent",
        "deploymentEnvironment": "production"
      }
    }
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `agent365.enabled` | bool | `false` | Ship spans to Agent 365 over OTLP/HTTP when `true` and an `endpoint` is set. |
| `agent365.endpoint` | string | _(unset)_ | The Agent 365 OTLP/HTTP **traces** endpoint (`.../otlp/agents/{agentId}/traces?api-version=1`). Required when enabled. |
| `agent365.authHeaderValue` | string | _(unset)_ | Convenience for the `Authorization` header, e.g. `Bearer <token>`. **Secret** — redacted wherever config is logged. |
| `agent365.headers` | object | `{}` | Additional OTLP request headers. **Secrets** — redacted. An explicit `Authorization` here wins over `authHeaderValue`. |
| `agent365.resource.serviceName` | string | `botnexus` | `service.name` resource attribute. |
| `agent365.resource.serviceInstanceId` | string | _(auto)_ | `service.instance.id` resource attribute. Auto-generated per process when unset. |
| `agent365.resource.deploymentEnvironment` | string | _(unset)_ | `deployment.environment` resource attribute. |

The exporter is always sent over `http/protobuf` because the Agent 365 traces route is
HTTP-only; the wire protocol is not operator-configurable for this target.

## Authentication

Agent 365 authenticates every OTLP request with a bearer token on the `Authorization` header.
BotNexus does **not** mint tokens itself — acquire the token out of band with MSAL /
`Microsoft.Identity.Web` and supply it via `agent365.authHeaderValue` (or a full `Authorization`
entry in `agent365.headers`). The relevant OAuth resource is
`9b975845-388f-4429-889e-eab1ef63949c`:

- **S2S (client credentials):** scope `9b975845-388f-4429-889e-eab1ef63949c/.default`, use the
  `/observabilityService/...` route.
- **OBO (delegated):** scope
  `9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite`, use the
  `/observability/...` route.

See the [direct OTel integration guide](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/direct-open-telemetry-integration)
for the full auth recipes.

### Tenant prerequisites

Agent 365 ingestion silently drops data (returns `200 OK` with `partialSuccess: null`) unless
the tenant is fully onboarded:

- A licensed tenant with an **assigned** Microsoft 365 E7 or Microsoft Agent 365 license.
- Tenant consent granted for your agent app.
- The `Agent365.Observability.OtelWrite` app role (S2S) or scope (delegated) granted.

## Delegation visibility (child spans)

Sub-agent spawns surface as **child spans** of the parent turn. This is standard OpenTelemetry
parent linkage: a child `Activity` started while a parent is current (ambient
`Activity.Current`) inherits the parent's trace id and records the parent span id, so a full
agent-to-agent delegation run appears as a single trace tree in Agent 365. BotNexus does not
rearchitect the span seam for this — it verifies and exports the existing parent/child linkage.

## Secrets handling

`agent365.authHeaderValue` and all `agent365.headers` values are treated as secrets. They are
never emitted in logs or config dumps: `Agent365ObservabilityConfig.DescribeForLogging()`
replaces every value with `[REDACTED]` while preserving header keys so operators can confirm
which headers are set.

## Related

- [`docs/configuration.md`](configuration.md) — full `telemetry` section reference.
- [`docs/observability.md`](observability.md) — the platform observability architecture.
- [`docs/extensions/telemetry.md`](extensions/telemetry.md) — the extension telemetry seam.
