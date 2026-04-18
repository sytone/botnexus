# Blazor WebUI — Research Document

**Created:** 2026-07-19
**Based on:** Code review of current Gateway.Api, Channels.Core, Channels.Telegram, TestSignalRClient, and JS WebUI

## 1. SignalR Channel Extraction

### Current State

The SignalR channel is embedded in `BotNexus.Gateway.Api.Hubs/` with these types:

- **`GatewayHub`** (529 lines) — Injects `IAgentSupervisor`, `IAgentRegistry`, `ISessionStore`, `IChannelDispatcher`, `IActivityBroadcaster`, `ISessionCompactor`, `ISessionWarmupService`, `IOptionsMonitor<CompactionOptions>`. All of these are defined in `BotNexus.Gateway.Abstractions` — they're interface-only dependencies, not Gateway.Api internals. This means `GatewayHub` can move to a new project that references only `Gateway.Abstractions`.

- **`SignalRChannelAdapter`** (86 lines) — Extends `ChannelAdapterBase` from `BotNexus.Channels.Core` and implements `IStreamEventChannelAdapter` from `Gateway.Abstractions.Channels`. Uses `IHubContext<GatewayHub>` to push events. This is the standard channel adapter pattern — identical structure to `TelegramChannelAdapter`.

- **`SubAgentSignalRBridge`** — `BackgroundService` that subscribes to `IActivityBroadcaster` and forwards sub-agent lifecycle events to SignalR groups via `IHubContext<GatewayHub>`. Pure SignalR concern.

- **`MediaContentPartDto`** — DTO used in `GatewayHub.SendMessageWithMedia()`. Defined in `Gateway.Api/Hubs/` — stays in the channel extension since it's a SignalR-specific concern (used only by the hub and its clients).

### Telegram and Tui Channels — Planned Extensions (Blocked on Infrastructure)

`BotNexus.Channels.Telegram` and `BotNexus.Channels.Tui` exist as source projects under `src/channels/` but are currently stubs. Telegram is a planned channel extension that's blocked on the same `IEndpointContributor` infrastructure needed for SignalR. Once that gap is filled, both channels slot in using the same dynamic extension pattern. They validate the project structure direction but aren't yet functional.

### How the Extension System Actually Works

The Gateway uses `AssemblyLoadContextExtensionLoader` to dynamically discover and load extensions:
1. Scans for `botnexus-extension.json` manifests
2. Loads assemblies via `AssemblyLoadContext`
3. Auto-discovers types implementing `DiscoverableServiceContracts` (including `IChannelAdapter`)
4. Registers discovered services into `IServiceCollection`

Extensions already in production: exec, process, web, MCP, mcp-invoke, audio transcription, skills.

The SignalR channel would follow this pattern for `IChannelAdapter` registration. However, the extension system currently has a critical gap — see below.

### The Extension System Gap

The current `DiscoverableServiceContracts` only covers service-level contracts:
```
IChannelAdapter, IIsolationStrategy, ISessionStore, IGatewayAuthHandler, IMessageRouter,
IAgentRegistry, IAgentSupervisor, IAgentCommunicator, IActivityBroadcaster, IAgentTool,
ICommandContributor, IMediaHandler
```

There is **NO mechanism** for extensions to:
- Map their own transport endpoints (SignalR hubs, webhook receivers)
- Register API endpoints/controllers
- Serve static files (wwwroot)
- Add middleware

SignalR hubs are deliberately excluded from this list — a hub is an internal implementation detail of the SignalR channel, not something the extension system should generalize. Each channel interacts with the gateway, sessions, and clients in its own way (SignalR uses hubs, Telegram uses webhooks, TUI uses stdio). The extension system only needs to let extensions register their own endpoints and static files; what happens behind those endpoints is the extension's business.

**Proposed solution:** Two new interfaces (both added to `DiscoverableServiceContracts`), splitting endpoint ownership from shared API contribution:

