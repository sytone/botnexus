# Proposal: Adopt OpenTelemetry + Serilog for Observability

**Author:** Leela (Lead / Architect)  
**Date:** 2026-04-08  
**Status:** Draft — awaiting review  
**Requested by:** Jon Bullen  
**Scope:** Cross-cutting · all active projects

---

## 1. Current State

### 1.1 Logging Framework

The platform uses **Microsoft.Extensions.Logging** with the built-in **Console** provider. There is no structured logging, no OTel tracing, and no Serilog integration anywhere in the codebase.

| Metric | Count |
|--------|-------|
| `ILogger<T>` constructor injection sites | 3 |
| `_logger.Log*` call sites | 73 across 20 files |
| `Console.WriteLine` usage (CLI/TUI UX) | 120+ across 12 files |
| `ActivitySource` / `Activity` usage | 0 |
| Serilog references | 0 |
| OpenTelemetry references | 0 |

### 1.2 Projects with Logging Package References

Only **4 projects** explicitly reference `Microsoft.Extensions.Logging.Abstractions 10.0.0-preview.3.25171.5`:

| Project | Has M.E.L.Abstractions |
|---------|:----------------------:|
| BotNexus.Gateway | ✅ |
| BotNexus.Gateway.Sessions | ✅ |
| BotNexus.Providers.Core | ✅ |
| BotNexus.Channels.Core | ✅ |

All other projects either get logging transitively through framework references or don't log at all.

### 1.3 Existing Correlation Infrastructure

The platform already has a `CorrelationIdMiddleware` in `BotNexus.Gateway.Api` that generates/propagates `X-Correlation-Id` headers. This is a natural integration point for OTel trace context but currently operates independently with no tie to any tracing system.

### 1.4 Host Configuration

- **Gateway API** (`Program.cs` line 99): Creates a bootstrap logger via `LoggerFactory.Create(logging => logging.AddConsole())`.
- **CLI**: No logging configured.
- **CodingAgent**: Uses `NullLogger<T>` — intentionally silent.

### 1.5 All Active Projects (18 src + 10 test)

**Source projects:**

| # | Project | Layer |
|---|---------|-------|
| 1 | BotNexus.Gateway.Abstractions | Core contracts |
| 2 | BotNexus.Gateway | Core orchestration |
| 3 | BotNexus.Gateway.Api | HTTP host / entry point |
| 4 | BotNexus.Gateway.Sessions | Session persistence |
| 5 | BotNexus.Cli | CLI tool |
| 6 | BotNexus.AgentCore | Agent execution contracts |
| 7 | BotNexus.Providers.Core | Provider abstractions + LlmClient |
| 8 | BotNexus.Providers.OpenAI | OpenAI provider |
| 9 | BotNexus.Providers.OpenAICompat | OpenAI-compatible provider |
| 10 | BotNexus.Providers.Anthropic | Anthropic provider |
| 11 | BotNexus.Providers.Copilot | Copilot provider |
| 12 | BotNexus.Channels.Core | Channel abstractions |
| 13 | BotNexus.Channels.WebSocket | WebSocket channel |
| 14 | BotNexus.Channels.Telegram | Telegram channel |
| 15 | BotNexus.Channels.Tui | TUI channel |
| 16 | BotNexus.Tools | Shared tool library |
| 17 | BotNexus.CodingAgent | Standalone coding agent |
| 18 | BotNexus.WebUI | Dashboard (Razor Pages) |

**Test projects (10):** AgentCore, CodingAgent, Gateway, Providers.Core, Providers.Anthropic, Providers.Copilot, Providers.OpenAI, Providers.OpenAICompat, Providers.Conformance.

---

## 2. Proposed Architecture

### 2.1 Design Principles

1. **Application code uses `Microsoft.Extensions.Logging` abstractions only** — no `using Serilog;` in business logic.
2. **Serilog plugs in at the host level** via `UseSerilog()` on the host builder, replacing the default log provider.
3. **OTel traces use `System.Diagnostics.Activity` API** — `ActivitySource` / `Activity`, not vendor SDKs.
4. **Trace context propagates naturally** through the middleware pipeline and DI-injected services.
5. **Don't over-instrument** — focus on the operations that matter for debugging message flow.
6. **Correlation ID maps to OTel TraceId** — the existing `CorrelationIdMiddleware` evolves to work with `Activity.Current`.

### 2.2 Serilog Integration

```
Host Builder
  └── UseSerilog()
        ├── WriteTo.Console()              (structured, development)
        ├── WriteTo.File()                 (rolling, production fallback)
        └── WriteTo.OpenTelemetry()        (OTLP export to collector)
```

