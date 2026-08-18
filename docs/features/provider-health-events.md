# Provider health events

> **Audience:** Channel authors and operators who need to know when an LLM provider stops working.
> **Source code:** `src/gateway/BotNexus.Gateway/Providers/WorldEventProviderHealthObserver.cs`

When an upstream provider fails, the gateway publishes a world event naming the provider and the
failure. Channels subscribe and decide for themselves whether to surface it, and how.

## Why this exists

During a GitHub Copilot outage the gateway logged 391 failed credential refreshes over seven hours.
Every Copilot-backed agent stopped responding, and no channel received any signal at all — from a
user's point of view the agents had simply gone quiet, which is indistinguishable from the agents
being broken. The information existed at the point of failure and was destroyed before any caller
could act on it.

Two things caused the silence, and both are now fixed:

- Credential resolution answered with a bare `string?`, where `null` meant "provider is down",
  "provider was never configured" and "no credential" all at once.
- `WorldEventTypes.HealthDegraded` was a declared constant with **no publisher anywhere in the
  codebase**, and the event bus itself was never registered in DI.

## Events

### `health.degraded`

Published when a provider's credential resolution fails repeatedly.

| Payload key | Always present | Meaning |
|---|---|---|
| `provider` | yes | Provider ID, e.g. `github-copilot` |
| `consecutiveFailures` | yes | How many consecutive failures have been observed |
| `firstFailureUtc` | yes | ISO-8601 timestamp of the first failure in this streak |
| `failureClass` | no | Exception type name, e.g. `HttpRequestException` |
| `statusCode` | no | Upstream HTTP status, e.g. `503` |
| `errorMessage` | no | Failure detail |

`statusCode` and `failureClass` are **omitted rather than defaulted** when no status was observed —
for example a DNS or connection-reset failure, where no HTTP status exists. A fabricated `0` would
read as a genuine measurement and misdirect whoever is diagnosing the outage.

### `health.recovered`

Published when a provider that previously went degraded resolves credentials successfully again.
Carries the `provider` key.

Recovery is announced **only if a `health.degraded` event was actually published** for that
provider. A provider that failed once below the threshold and then succeeded never alarmed anyone,
so an unsolicited "recovered" would be the first a channel had heard of it.

## Debouncing

Debouncing is the point of the design, not an optimisation.

- **Threshold** (default 3 consecutive failures) — a single transient `502` is normal operation and
  must not raise an alarm.
- **Cooldown** (default 15 minutes) — a sustained outage publishes once per window, not once per
  retry. Without it, the incident described above would have produced 391 events.
- **Per provider** — one failing provider never triggers or suppresses a signal about another.
- A **success resets the streak**, so intermittent failures spread across successes never accumulate
  into a false outage signal.

An unconfigured provider is a steady state, not a fault. It never publishes and never counts toward
the failure streak — otherwise every host would report a permanent outage of every provider it does
not use.

## Consuming the events

Emission is unconditional; consumption is entirely the channel's choice. Nothing is forced to
subscribe, and publishing to zero subscribers is a supported no-op.

```csharp
worldEventBus.SetSubscriptions("my-agent", [
    new EventSubscription(WorldEventTypes.HealthDegraded)
]);
```

A subscription may also filter on payload values — for example to watch a single provider:

```csharp
worldEventBus.SetSubscriptions("my-agent", [
    new EventSubscription(
        WorldEventTypes.HealthDegraded,
        new Dictionary<string, string> { ["provider"] = "github-copilot" })
]);
```

## Distinguishing an outage from a missing credential

`GatewayAuthManager.ResolveCredentialAsync` returns a `ProviderCredentialOutcome` carrying the
reason:

| Status | Meaning |
|---|---|
| `Resolved` | A credential was obtained |
| `NotConfigured` | No credential is configured — a steady state, never an outage |
| `RefreshFailed` | A credential exists but refreshing it failed — a provider fault |

Use `IsProviderFault` to test for the outage case. `GetApiKeyAsync` remains available as the
convenience projection that drops the reason and returns `string?`.