1. **`IEndpointContributor`** — Extension-owned surfaces: static files, transport endpoints (SignalR hubs, webhook receivers), UI paths. The extension has full control over its own routes and receives `WebApplication` to register whatever it needs.

2. **`IApiContributor`** — Contributes to the gateway's shared API surface. Extensions receive a **scoped `RouteGroupBuilder`** pre-namespaced (e.g., `/api/extensions/{extensionId}/`) so extensions can't stomp on each other or core API routes. The scoped route group provides governance (namespacing, auth, OpenAPI metadata) automatically.

```csharp
/// Extension-owned endpoints, static files, middleware, transport surfaces.
public interface IEndpointContributor
{
    /// Called during app startup with the built WebApplication.
    /// Internal transport details (e.g., SignalR hubs, webhook receivers) are the extension's own concern.
    void MapEndpoints(WebApplication app);
}

/// Contributes to the gateway's shared API surface.
/// Extensions receive a scoped RouteGroupBuilder pre-namespaced to prevent route collisions.
public interface IApiContributor
{
    /// Called during app startup with a scoped route group (e.g., /api/extensions/{extensionId}/).
    void MapApiRoutes(RouteGroupBuilder apiGroup);
}
```

**Why the split:** Serving a UI/transport is different from enriching the shared API. API contributions get automatic governance via the scoped route group. An extension can implement one or both interfaces independently. Extensions that only need API surface don't touch the endpoint side, and vice versa.

> **Future consideration:** `IApiContributor` could potentially support contributing to existing core API resources (e.g., enriching `/api/sessions/` responses with channel-specific data). Noted as a possibility but deliberately not designed now.

The extension loader gains a post-build phase where it calls `MapEndpoints(app)` on `IEndpointContributor` implementations, and creates scoped route groups + calls `MapApiRoutes(group)` on `IApiContributor` implementations. See the design spec for full details.

### SignalR Channel Needs Beyond Service Registration

`SignalRChannelAdapter` already extends `ChannelAdapterBase` and implements `IChannelAdapter` — that part works with the existing extension loader. But the SignalR channel also needs:
- `services.AddSignalR()` — framework service registration
- `endpoints.MapHub<GatewayHub>(...)` — endpoint mapping (**`IEndpointContributor`** — hub is an extension-owned transport surface)
- `services.AddHostedService<SubAgentSignalRBridge>()` — background event bridge
- Static file serving for both the legacy JS WebUI and the Blazor WASM client (**`IEndpointContributor`** — extension-owned static files)

The service registrations (`AddSignalR()`, `AddHostedService`) can potentially be handled during the existing service discovery phase. The endpoint/static file registration requires `IEndpointContributor`.

If the SignalR channel later needs to expose REST API endpoints (e.g., connection diagnostics, channel configuration), those would go through a separate `IApiContributor` implementation, receiving a scoped route group at `/api/extensions/signalr/`. This keeps the transport concerns (hub, static files) cleanly separated from any API surface the channel might offer.

### Dependencies to Resolve

The `GatewayHub` currently lives in `Gateway.Api`, which gives it implicit access to all Gateway internals. After extraction, `BotNexus.Channels.SignalR` needs these project references:

```xml
<ProjectReference Include="..\BotNexus.Channels.Core\BotNexus.Channels.Core.csproj" />
<ProjectReference Include="..\..\gateway\BotNexus.Gateway.Abstractions\BotNexus.Gateway.Abstractions.csproj" />
```

No reference to `Gateway.Api` is needed — `GatewayHub` only uses abstraction interfaces.

### CronTrigger and SoulTrigger

These are NOT SignalR-related despite living in the `Hubs` namespace. `CronTrigger` implements `IInternalTrigger` and dispatches to `IAgentSupervisor` — no SignalR dependency. `SoulTrigger` is similar. They should stay in `Gateway.Api` (or move to their own homes) but are out of scope for this extraction.

## 2. Channel-Internal Contract Strategy

### What Already Exists at the Gateway Level