- **Serilog replaces the default `ILoggerFactory`** at the host level in `Program.cs`.
- **Enrichers** automatically add `TraceId`, `SpanId`, `MachineName`, and `ThreadId` to every log event.
- Serilog's built-in `DiagnosticContext` enricher in `Serilog.AspNetCore` automatically correlates HTTP request logs with the active `Activity`.
- All existing `ILogger<T>` call sites work unchanged — zero migration needed for business code.

### 2.3 OpenTelemetry Tracing

```
ActivitySource: "BotNexus.Gateway"        → gateway-level spans
ActivitySource: "BotNexus.Providers"      → LLM provider call spans
ActivitySource: "BotNexus.Channels"       → channel dispatch spans
ActivitySource: "BotNexus.Agents"         → agent execution spans
```

Four `ActivitySource` instances, one per architectural layer. Each is defined in the layer's core library and registered in the OTel SDK configuration.

### 2.4 Trace Propagation Flow

```
Channel Inbound (WebSocket/HTTP/Telegram)
  │
  ├─ [Gateway Entry Span]
  │   ├─ CorrelationIdMiddleware (bridges X-Correlation-Id ↔ Activity)
  │   ├─ AuthMiddleware
  │   └─ RateLimitingMiddleware
  │
  ├─ [Message Routing Span]
  │   └─ DefaultMessageRouter.ResolveAsync()
  │
  ├─ [Agent Execution Span]
  │   ├─ AgentSupervisor.GetOrCreateAsync()
  │   ├─ AgentHandle.PromptAsync / StreamAsync
  │   │   └─ [Provider Call Span]
  │   │       ├─ LlmClient.Stream / CompleteAsync
  │   │       └─ HttpClient → external API (auto-instrumented)
  │   └─ (optional) Cross-Agent / Sub-Agent calls [Child Spans]
  │
  ├─ [Session Persist Span]
  │   └─ ISessionStore.SaveAsync()
  │
  └─ [Channel Outbound Span]
      └─ IChannelAdapter.SendAsync / SendStreamDeltaAsync
```

### 2.5 Key Span Attributes

| Attribute | Example | Scope |
|-----------|---------|-------|
| `botnexus.session.id` | `abc-123` | All spans |
| `botnexus.agent.id` | `coding-agent` | Agent + Provider spans |
| `botnexus.channel.type` | `websocket` | Channel spans |
| `botnexus.message.type` | `user` / `assistant` / `tool_call` | Message spans |
| `botnexus.provider.name` | `openai` / `anthropic` | Provider spans |
| `botnexus.model` | `gpt-4o` | Provider spans |
| `botnexus.correlation.id` | `d4e5f6...` | All spans |
| `botnexus.agent.call_depth` | `2` | Cross-agent spans |

---

## 3. Implementation Phases

### Wave 1: Foundation (Serilog + OTel SDK wiring)

**Scope:** Gateway.Api host only. No new spans — just replace the logging backend and wire OTel SDK.

**Tasks:**
1. Add Serilog packages to `BotNexus.Gateway.Api`.
2. Replace `LoggerFactory.Create(...)` with `UseSerilog()` in `Program.cs`.
3. Configure sinks: Console (structured), File (rolling), OpenTelemetry (OTLP).
4. Add OTel SDK packages to `BotNexus.Gateway.Api`.
5. Register `AddOpenTelemetry()` with ASP.NET Core and HttpClient auto-instrumentation.
6. Evolve `CorrelationIdMiddleware` to read/write `Activity.Current.TraceId`.
7. Verify: all existing `_logger.*` calls now emit structured JSON with TraceId/SpanId.

**Owner:** Bender (implementation) · Leela (review)  
**Risk:** Low — Serilog replaces the log provider transparently. No API changes.  
**Test impact:** Existing tests use `NullLogger` / `NullLoggerFactory` — unaffected.

### Wave 2: Core Tracing Spans

**Scope:** Add custom `ActivitySource` definitions and the key spans for message flow tracing.

**Tasks:**
1. Define `BotNexus.Gateway` `ActivitySource` in `GatewayHost` or a shared diagnostics class.
2. Add spans to `GatewayHost.DispatchAsync()` (gateway entry).
3. Add spans to `DefaultMessageRouter.ResolveAsync()` (routing decision).
4. Add spans to `DefaultAgentSupervisor.GetOrCreateAsync()` (agent lifecycle).
5. Define `BotNexus.Providers` `ActivitySource` in `Providers.Core`.
6. Add spans to `LlmClient.Stream()` / `CompleteAsync()` (provider call boundary).
7. Define `BotNexus.Agents` `ActivitySource` in `AgentCore`.
8. Add spans to agent handle methods (`PromptAsync`, `StreamAsync`).
9. Add spans to `DefaultAgentCommunicator.CallSubAgentAsync()` / `CallCrossAgentAsync()`.

