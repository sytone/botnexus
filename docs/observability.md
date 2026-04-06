# Observability Architecture

**Version:** 1.0  
**Last Updated:** 2026-05-15  
**Owner:** Kif (Documentation Engineer)

The BotNexus platform uses **Serilog** for structured logging and **OpenTelemetry** for distributed tracing. This guide covers the observability stack, local development setup, and how to add instrumentation to new code.

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Trace Propagation Flow](#trace-propagation-flow)
4. [Span Attributes](#span-attributes)
5. [Serilog Configuration](#serilog-configuration)
6. [Local Development Setup](#local-development-setup)
7. [Adding New Spans](#adding-new-spans)
8. [Troubleshooting](#troubleshooting)

---

## Overview

The platform captures observability data via two complementary systems:

- **Serilog (Structured Logging)** — Application logs with structured fields (session ID, agent ID, correlation ID)
- **OpenTelemetry (Distributed Tracing)** — Request traces with spans, tags, and status codes

Both systems write to:
- Console (development)
- Rolling file sink (14-day retention, `logs/` directory)
- OTLP exporter (opt-in, for external APM systems like Jaeger, Datadog, App Insights)

This setup enables developers to:
- Correlate logs and traces using session/correlation IDs
- Debug multi-agent flows end-to-end
- Measure provider latency and stream performance
- Monitor channel health and message throughput

---

## Architecture

The observability stack is built on **4 ActivitySources** corresponding to the platform's core layers:

| ActivitySource | Namespace | Instrumented Operations |
|---|---|---|
| `BotNexus.Gateway` | `BotNexus.Gateway.Diagnostics` | Gateway dispatch, routing, agent lifecycle, session ops |
| `BotNexus.Providers` | `BotNexus.Providers.Core.Diagnostics` | LLM provider calls (stream, stream_simple, completions) |
| `BotNexus.Agents` | `BotNexus.AgentCore.Diagnostics` | Agent prompt execution, streaming, cross-agent calls |
| `BotNexus.Channels` | `BotNexus.Channels.Core.Diagnostics` | Channel lifecycle, message receive/send, streaming |

Each ActivitySource produces spans tagged with semantic attributes (see [Span Attributes](#span-attributes)) that enable filtering and correlation across the distributed trace.

### Example Span Tree

A message arriving via WebSocket produces:

```
gateway.dispatch (GatewayDiagnostics)
  ├─ http.server.request (AspNetCore instrumentation)
  ├─ gateway.route (DefaultMessageRouter)
  └─ agent.stream (AgentDiagnostics)
      ├─ provider.openai-completions.stream (ProviderDiagnostics)
      └─ provider.openai-completions.http (HttpClient instrumentation)
```

---

## Trace Propagation Flow

Messages flow through the platform with tracing enabled at each layer:

```
┌──────────────────────┐
│  Channel Inbound     │  [botnexus.channel.type = websocket]
│  WebSocket message   │
└──────────────┬───────┘
               │
               ▼
      ┌────────────────────┐
      │  Gateway Dispatch  │  [botnexus.session.id]
      │  (AspNetCore)      │
      └─────────┬──────────┘
                │
                ▼
        ┌───────────────────┐
        │  Message Router   │  [botnexus.route.agent_count]
        │  (ResolveAsync)   │
        └────────┬──────────┘
                 │
                 ▼
          ┌──────────────────┐
          │  Agent Stream    │  [botnexus.agent.id]
          │  (Executor)      │
          └────────┬─────────┘
                   │
                   ▼
         ┌──────────────────────┐
         │  Provider Stream     │  [botnexus.provider.name]
         │  (LLM API call)      │  [botnexus.model]
         └────────┬─────────────┘
                  │
                  ▼
        ┌────────────────────────┐
        │  Channel Outbound      │  [botnexus.session.id]
        │  WebSocket response    │
        └────────────────────────┘
```

**Trace ID** — Set by the first span (typically AspNetCore's http.server.request) and propagated through the entire call chain via Activity.Current. All child spans inherit the same trace ID, enabling end-to-end correlation.

**Correlation ID** — Set from the `botnexus.correlation.id` tag and maps to the OpenTelemetry TraceId. Logged with each log entry via Serilog enrichment.

---

## Span Attributes

All spans in BotNexus use semantic attributes following the `botnexus.*` convention. These attributes enable filtering, alerting, and debugging:

| Attribute | Type | Description | Scope | Example |
|-----------|------|-------------|-------|---------|
| `botnexus.session.id` | string | Session identifier (UUID) | All spans | `550e8400-e29b-41d4-a716-446655440000` |
| `botnexus.agent.id` | string | Agent identifier | Agent, Provider spans | `customer-support-agent` |
| `botnexus.channel.type` | string | Channel type (websocket, telegram) | Channel, Router spans | `websocket` |
| `botnexus.provider.name` | string | Provider name | Provider spans | `openai` |
| `botnexus.model` | string | Model identifier | Provider spans | `gpt-4-turbo` |
| `botnexus.correlation.id` | string | Correlation ID (maps to TraceId) | All spans | `550e8400-e29b-41d4-a716-446655440000` |
| `botnexus.route.agent_count` | int | Number of agents resolved | Router spans | `2` |

### Setting Tags in Code

Tags are set on Activity objects using `SetTag()`:

```csharp
using var activity = AgentDiagnostics.Source.StartActivity("agent.stream");
activity?.SetTag("botnexus.session.id", sessionId);
activity?.SetTag("botnexus.agent.id", agentId);
activity?.SetStatus(ActivityStatusCode.Ok);
```

---

## Serilog Configuration

Serilog is configured in `Program.cs` to provide structured logging across all layers.

### Bootstrap Logger

On startup, before full configuration is loaded:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();
```

### Full Configuration

In `Program.cs` via `builder.Host.UseSerilog()`:

```csharp
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "botnexus-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14),
    preserveStaticLogger: true);
```

### Output Sinks

1. **Console** — Development-friendly format with timestamp, level, and structured properties
2. **File** — Rolling daily logs stored in `logs/` directory with 14-day retention
3. **OTLP Exporter** — Optional; enabled via `OpenTelemetry:OtlpEndpoint` configuration

### Enrichers

- **LogContext** — Captures structured context data (e.g., correlation ID)
- **MachineName** — Adds machine hostname to all logs
- **ThreadId** — Adds managed thread ID for debugging async code

### Usage in Application Code

All business code uses the .NET standard `ILogger<T>` interface (no direct Serilog references):

```csharp
public class MyService(ILogger<MyService> logger)
{
    public async Task DoWorkAsync()
    {
        using var activity = GatewayDiagnostics.Source.StartActivity("my.operation");
        
        logger.LogInformation("Starting work for session {SessionId}", sessionId);
        
        try
        {
            // ... work ...
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Work failed");
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }
}
```

Serilog automatically handles log correlation with OpenTelemetry via Serilog's built-in enricher.

---

## Local Development Setup

### Prerequisites

- Docker and Docker Compose (for running Jaeger)
- .NET 10.0+ SDK
- BotNexus repository cloned

### Step 1: Start Jaeger

Create `docker-compose.yml` in the repository root:

```yaml
version: '3'

services:
  jaeger:
    image: jaegertracing/jaeger:latest
    ports:
      - "16686:16686"   # Jaeger UI (http://localhost:16686)
      - "4317:4317"     # OTLP gRPC
      - "4318:4318"     # OTLP HTTP
    environment:
      - COLLECTOR_OTLP_ENABLED=true
```

Start the service:

```bash
docker compose up -d jaeger
```

Verify it's running:

```bash
curl http://localhost:16686/api/services
# Expected response: {"data":[],"total":0,"limit":0,"offset":0,"errors":null}
```

### Step 2: Configure BotNexus

Update `appsettings.Development.json` in `src/gateway/BotNexus.Gateway.Api/`:

```json
{
  "OpenTelemetry": {
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

Or set environment variable:

```powershell
$env:OpenTelemetry__OtlpEndpoint = "http://localhost:4317"
```

### Step 3: Run BotNexus

```powershell
# From repository root
.\scripts\start-gateway.ps1
```

The Gateway starts at `http://localhost:5005`.

### Step 4: Send a Request

From another terminal:

```bash
curl -X POST http://localhost:5005/api/agents/my-agent/stream \
  -H "Content-Type: application/json" \
  -d '{"prompt": "Hello, agent!"}'
```

### Step 5: View Traces in Jaeger

Open [http://localhost:16686](http://localhost:16686) in your browser:

1. Select **Service** dropdown → choose `BotNexus.Gateway`
2. Click **Find Traces**
3. Click any trace to view the span tree, attributes, and logs

Each span shows:
- Operation name (e.g., `gateway.route`)
- Duration
- Tags (e.g., `botnexus.agent.id`, `botnexus.provider.name`)
- Status (Ok or Error)
- Child spans

---

## Adding New Spans

To instrument a new operation, follow this pattern:

### 1. Import the Diagnostics Class

```csharp
using BotNexus.Gateway.Diagnostics;  // or Agents, Providers, etc.
using System.Diagnostics;
```

### 2. Start a Span

```csharp
public async Task MyOperationAsync(string agentId, string sessionId)
{
    using var activity = GatewayDiagnostics.Source.StartActivity(
        "gateway.my_operation", 
        ActivityKind.Internal);
    
    // Set tags
    activity?.SetTag("botnexus.agent.id", agentId);
    activity?.SetTag("botnexus.session.id", sessionId);
    
    try
    {
        // ... do work ...
        activity?.SetStatus(ActivityStatusCode.Ok);
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.RecordException(ex);
        throw;
    }
}
```

### 3. Operation Naming Convention

Follow this pattern:

```
{layer}.{component}[.{operation}]
```

Examples:

- `gateway.route` — Route a message
- `gateway.dispatch` — Dispatch to agent
- `agent.stream` — Stream agent output
- `provider.openai-completions.stream` — Stream from OpenAI
- `channel.websocket.receive` — Receive WebSocket message

### 4. Activity Kind

Use one of:

- `ActivityKind.Internal` — Internal processing (routing, streaming)
- `ActivityKind.Client` — Client call (provider API, database query)
- `ActivityKind.Server` — Server-side handling (request handling)

### 5. Set Status

```csharp
// Success
activity?.SetStatus(ActivityStatusCode.Ok);

// Error (with exception)
activity?.RecordException(exception);
activity?.SetStatus(ActivityStatusCode.Error);

// Unset (default behavior — traces succeed unless an error is recorded)
activity?.SetStatus(ActivityStatusCode.Unset);
```

### 6. Log Within Spans

Logs and spans are automatically correlated via `ActivityTraceId`. No additional setup needed:

```csharp
using var activity = GatewayDiagnostics.Source.StartActivity("my.operation");
logger.LogInformation("Processing started");  // This log is correlated with the span
```

---

## Troubleshooting

### No Traces Appearing in Jaeger

1. **Verify Jaeger is running:**
   ```bash
   docker ps | grep jaeger
   curl http://localhost:16686/api/services
   ```

2. **Check OTLP endpoint is set:**
   ```powershell
   $env:OpenTelemetry__OtlpEndpoint
   # Should output: http://localhost:4317
   ```

3. **Check Gateway logs for startup errors:**
   ```powershell
   Get-Content logs/botnexus-*.log | Select-String -Pattern "OpenTelemetry|OTLP"
   ```

### Traces Incomplete (Missing Spans)

1. **Verify ActivitySource is registered in Program.cs:**
   ```csharp
   tracing
       .AddSource("BotNexus.Gateway")
       .AddSource("BotNexus.Providers")
       .AddSource("BotNexus.Channels")
       .AddSource("BotNexus.Agents")
   ```

2. **Check that spans are being created with correct ActivityKind**

3. **Use listener to debug:**
   ```csharp
   var listener = new ActivityListener
   {
       ShouldListenTo = _ => true,
       Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
   };
   ActivitySource.AddActivityListener(listener);
   ```

### High Memory Usage

- Serilog file sink has 14-day retention. Old logs are automatically deleted.
- Check `logs/` directory for large files:
  ```bash
  ls -lh logs/ | tail -20
  ```
- OTLP exporter batches traces; increase batch size if network-bound:
  ```json
  {
    "OpenTelemetry": {
      "OtlpEndpoint": "http://localhost:4317",
      "BatchSize": 2048
    }
  }
  ```

### Correlation ID Not in Logs

1. **Verify LogContext enricher is enabled:**
   ```csharp
   .Enrich.FromLogContext()
   ```

2. **Check that Activity is flowing in async code:**
   Ensure all async operations inherit Activity.Current (this is automatic in .NET 5+)

---

## Further Reading

- [OpenTelemetry .NET Documentation](https://opentelemetry.io/docs/instrumentation/net/)
- [Serilog Documentation](https://github.com/serilog/serilog/wiki)
- [Jaeger Getting Started](https://www.jaegertracing.io/docs/getting-started/)
- [BotNexus Architecture](/docs/architecture/overview.md)