`BotNexus.Gateway.Abstractions` (exposed via type-forwards from `Gateway.Contracts`) already contains the core domain types:

| Type | Location | Used By |
|------|----------|---------|
| `AgentStreamEvent` / `AgentStreamEventType` | `Gateway.Abstractions.Models` | `SignalRChannelAdapter.SendStreamEventAsync()`, `TestSignalRClient` (as `JsonElement`) |
| `InboundMessage` / `OutboundMessage` | `Gateway.Abstractions.Models` | `GatewayHub.DispatchMessageAsync()`, all channel adapters |
| `SessionSummary` | `Gateway.Abstractions.Sessions` | `ISessionWarmupService.GetAvailableSessionsAsync()` |
| `AgentDescriptor` | `Gateway.Abstractions.Agents` | `GatewayHub.GetAgents()` |
| `IChannelAdapter` / `IChannelDispatcher` / `IStreamEventChannelAdapter` | `Gateway.Abstractions.Channels` | All channel adapters |

These are gateway-level abstractions shared by all channels. They stay where they are.

### Hub Contracts Are Internal to the Channel

The hub is a SignalR implementation detail, not a gateway concept. If there were no SignalR channel, there would be no hub — other channels (Telegram, TUI) don't use one. Therefore, all hub-specific types live inside `BotNexus.Channels.SignalR`:

| Type | Lives In | Notes |
|------|----------|-------|
| `IGatewayHubClient` | `BotNexus.Channels.SignalR` | Server→client event contract. NOT in `Gateway.Contracts`. |
| `MediaContentPartDto` | `BotNexus.Channels.SignalR` | Upload DTO used by hub and its clients. |
| `SendMessageResult` | `BotNexus.Channels.SignalR` | Typed return from `SendMessage` hub method. |
| `SubscribeAllResult` | `BotNexus.Channels.SignalR` | Typed return from `SubscribeAll` hub method. |
| `CompactSessionResult` | `BotNexus.Channels.SignalR` | Typed return from `CompactSession` hub method. |

The "shared" aspect is between the server-side hub and the client-side Blazor WASM — but both live in the same channel extension. The Blazor WASM project (`BotNexus.Channels.SignalR.BlazorClient`) simply references `BotNexus.Channels.SignalR` directly. No separate contracts package, no gateway-level sharing.

### Anonymous Return Types Problem

The anonymous return types in `GatewayHub` are a problem. Methods like `SendMessage`, `JoinSession`, `CompactSession` return `object` typed as anonymous types. The Blazor client would need to deserialize these as `JsonElement` (like `TestSignalRClient` does today) unless we define proper return DTOs. Phase 1 should create these as named types inside the channel extension.

### IGatewayHubClient Interface

```csharp
// Defined in BotNexus.Channels.SignalR (NOT in Gateway.Contracts)
public interface IGatewayHubClient
{
    Task Connected(ConnectedPayload payload);
    Task SessionReset(SessionResetPayload payload);
    Task MessageStart(AgentStreamEvent evt);
    Task ContentDelta(AgentStreamEvent evt);
    Task ThinkingDelta(AgentStreamEvent evt);
    Task ToolStart(AgentStreamEvent evt);
    Task ToolEnd(AgentStreamEvent evt);
    Task MessageEnd(AgentStreamEvent evt);
    Task Error(AgentStreamEvent evt);
    Task SubAgentSpawned(SubAgentPayload payload);
    Task SubAgentCompleted(SubAgentPayload payload);
    Task SubAgentFailed(SubAgentPayload payload);
    Task SubAgentKilled(SubAgentPayload payload);
}
```

This interface is referenced by:
- **Server**: `GatewayHub : Hub<IGatewayHubClient>` — eliminates string-based `Clients.Group(...).SendAsync("ContentDelta", ...)` calls
- **Blazor WASM client**: Registers typed handlers that match the interface
- **TestSignalRClient**: Replaces the hardcoded `AllHubEvents` string array with compile-time checked handler registration

