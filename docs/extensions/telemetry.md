# Extension Telemetry

Extensions get the **same telemetry seam the platform core uses** — there is no privileged,
internal-only path. An extension can:

1. **Emit metrics** (counters, histograms, up/down counters, observable gauges) through the
   injected [`IExtensionMetrics`](#metrics-iextensionmetrics) handle. Every instrument is
   automatically prefixed into the extension's own `botnexus.ext.<id>.*` namespace and validated so
   it can never collide with a platform instrument such as `botnexus.turns.*`.
2. **Record durable usage telemetry** (coarse, monotonic per-entity counters plus provenance and a
   pin flag) through the injected [`IExtensionUsageTelemetry`](#durable-usage-iextensionusagetelemetry)
   handle, backed by the **shared** usage-telemetry SQLite store — the extension never news up its
   own database file, and its rows are isolated to a namespace equal to the extension id.

Both handles are obtained from a single injected factory, [`IExtensionTelemetryFactory`](#the-factory-iextensiontelemetryfactory).

Metrics still land on the single canonical `BotNexus` meter scope (see
[Observability](../observability.md)), so an exporter that subscribes with `AddMeter("BotNexus")`
observes extension instruments exactly as it observes platform instruments — the seam is
*namespaced*, not privileged.

---

## The factory: `IExtensionTelemetryFactory`

The host registers `IExtensionTelemetryFactory` as a singleton during startup. Because an
extension's id is only known to the extension itself, you resolve the factory and ask it for
handles bound to your id:

```csharp
using BotNexus.Gateway.Abstractions.Extensions;

public sealed class MyToolContributor
{
    private readonly IExtensionMetrics _metrics;
    private readonly IExtensionUsageTelemetry _usage;

    public MyToolContributor(IExtensionTelemetryFactory telemetry)
    {
        // "my-ext" is this extension's id — the instrument namespace segment and the
        // durable usage consumer namespace.
        _metrics = telemetry.MetricsFor("my-ext");
        _usage = telemetry.UsageFor("my-ext");
    }
}
```

Extension ids must be lowercase, digits, and hyphens only (no dots, spaces, or uppercase) so the id
forms a single well-formed instrument-namespace segment. An invalid id throws `ArgumentException`.

---

## Metrics: `IExtensionMetrics`

`IExtensionMetrics` mirrors the platform `IMetrics` facade, but you pass only the **leaf** name of
the instrument — the handle prepends `botnexus.ext.<id>.`:

```csharp
// Creates instrument "botnexus.ext.my-ext.loads"
private readonly Counter<long> _loads = _metrics.CreateCounter<long>(
    "loads", description: "Number of times my extension loaded a resource.");

public void OnLoad() => _loads.Add(1);
```

### Guardrail: you cannot reach into a platform namespace

The leaf name is validated. Passing a name that starts with `botnexus.` is rejected so an extension
can never shadow or spoof a platform instrument:

```csharp
// Throws ArgumentException: reaches into the reserved platform namespace "botnexus.*".
_metrics.CreateCounter<long>("botnexus.turns.total");
```

Only the platform core may emit under `botnexus.turns.*`, `botnexus.host.*`, and the other
first-party areas.

---

## Durable usage: `IExtensionUsageTelemetry`

Use durable usage telemetry to answer "which of my extension's entities earn their keep" — coarse,
monotonic counters keyed by an entity name, plus a `created_by` provenance stamp, a `last_used_at`
freshness signal, and a `pinned` flag a future curator honours. All rows are isolated to the
consumer namespace equal to your extension id inside the **shared** store, so you never provision
your own SQLite file:

```csharp
// Increment a counter for an entity (creates the row on first touch).
await _usage.IncrementAsync("email-triage", "use");

// Stamp provenance when an entity is created.
await _usage.RecordCreatedAsync("email-triage", createdBy: agentId);

// Protect an entity from a future archive pass.
await _usage.SetPinnedAsync("email-triage", pinned: true);

// Read back.
var record = await _usage.GetAsync("email-triage");
long uses = record?.GetCounter("use") ?? 0;
```

Recording is **best-effort from the caller's perspective**: hot-path code that records usage must
swallow telemetry failures so a telemetry outage can never break real work. (The underlying store
does not swallow exceptions, so tests can assert failure behaviour directly — the discipline lives
at your call site.)

---

## Worked example: the Skills extension

The [Skills extension](skills.md) is the reference consumer of the shared durable usage primitive.
It records `view`, `use`, and `patch` counters plus provenance per skill, isolated under the
namespace `"skills"`, and surfaces them through the `/api/skills/telemetry` endpoint. Its
`SqliteSkillUsageStore` is a facade over the same shared `IUsageTelemetry` store the extension seam
exposes — an extension built today would obtain the equivalent handle straight from
`IExtensionTelemetryFactory.UsageFor("skills")` rather than writing a bespoke facade:

```csharp
// Equivalent to SqliteSkillUsageStore.RecordUseAsync(skillName), via the injected seam:
await _usage.IncrementAsync(skillName, "use");
```

For metrics, a skill-loading counter would read:

```csharp
// Instrument "botnexus.ext.skills.loads"
var loads = _metrics.CreateCounter<long>("loads");
loads.Add(1);
```

---

## Summary

| Need | Inject | Call |
| --- | --- | --- |
| Emit a metric | `IExtensionMetrics` (from `IExtensionTelemetryFactory.MetricsFor(id)`) | `CreateCounter("leaf")` → `botnexus.ext.<id>.leaf` |
| Record durable usage | `IExtensionUsageTelemetry` (from `IExtensionTelemetryFactory.UsageFor(id)`) | `IncrementAsync(key, counter)` |

Both paths use the exact abstractions the platform core uses; the seam only adds namespace
prefixing and isolation so extensions coexist safely.