**Owner:** Farnsworth (provider spans) · Bender (gateway/agent spans) · Leela (review)  
**Risk:** Medium — requires touching hot paths. Must ensure `Activity` start/stop is exception-safe (`using` blocks). Performance cost of span creation is negligible (~100ns per span).

### Wave 3: Channel + Session Spans

**Scope:** Complete the tracing picture with channel and session lifecycle spans.

**Tasks:**
1. Define `BotNexus.Channels` `ActivitySource` in `Channels.Core`.
2. Add spans to `ChannelAdapterBase` lifecycle (`OnStartAsync`/`OnStopAsync`).
3. Add spans to `WebSocketChannelAdapter.DispatchInboundMessageAsync()`.
4. Add spans to `WebSocketMessageDispatcher.HandleUserMessageAsync()` / `HandleSteerAsync()` / `HandleFollowUpAsync()`.
5. Add spans to `ISessionStore.GetOrCreateAsync()` / `SaveAsync()`.
6. Tag all spans with `botnexus.*` semantic attributes from section 2.5.

**Owner:** Kif (channel spans) · Bender (session spans) · Leela (review)  
**Risk:** Low — these are leaf spans that don't affect the critical path.

### Wave 4: Hardening & Documentation

**Scope:** Test coverage, dashboard setup, and documentation.

**Tasks:**
1. Add integration tests that verify trace propagation end-to-end (ChatController → provider).
2. Verify structured log output includes `TraceId` and `SpanId` in all log entries.
3. Document the observability architecture in `docs/`.
4. Provide a `docker-compose.yml` snippet for Jaeger/OTLP collector local development.
5. Add Serilog configuration to `appsettings.json` / `appsettings.Development.json`.
6. Performance baseline: verify no measurable regression in message throughput.

**Owner:** Hermes (docs) · Kif (test support) · Leela (final review)  
**Risk:** Low — documentation and test-only wave.

---

## 4. Package Requirements

### 4.1 Serilog Packages

| Package | Version | Target Project(s) |
|---------|---------|-------------------|
| `Serilog.AspNetCore` | 10.0.0 | Gateway.Api |
| `Serilog` | 4.3.1 | (transitive via AspNetCore) |
| `Serilog.Sinks.Console` | 6.1.1 | Gateway.Api |
| `Serilog.Sinks.File` | 7.0.0 | Gateway.Api |
| `Serilog.Sinks.OpenTelemetry` | 4.2.0 | Gateway.Api |

> **Note:** Only the host project (`Gateway.Api`) needs direct Serilog references. All other projects continue using `Microsoft.Extensions.Logging.Abstractions` and receive Serilog-powered logging through DI.

### 4.2 OpenTelemetry Packages

| Package | Version | Target Project(s) |
|---------|---------|-------------------|
| `OpenTelemetry` | 1.15.1 | Gateway.Api |
| `OpenTelemetry.Api` | 1.15.1 | Gateway, Providers.Core, Channels.Core, AgentCore |
| `OpenTelemetry.Extensions.Hosting` | 1.15.1 | Gateway.Api |
| `OpenTelemetry.Instrumentation.AspNetCore` | 1.15.1 | Gateway.Api |
| `OpenTelemetry.Instrumentation.Http` | 1.15.1 | Gateway.Api |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.15.1 | Gateway.Api |

> **Note:** `OpenTelemetry.Api` is the lightweight package containing `ActivitySource` abstractions. It goes into library projects that define custom spans. The full SDK and exporters only live in the host.

### 4.3 Package Placement Strategy

```
Gateway.Api (host)
  ├── Serilog.AspNetCore
  ├── Serilog.Sinks.Console
  ├── Serilog.Sinks.File
  ├── Serilog.Sinks.OpenTelemetry
  ├── OpenTelemetry
  ├── OpenTelemetry.Extensions.Hosting
  ├── OpenTelemetry.Instrumentation.AspNetCore
  ├── OpenTelemetry.Instrumentation.Http
  └── OpenTelemetry.Exporter.OpenTelemetryProtocol

Gateway (library)
  └── OpenTelemetry.Api          (for ActivitySource definitions)

Providers.Core (library)
  └── OpenTelemetry.Api

Channels.Core (library)
  └── OpenTelemetry.Api

AgentCore (library)
  └── OpenTelemetry.Api
```

---

## 5. Risk Assessment