All three consumers reference the same `BotNexus.Channels.SignalR` project directly.

## 3. Blazor WASM SignalR Client Setup

### How It Connects

Blazor WASM uses the same `Microsoft.AspNetCore.SignalR.Client` NuGet package that `TestSignalRClient` already uses. The connection setup is nearly identical:

```csharp
// Blazor WASM service
public class GatewayHubConnection : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public GatewayHubConnection(string hubUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{hubUrl}/hub/gateway?clientVersion=blazor-wasm")
            .WithAutomaticReconnect([
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)])
            .Build();
    }
}
```

This is essentially `TestSignalRClient` cleaned up as a production service. The key difference: instead of recording events into `ConcurrentDictionary<string, List<ReceivedEvent>>`, it exposes per-session observable streams or events that Blazor components subscribe to.

### Typed Event Registration

With `IGatewayHubClient` (from the channel extension):

```csharp
// Instead of TestSignalRClient's string-based:
// _connection.On<JsonElement>("ContentDelta", payload => ...)

// Use typed registration:
_connection.On<AgentStreamEvent>(nameof(IGatewayHubClient.ContentDelta), evt =>
{
    var sessionId = evt.SessionId.Value;
    _sessionStates[sessionId].AppendDelta(evt);
});
```

The event names match the interface method names at compile time. If the server renames an event, the client gets a build error.

### Blazor WASM Limitations

- **No `HttpContext`** — Blazor WASM runs in the browser. `HubConnection` connects over WebSockets (or long-polling fallback) just like the JS client.
- **No `IHubContext` injection** — Only the server can inject `IHubContext<GatewayHub>`. The client uses `HubConnection.InvokeAsync/SendAsync`.
- **Single-threaded** — WASM runs on the browser's main thread (until threading support ships). High-frequency event handlers must be lightweight to avoid UI jank. `StateHasChanged()` calls should be batched.

## 4. Hosting Strategy

### Current Setup (The Tech Debt)

`SignalRChannelAdapter` is registered directly in `GatewayApiServiceCollectionExtensions.cs` (line 29):
```csharp
services.AddSingleton<IChannelAdapter>(provider => provider.GetRequiredService<SignalRChannelAdapter>());
```

`GatewayHub` lives in `Gateway.Api/Hubs/` and WebUI static files in `Gateway.Api/wwwroot/`. `Program.cs` configures:
```csharp
app.UseStaticFiles();           // serves wwwroot/ (JS WebUI)
app.MapHub<GatewayHub>("/hub/gateway");
app.MapFallbackToFile("index.html");  // SPA fallback
```

None of this goes through the dynamic extension loader. The listen URL is configurable: `platformConfig.Gateway?.ListenUrl` (typically `http://localhost:5005`).

### Path-Based Hosting (Confirmed)

Both UIs are served from the same port via path-based routing. The SignalR channel extension registers its own static file paths and endpoints dynamically via `IEndpointContributor` — the gateway core doesn't know about hubs, static files, or HTML.

The channel extension implements `IEndpointContributor` for its transport and UI surfaces:

```csharp
// In SignalREndpointContributor.cs (discovered by extension loader)
public class SignalREndpointContributor : IEndpointContributor
{
    public void MapEndpoints(WebApplication app)
    {
        // Legacy JS WebUI at root
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new EmbeddedFileProvider(typeof(GatewayHub).Assembly,
                "BotNexus.Channels.SignalR.wwwroot"),
        });

        // Blazor WASM UI at /blazor/
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(blazorWwwrootPath),
            RequestPath = "/blazor"
        });

        app.MapHub<GatewayHub>("/hub/gateway");
        app.MapFallbackToFile("index.html");           // Legacy SPA fallback
        app.MapFallbackToFile("/blazor/{**path}", "blazor/index.html"); // Blazor SPA fallback
    }
}
```

If the channel also needed REST API endpoints, it would separately implement `IApiContributor` to receive a scoped route group:

```csharp
// Hypothetical — only if the SignalR channel exposes REST endpoints
public class SignalRApiContributor : IApiContributor
{
    public void MapApiRoutes(RouteGroupBuilder apiGroup)
    {
        // Routes automatically scoped to /api/extensions/signalr/
        apiGroup.MapGet("status", () => Results.Ok(new { connected = true }));
    }
}
```

The gateway's `Program.cs` has **ZERO SignalR-specific code**. The extension loader discovers `SignalRChannelAdapter` (as `IChannelAdapter`) during service registration, `SignalREndpointContributor` (as `IEndpointContributor`) during the post-build phase, and any `IApiContributor` implementations for scoped API route registration.

The extension manifest declares its roles:
```json
{
  "name": "signalr",
  "extensionTypes": ["channel", "endpoint-contributor"],
  "assembly": "BotNexus.Channels.SignalR.dll"
}
```

### Extension Self-Registration Pattern

This is a general pattern, not SignalR-specific. The two interfaces cover different extension needs:

**`IEndpointContributor`** — extension-owned surfaces:
- **Static file paths** (for serving UI assets)
- **SignalR hubs** (or other real-time transports)
- **Webhook receivers** (e.g., Telegram bot webhooks)
- **Middleware** (authentication, CORS, etc.)

**`IApiContributor`** — shared API surface:
- **REST endpoints** scoped under `/api/extensions/{extensionId}/`
- **Configuration endpoints** for extension-specific settings
- Automatic namespacing, auth, and OpenAPI metadata via the scoped route group

An extension can implement one or both interfaces. The extension loader handles discovery and invocation. The gateway core never knows the details.

The Blazor WASM client connects to the hub at the same origin: `new HubConnectionBuilder().WithUrl("/hub/gateway")` — no CORS issues since it's the same host and port.

## 5. Concurrency Model

### The Core Problem with the JS UI

The current JS client has a single `currentSessionId` global. All send operations use this global. When switching agents, there's a window where `currentSessionId` is stale (set to null during async operations in `openAgentTimeline()`). The `routeEvent()` function uses `activeViewId` to decide which chat canvas receives events — but during agent switches or when multiple agents respond simultaneously, events bleed across canvases.

### Blazor WASM Approach: Per-Session State Containers

```csharp
public class SessionStateManager
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    public SessionState GetOrCreate(string sessionId) =>
        _sessions.GetOrAdd(sessionId, _ => new SessionState(sessionId));
}

public class SessionState
{
    public string SessionId { get; }
    public List<ChatMessage> Messages { get; } = [];
    public StringBuilder StreamingBuffer { get; } = new();
    public bool IsStreaming { get; set; }
    public bool IsLoading { get; set; }
    public event Action? OnChanged;

    public void AppendDelta(string delta)
    {
        StreamingBuffer.Append(delta);
        OnChanged?.Invoke();
    }
}
```

Each `<ChatPanel SessionId="..." />` Blazor component subscribes to its own `SessionState.OnChanged` event. Events from the hub are routed to the correct `SessionState` by `sessionId` — there is no global "active session" concept for routing purposes.

**No send-side race:** When the user clicks "Send" in Agent B's panel, the send call includes Agent B's session ID explicitly from the component's bound state — not from a global variable. There is no window where the session ID can be stale.

**No receive-side bleed:** Each component only re-renders when its own `SessionState` fires `OnChanged`. Agent A's streaming events update Agent A's state container; Agent B's component is unaffected.

### Handling WASM Single-Threadedness

WASM is single-threaded, so "concurrent" really means interleaved async. When two agents respond simultaneously, their `ContentDelta` events arrive interleaved on the same thread. The routing is synchronous (dictionary lookup by `sessionId`), so there's no race condition — each delta is appended to the correct `SessionState` buffer.