### 5.1 Breaking Changes

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Serilog replaces log output format | Certain | Low | Console sink with structured template matches existing output. Configurable via `appsettings.json`. |
| Existing `NullLogger` tests break | Very Low | Low | `NullLogger` is a MEL abstraction — works identically regardless of the underlying provider. |
| `CorrelationIdMiddleware` behavior changes | Low | Medium | Evolve carefully — keep `X-Correlation-Id` header behavior, add `Activity` integration as enrichment, not replacement. |
| New packages increase binary size | Certain | Very Low | OTel SDK + Serilog add ~5 MB total to host deployment. Acceptable for observability value. |

### 5.2 Performance Impact

| Area | Expected Impact | Notes |
|------|----------------|-------|
| Serilog log writes | Negligible | Serilog is faster than the default MEL console provider due to async batching. |
| Span creation (`Activity`) | ~100ns per span | .NET's built-in `Activity` API is highly optimized. No allocation if no listener is registered. |
| OTLP export | Background batched | Exporter uses a background channel — never blocks the request pipeline. |
| HttpClient auto-instrumentation | Negligible | Wraps existing `HttpMessageHandler` pipeline with minimal overhead. |

### 5.3 Test Impact

- **Unit tests:** No impact. They use `NullLogger<T>` / `NullLoggerFactory` which are unaffected by Serilog wiring.
- **Integration tests:** May need `WebApplicationFactory` adjustments if tests inspect log output. Recommend adding a `Serilog.Sinks.InMemory` sink for test assertions.
- **New tests needed:** Wave 4 adds dedicated trace-propagation integration tests.

---

## 6. Agent Assignment Summary

| Agent | Waves | Responsibilities |
|-------|-------|-----------------|
| **Bender** | 1, 2, 3 | Host wiring (Serilog + OTel SDK), gateway spans, agent spans, session spans |
| **Farnsworth** | 2 | Provider `ActivitySource` + spans in `LlmClient` and provider implementations |
| **Kif** | 3, 4 | Channel spans, integration test support |
| **Hermes** | 4 | Documentation, `docker-compose.yml` for local collector, architecture docs |
| **Leela** | 1–4 | Architectural review gate on each wave |

---

## 7. Key Design Decisions

### 7.1 Why Serilog (not just MEL defaults)?

- **Structured logging** with rich property support — critical for correlating logs with traces.
- **Sink ecosystem** — Console, File, OTLP, Seq, Elasticsearch, etc. without code changes.
- **Enrichers** — automatic `TraceId`/`SpanId` injection into every log event.
- **Performance** — async batching, message templates compiled once.
- **Industry standard** for .NET structured logging.

### 7.2 Why `System.Diagnostics.Activity` (not OTel SDK directly)?

- **Built into the runtime** — zero-allocation when no listener is active.
- **OTel SDK listens to `ActivitySource`** — the SDK subscribes to activities, not the other way around. This means library code has no SDK dependency.
- **Future-proof** — if we ever switch from OTel to another collector, application code doesn't change.
- **ASP.NET Core already creates activities** — the framework's middleware pipeline produces activities that OTel automatically captures.

### 7.3 Why not instrument everything?

Over-instrumentation creates noise, increases storage costs, and makes traces harder to read. The proposed spans focus on:
- **Message flow** (the most common debugging scenario)
- **Provider latency** (the most impactful performance bottleneck)
- **Cross-agent calls** (the most complex debugging scenario)
- **Session lifecycle** (the most common state-related bug source)

Leaf operations (tool execution, config reads, extension loading) are intentionally excluded from Wave 1–3. They can be added incrementally if specific debugging needs arise.

---

## 8. Open Questions

1. **OTLP endpoint configuration:** Should we default to `http://localhost:4317` (standard OTLP gRPC) or make it purely opt-in via `appsettings.json`? Recommendation: opt-in, with a dev-mode default.
2. **Log level management:** Should Serilog minimum level be configurable at runtime (via `appsettings.json` reload)? Recommendation: yes — Serilog supports this natively.
3. **CodingAgent:** The standalone coding agent currently uses `NullLogger`. Should it get its own Serilog host, or remain silent? Recommendation: remain `NullLogger` for now — it's a CLI tool where stdout is the user interface.
4. **CLI tool logging:** `BotNexus.Cli` uses `Console.WriteLine` for user output. This is intentional UX, not logging. Should we add structured logging alongside? Recommendation: not in scope for Wave 1–3.

---

*This proposal establishes the observability foundation for BotNexus. Each wave is independently shippable and testable. Wave 1 alone delivers immediate value (structured logs with correlation IDs) with near-zero risk.*