For rendering performance, batch `StateHasChanged()` calls:
```csharp
// Don't call StateHasChanged() on every delta
// Instead, use a timer to batch renders
private Timer? _renderTimer;

protected override void OnInitialized()
{
    _sessionState.OnChanged += QueueRender;
}

private void QueueRender()
{
    _renderTimer ??= new Timer(_ =>
    {
        InvokeAsync(StateHasChanged);
        _renderTimer = null;
    }, null, 50, Timeout.Infinite); // 50ms batch window → ~20fps
}
```

## 6. Testing Strategy

### bUnit Component Tests

Test individual Blazor components with mocked hub connections:

```csharp
[Fact]
public void ChatPanel_RendersStreamingDelta()
{
    var state = new SessionState("test-session");
    using var ctx = new TestContext();
    ctx.Services.AddSingleton(state);

    var cut = ctx.RenderComponent<ChatPanel>(p => p.Add(x => x.SessionId, "test-session"));

    state.AppendDelta("Hello ");
    state.AppendDelta("world");
    cut.WaitForState(() => cut.Find(".message-content").TextContent.Contains("Hello world"));
}
```

No real SignalR connection needed. Components are tested against state containers.

### Shared Hub Client (Replace TestSignalRClient)

`TestSignalRClient` (186 lines) is essentially a production SignalR client with test helpers bolted on. After Phase 2, the `GatewayHubConnection` service from the Blazor app replaces it:

```csharp
// In integration tests:
var hubConnection = new GatewayHubConnection(testServerUrl);
await hubConnection.ConnectAsync(ct);
var result = await hubConnection.SendMessageAsync("nova", "signalr", "hello", ct);

// Wait for response using the same SessionState containers
var state = hubConnection.Sessions.Get(result.SessionId);
await state.WaitForMessageComplete(timeout, ct);
```

The `AllHubEvents` string array in `TestSignalRClient` becomes unnecessary — event registration is driven by `IGatewayHubClient` at compile time.

### Integration Tests

Existing `SignalRIntegrationTests` continue to work because the hub URL (`/hub/gateway`) and contract don't change. The tests just get a better client.

### Playwright E2E

Blazor WASM works with Playwright. The main gotcha is initial load time — WASM assets need to download and initialize. Tests need a longer initial timeout:

```csharp
await page.GotoAsync($"{baseUrl}/blazor/");
await page.WaitForSelectorAsync("[data-testid='agent-list']", new() { Timeout = 30_000 });
```

## 7. Download Size

### Baseline

A trimmed .NET 10 Blazor WASM app is ~2-5MB compressed (Brotli). This includes the .NET runtime, framework assemblies, and app code.

### Mitigations

- **Brotli compression** — enabled by default in published Blazor WASM apps
- **IL trimming** — remove unused framework code
- **Lazy loading** — load admin/config assemblies on demand
- **Caching** — WASM files are fingerprinted and cached aggressively by the browser. First load is slow; subsequent loads are fast.

### Acceptable for BotNexus

This is a local developer tool, not a public website. Users are on localhost with essentially zero network latency. A 3-5MB download on first visit is negligible. The JS WebUI currently loads `signalr.min.js` (70KB), `marked.min.js` (48KB), `purify.min.js` (20KB), plus 12 JS files — already ~200KB uncompressed. The Blazor WASM overhead is the .NET runtime (~1.5MB compressed), which is a one-time cost.

## 8. Key Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| WASM rendering perf with rapid deltas | UI jank during fast streaming | Batch `StateHasChanged()` calls (50ms window), virtualize long message lists |
| SignalR extraction breaks existing JS UI | Users on existing UI see errors | Phase 1 keeps the same hub URL and contract; JS UI doesn't know the code moved |
| Channel-internal contract versioning | Client/server version mismatch | Same solution, same build — no NuGet versioning needed |
| Markdown rendering quality | Markdig output differs from marked.js | Start with JS interop to existing marked.js; migrate to Markdig later if needed |
| WASM single-thread blocking | Long synchronous operations freeze UI | All hub handlers are lightweight (dictionary append); rendering is async |
