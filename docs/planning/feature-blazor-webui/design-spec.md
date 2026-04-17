---
id: feature-blazor-webui
title: "Blazor WebUI — Migrate from Plain JS to Blazor WASM SPA"
type: feature
priority: high
status: delivered
created: 2026-04-17
updated: 2026-07-19
tags: [webui, blazor, wasm, spa, signalr, testing, architecture]
---

# Feature: Blazor WebUI — Replace Plain JS with Blazor WASM SPA

**Status:** delivered (Phases 1-3 complete, Phase 4 partial — see Delivery Notes)
**Priority:** high (active initiative per Jon's direction)
**Created:** 2026-04-17
**Updated:** 2026-07-19

## Problem

The current WebUI is plain HTML/JS/CSS served as static files — no build tooling, no component model, no type safety. This causes:

1. **Test/UI drift** — The C# integration test client (`TestSignalRClient`) and the JS WebUI (`hub.js`, `events.js`, `chat.js`) implement the same SignalR contract independently. Bugs in one don't reproduce in the other (e.g., `Context.ConnectionId` captured after fire-and-forget worked in tests but broke the real UI).

2. **No component testing** — The JS UI can only be tested via Playwright E2E (91 tests, all conditional/skipped). No unit or component-level testing. Bugs in event handling, state management, or rendering are only caught by manual testing.

3. **String-typed hub contracts** — JS uses `connection.invoke("SendMessage", agentId, channelType, content)` with no compile-time validation. If the server signature changes, JS breaks silently at runtime.

4. **State management complexity** — `session-store.js` hand-rolls a channel/session state machine in vanilla JS. Race conditions between events, session switching, and streaming state are hard to reason about without a reactive framework.

5. **Duplicated models** — DTOs like `AgentStreamEvent`, `MediaContentPartDto`, session metadata are defined in C# and consumed in JS via untyped JSON parsing.

6. **Concurrency and session-switching bugs** — The current JS client cannot handle multiple agents responding simultaneously without cross-session bleed. The `SubscribeAll` model subscribes to all session groups, but the client-side routing (`routeEvent()` in `events.js`) fails when switching agents mid-stream — events from one agent render into another agent's chat canvas. The send-side race in `openAgentTimeline()` means `currentSessionId` can be stale/null during async switch operations, causing messages to route to the wrong session (see `bug-session-switching-ui` spec). These are fundamental architectural limitations of the single-threaded, global-state JS approach.

7. **SignalR channel is hardwired in Gateway.Api** — `GatewayHub`, `SignalRChannelAdapter`, `SubAgentSignalRBridge`, `CronTrigger`, `SoulTrigger`, and `MediaContentPartDto` all live in `BotNexus.Gateway.Api.Hubs`, tightly coupling the SignalR transport to the Gateway core. Specifically, `SignalRChannelAdapter` is registered directly in `GatewayApiServiceCollectionExtensions.cs` (line 29) as `services.AddSingleton<IChannelAdapter>(provider => provider.GetRequiredService<SignalRChannelAdapter>())` — bypassing the extension loader entirely. `GatewayHub` lives in `Gateway.Api/Hubs/` and WebUI static files in `Gateway.Api/wwwroot/`. None of this goes through the dynamic extension system. Note: `Channels.Telegram` and `Channels.Tui` exist as source projects under `src/channels/` but are currently stubs — Telegram is a planned channel extension that's blocked on the same `IEndpointContributor` infrastructure needed for SignalR. Once that gap is filled, Telegram slots in using the same pattern.

## Architecture Decisions (Confirmed)

These decisions have been made by Jon and are not open for debate:

1. **Blazor WASM** — Full client-side SPA. No Blazor Server, no server-side rendering. The server provides SignalR + REST APIs only.
2. **Run in parallel** — The Blazor UI is served at `/blazor/` alongside the existing WebUI. Both UIs are available simultaneously. This is not a replacement yet.
3. **Start simple, evolve** — No feature parity on day one. Start with core messaging, add features incrementally.
4. **Multi-agent concurrent interaction** — The Blazor client connects to all visible agents on all channels concurrently. No blocking, no single-active-session model. This is the key design goal that the current JS UI fails at.
5. **Host from Gateway (path-based)** — The Blazor WASM static files are served from the same Gateway process at `/blazor/`. No separate port — the channel extension registers its own static file paths and endpoints.
6. **SignalR becomes a channel extension** — Extract `GatewayHub`, `SignalRChannelAdapter`, `SubAgentSignalRBridge`, and related types from `BotNexus.Gateway.Api` into a proper `BotNexus.Channels.SignalR` extension project, loaded dynamically by the existing `AssemblyLoadContextExtensionLoader`. The web UI (both legacy JS and Blazor WASM) is part of this channel extension — the hub, adapter, bridge, contracts, DTOs, and UI are one cohesive unit. This requires extending the extension loader to support web/endpoint registration (see Extension Registration Model below).
7. **Reduce tech debt** — Stop accumulating hacks in the JS UI to fix concurrency bugs. The Blazor rewrite is the fix.

## SignalR Channel Extraction

### What Moves

The following types currently in `BotNexus.Gateway.Api.Hubs` move to `BotNexus.Channels.SignalR`:

| Type | Current Location | Notes |
|------|-----------------|-------|
| `GatewayHub` | `Gateway.Api/Hubs/GatewayHub.cs` (529 lines) | The SignalR hub. Depends on `IAgentSupervisor`, `IAgentRegistry`, `ISessionStore`, `IChannelDispatcher`, `IActivityBroadcaster`, `ISessionCompactor`, `ISessionWarmupService` — all from `Gateway.Abstractions`. |
| `SignalRChannelAdapter` | `Gateway.Api/Hubs/SignalRChannelAdapter.cs` (86 lines) | Implements `ChannelAdapterBase` + `IStreamEventChannelAdapter`. Uses `IHubContext<GatewayHub>` to push events to session groups. |
| `SubAgentSignalRBridge` | `Gateway.Api/Hubs/SubAgentSignalRBridge.cs` | `BackgroundService` that forwards sub-agent lifecycle events from `IActivityBroadcaster` to SignalR groups. |
| `MediaContentPartDto` | `Gateway.Api/Hubs/MediaContentPartDto.cs` | DTO for media uploads. Stays in the channel extension — it's a SignalR-specific concern. |
| `IGatewayHubClient` | New | Typed hub client interface. Internal to the channel extension — defines the server→client event contract. |
| Legacy JS WebUI | `BotNexus.WebUI/wwwroot/` | Static files move into (or are served by) the channel extension. |
| Blazor WASM client | New | `BotNexus.Channels.SignalR.BlazorClient` — lives inside the channel extension. |

**Key principle:** The hub is a SignalR implementation detail, not a gateway concept. If there were no SignalR channel, there would be no hub. Therefore `IGatewayHubClient`, `MediaContentPartDto`, typed hub return DTOs — these are ALL internal to `BotNexus.Channels.SignalR`. They don't belong in `Gateway.Contracts` or `Gateway.Abstractions`. The Blazor WASM project references them directly because it's part of the same channel extension.

### What Stays in Gateway.Api

| Type | Reason |
|------|--------|
| `CronTrigger` | Not SignalR-related. Implements `IInternalTrigger` for cron-triggered sessions. Should move to `BotNexus.Cron` or stay in Gateway.Api. |
| `SoulTrigger` | Not SignalR-related. Implements `IInternalTrigger` for soul-session heartbeats. |

### New Project Structure

```
src/channels/BotNexus.Channels.SignalR/
├── BotNexus.Channels.SignalR.csproj
├── GatewayHub.cs
├── SignalRChannelAdapter.cs
├── SubAgentSignalRBridge.cs
├── IGatewayHubClient.cs              # Typed hub client interface (internal to channel)
├── MediaContentPartDto.cs            # Upload DTO (internal to channel)
├── SendMessageResult.cs              # Typed hub return DTOs
├── SubscribeAllResult.cs
├── CompactSessionResult.cs
├── SignalREndpointContributor.cs           # IEndpointContributor — registers endpoints, static files, middleware
├── botnexus-extension.json                 # Extension manifest: extensionTypes: ["channel", "endpoint-contributor"]
├── wwwroot/                          # Legacy JS WebUI static files
│   ├── index.html
│   ├── js/
│   └── css/
└── README.md

src/channels/BotNexus.Channels.SignalR.BlazorClient/
├── BotNexus.Channels.SignalR.BlazorClient.csproj
├── wwwroot/
│   └── index.html
├── Program.cs
├── Services/
│   └── GatewayHubConnection.cs
├── Components/
│   ├── ChatPanel.razor
│   └── AgentList.razor
└── README.md
```

The Blazor WASM project references `BotNexus.Channels.SignalR` directly for `IGatewayHubClient`, `MediaContentPartDto`, and typed return DTOs. No separate contracts package needed — both projects are part of the same channel extension.

### Extension Registration Model

#### The Gap in the Current Extension System

The Gateway already has a mature dynamic extension loader (`AssemblyLoadContextExtensionLoader`) that discovers extensions from `botnexus-extension.json` manifests, loads assemblies via `AssemblyLoadContext`, and auto-discovers types implementing `DiscoverableServiceContracts`. Extensions already in production: exec, process, web, MCP, mcp-invoke, audio transcription, skills.

The current `DiscoverableServiceContracts` list covers service-level contracts:
```
IChannelAdapter, IIsolationStrategy, ISessionStore, IGatewayAuthHandler, IMessageRouter,
IAgentRegistry, IAgentSupervisor, IAgentCommunicator, IActivityBroadcaster, IAgentTool,
ICommandContributor, IMediaHandler
```

All existing extensions only needed service registrations into `IServiceCollection`. However, there is **NO mechanism** for extensions to:
- Map SignalR hubs
- Register API endpoints/controllers
- Serve static files (wwwroot)
- Add middleware

This is the critical gap. The SignalR channel needs all of these. Other extensions never hit this gap because they only needed service registrations.

#### Proposed Solution: `IEndpointContributor` + `IApiContributor`

Two new discoverable interfaces that let extensions participate in the ASP.NET pipeline after the `WebApplication` is built. The split keeps concerns specific: serving a UI/transport is different from enriching the shared API surface.

##### `IEndpointContributor` — Extension-Owned Surfaces

For static files, transport endpoints (SignalR hubs, webhook receivers), UI paths, and middleware. The extension has full control over its own routes.

```csharp
// Added to DiscoverableServiceContracts and Gateway.Abstractions

/// <summary>
/// Extension-owned endpoints, static files, middleware, transport surfaces.
/// The extension has full control over its routes.
/// </summary>
public interface IEndpointContributor
{
    /// <summary>
    /// Called during app startup with the built WebApplication.
    /// Extensions can register endpoints, static files, and middleware.
    /// Internal transport details (e.g., SignalR hubs, webhook receivers) are the extension's own concern.
    /// </summary>
    void MapEndpoints(WebApplication app);
}
```

##### `IApiContributor` — Shared API Surface

For contributing to the gateway's shared API surface. Extensions receive a **scoped `RouteGroupBuilder`** pre-namespaced (e.g., `/api/extensions/{extensionId}/`) so extensions can't stomp on each other or core API routes. The scoped route group provides governance (namespacing, auth, OpenAPI metadata) automatically.

```csharp
/// <summary>
/// Contributes to the gateway's shared API surface.
/// Extensions receive a scoped RouteGroupBuilder pre-namespaced to prevent route collisions.
/// </summary>
public interface IApiContributor
{
    /// <summary>
    /// Called during app startup with a scoped route group (e.g., /api/extensions/{extensionId}/).
    /// Extensions register their API endpoints within this scope.
    /// </summary>
    void MapApiRoutes(RouteGroupBuilder apiGroup);
}
```

> **Future consideration:** `IApiContributor` could potentially support contributing to existing core API resources (e.g., enriching `/api/sessions/` responses with channel-specific data). This is noted as a possibility but deliberately not designed now.

**Why two interfaces instead of one:**
- Keeps concerns specific — serving a UI/transport is different from enriching the shared API
- API contributions get governance (namespacing, auth, OpenAPI metadata) automatically via the scoped route group
- An extension can implement one or both interfaces independently
- Extensions that only need API surface don't touch the endpoint side, and vice versa

The extension loader needs a **post-build phase**: after `WebApplication` is constructed (services are locked), it iterates all discovered `IEndpointContributor` implementations and calls `MapEndpoints(app)`, and creates scoped route groups for each `IApiContributor` implementation and calls `MapApiRoutes(group)`. Currently the loader only participates during `IServiceCollection` registration.

The SignalR channel extension implements `IEndpointContributor` for its hub, static files, and middleware:

```csharp
// In BotNexus.Channels.SignalR
public class SignalREndpointContributor : IEndpointContributor
{
    public void MapEndpoints(WebApplication app)
    {
        // Register SignalR hub
        app.MapHub<GatewayHub>("/hub/gateway");

        // Serve legacy JS WebUI static files
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new EmbeddedFileProvider(
                typeof(GatewayHub).Assembly,
                "BotNexus.Channels.SignalR.wwwroot"),
        });

        // Serve Blazor WASM UI at /blazor/
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(blazorWwwrootPath),
            RequestPath = "/blazor"
        });

        // SPA fallbacks
        app.MapFallbackToFile("index.html");
        app.MapFallbackToFile("/blazor/{**path}", "blazor/index.html");
    }
}
```

If the SignalR channel needed to expose REST API endpoints (e.g., for channel configuration or diagnostics), it would separately implement `IApiContributor`:

```csharp
// Hypothetical — only needed if the SignalR channel exposes REST endpoints
public class SignalRApiContributor : IApiContributor
{
    public void MapApiRoutes(RouteGroupBuilder apiGroup)
    {
        // Routes are automatically scoped to /api/extensions/signalr/
        apiGroup.MapGet("status", () => Results.Ok(new { connected = true }));
        apiGroup.MapGet("connections", (IHubContext<GatewayHub> hub) => ...);
    }
}
```

The gateway's `Program.cs` has **ZERO SignalR-specific code**. It calls the extension loader, which discovers `IChannelAdapter` (for `SignalRChannelAdapter`) during service registration, `IEndpointContributor` (for `SignalREndpointContributor`) during the post-build phase, and `IApiContributor` (if implemented) for scoped API route registration.

The extension manifest (`botnexus-extension.json`) would declare its roles:
```json
{
  "name": "signalr",
  "extensionTypes": ["channel", "endpoint-contributor"],
  "assembly": "BotNexus.Channels.SignalR.dll"
}
```

An extension that also contributes API endpoints would add `"api-contributor"` to the list:
```json
{
  "extensionTypes": ["channel", "endpoint-contributor", "api-contributor"]
}
```

The `"channel"` type is already supported by the manifest format. The new `"endpoint-contributor"` and `"api-contributor"` types signal that the extension needs the post-build pipeline hook.

This pattern generalizes to any future extension that needs to serve UI, APIs, or middleware.

## Phased Implementation Plan

### Phase 1: Extract SignalR Channel + Extend Extension Loader

**Goal:** Clean architectural foundation. The SignalR channel becomes a self-contained, dynamically-loaded extension that owns the hub, adapter, bridge, contracts, DTOs, AND the web UI. The extension loader gains the ability to support web/endpoint extensions.

**Part A — Extend the extension loader:**
- Define `IEndpointContributor` and `IApiContributor` interfaces in `Gateway.Abstractions`
- Add both `IEndpointContributor` and `IApiContributor` to `DiscoverableServiceContracts`
- Add post-build phase to `AssemblyLoadContextExtensionLoader`: after `WebApplication` is built, iterate discovered `IEndpointContributor` types and call `MapEndpoints(app)`, and create scoped route groups (`/api/extensions/{extensionId}/`) for each `IApiContributor` and call `MapApiRoutes(group)`
- Add `"endpoint-contributor"` and `"api-contributor"` as recognized extension types in the manifest schema
- Existing extensions are unaffected (they don't implement either interface)

**Part B — Extract SignalR to extension:**
- Create `BotNexus.Channels.SignalR` project under `src/channels/`
- Move `GatewayHub`, `SignalRChannelAdapter`, `SubAgentSignalRBridge` from `Gateway.Api.Hubs`
- Relocate `CronTrigger` and `SoulTrigger` out of `Gateway.Api.Hubs` to a proper home in the gateway core (e.g., `Gateway/Triggers/` or `Gateway/Scheduling/`) — they're not SignalR concerns, just misplaced
- Move `MediaContentPartDto` into the channel extension (NOT to shared contracts — it's a SignalR concern)
- Define `IGatewayHubClient` interface inside the channel extension
- Define typed hub return DTOs (`SendMessageResult`, `SubscribeAllResult`, etc.) inside the channel extension
- Refactor `GatewayHub` to `Hub<IGatewayHubClient>` (strongly-typed)
- Move legacy JS WebUI static files into the channel extension
- Implement `SignalREndpointContributor : IEndpointContributor` to register hub, static files, and SPA fallbacks
- If REST API endpoints are needed, implement a separate `IApiContributor` class for scoped API routes
- Create `botnexus-extension.json` manifest with `extensionTypes: ["channel", "endpoint-contributor"]` (add `"api-contributor"` if API surface is needed)
- **Remove** all SignalR-specific code from `Gateway.Api/Program.cs` and `GatewayApiServiceCollectionExtensions.cs` — the extension loader handles everything dynamically
- Update `TestSignalRClient` to reference channel extension types directly
- All existing integration tests pass unchanged
- Existing JS WebUI continues to work (the hub URL doesn't change)

**Risk:** Two coupled changes — extraction + loader enhancement. Can be staged: implement the loader enhancement first with a test extension, then extract SignalR. The hub contract is already implicitly defined by `GatewayHub.cs` and `TestSignalRClient.cs` — making it explicit should be low-risk.

### Phase 2: Minimal Blazor WASM App

**Goal:** Prove the architecture. Connect, list agents, send/receive on one session.

- Create `BotNexus.Channels.SignalR.BlazorClient` Blazor WASM project inside the channel extension
- Host WASM static files at `/blazor/` via the channel extension's `SignalREndpointContributor`
- Implement `GatewayHubConnection` service — typed wrapper around `HubConnection` using `IGatewayHubClient` events
- Implement minimal pages:
  - Agent list sidebar
  - Single chat panel: send message, receive streaming response with markdown rendering
  - Connection status indicator
- Reference `IGatewayHubClient` and typed DTOs directly from the channel extension (compile-time type safety)
- Add bUnit tests for chat panel component with mocked hub connection

**Hosting:** The Blazor UI is served at `/blazor/` on the same port as the existing UI. The channel extension registers both sets of static files:
```csharp
// Legacy UI at root (e.g., http://localhost:5005/)
// Blazor UI at /blazor/ (e.g., http://localhost:5005/blazor/)
// SignalR hub at /hub/gateway (shared by both UIs)
// All registered dynamically by SignalREndpointContributor — zero hardcoded SignalR code in Gateway.Api
```

### Phase 3: Multi-Agent Concurrent Sessions

**Goal:** The key differentiator — what the JS UI can't do.

- `GatewayHubConnection` uses `SubscribeAll` to join all session groups on connect
- Per-session state containers: each agent/session gets its own message buffer, streaming state, loading state
- UI renders multiple agent panels or a tabbed/sidebar view with independent state
- Session switching is instant — just show/hide the already-populated panel, no re-fetch
- Background agents continue streaming into their buffers; UI updates when the user switches to that panel
- No global `currentSessionId` — each interaction is scoped to the panel it originates from
- Concurrent `SendMessage` calls to different agents fire independently (no blocking/queuing)

### Phase 4: Feature Parity

**Goal:** Full replacement for the JS UI.

- Streaming markdown rendering (Markdig or JS interop with `marked.min.js`)
- Code syntax highlighting
- Tool execution display (ToolStart/ToolEnd badges with timing)
- Sub-agent panel with lifecycle tracking (Spawned → Completed/Failed/Killed)
- Audio recording via JS interop (`MediaRecorder` API)
- Session history with scrollback and lazy loading
- Agent configuration sidebar
- Session management (reset, compact, archive)
- Steer and follow-up controls
- Dark theme
- Keyboard shortcuts
- Reconnection handling with re-subscribe

## Research Questions (Status)

### Architecture
1. **Blazor WASM vs Blazor Server vs Blazor Web App?** → ✅ **DECIDED: Blazor WASM.** Full client-side SPA. Server is SignalR + API only.

2. **Can Blazor WASM coexist with the existing SignalR hub?** → ✅ **ANSWERED: Yes.** Blazor WASM uses the standard `Microsoft.AspNetCore.SignalR.Client` `HubConnection` — same as `TestSignalRClient.cs` already does. The hub is a vanilla SignalR hub, not a Blazor circuit hub. No conflict.

3. **What about the existing static file serving?** → ✅ **DECIDED: Path-based.** Both UIs run simultaneously on the same port. Existing JS UI at root, Blazor WASM at `/blazor/`. Both are served by the SignalR channel extension.

### Hub Contracts
4. **Hub contracts and DTOs?** → ✅ **DECIDED: Internal to channel extension.** `IGatewayHubClient`, `MediaContentPartDto`, typed hub return DTOs all live inside `BotNexus.Channels.SignalR`. The Blazor WASM project references them directly as part of the same channel extension. No gateway-level shared contracts needed — the hub is a SignalR implementation detail, not a gateway concept.

5. **Strongly-typed hub client?** → ✅ **PLANNED: Phase 1.** `IGatewayHubClient` interface + `Hub<IGatewayHubClient>` refactor. Defined inside the channel extension.

### Testing
6. **bUnit for component testing?** → ✅ **ANSWERED: Yes.** See `research.md` for details. Mock `HubConnection` → inject into components → test rendering without a real server.

7. **Can integration tests share the Blazor client?** → ✅ **ANSWERED: Yes.** The `GatewayHubConnection` wrapper from Phase 2 replaces `TestSignalRClient`. Both use `HubConnection` + channel extension types. Single implementation, used in both tests and production.

8. **Playwright compatibility?** → ⚠️ **OPEN.** Blazor WASM works with Playwright but has gotchas (WASM load time, dynamic rendering). Needs validation in Phase 2.

### Performance & UX
9. **WASM download size?** → ⚠️ **OPEN.** Baseline ~2-5MB for trimmed Blazor WASM. Acceptable for a local dev tool. Lazy loading possible for admin pages. See `research.md`.

10. **Streaming rendering performance?** → ⚠️ **OPEN.** High-frequency `ContentDelta` events need careful rendering. Options: batch updates, `StateHasChanged` throttling, virtualized scrolling. Needs prototyping in Phase 2.

11. **Markdown rendering?** → ⚠️ **OPEN.** Options: Markdig (.NET, server-side or WASM), JS interop to existing `marked.min.js`. Needs evaluation.

### Migration Path
12. **Incremental migration?** → ✅ **DECIDED: Parallel, not incremental.** Both UIs run simultaneously. Blazor UI grows to parity, then JS UI is retired.

13. **Feature parity checklist?** → ✅ **DOCUMENTED.** See Phase 4 above.

14. **Browser API access?** → ✅ **ANSWERED: JS interop.** `MediaRecorder`, `navigator.clipboard`, etc. accessed via Blazor JS interop (`IJSRuntime`).

### Alternatives
15. **TypeScript instead?** → ✅ **DECIDED: No.** Blazor WASM chosen for full .NET stack alignment and shared contracts.

16. **React/Svelte/Vue?** → ✅ **DECIDED: No.** Same reasoning — .NET stack alignment, shared type system, single language for server+client.

## Context

- **Current WebUI:** `src/BotNexus.WebUI/wwwroot/` — 12 JS files, 1 HTML, 1 CSS, plus `marked.min.js`, `purify.min.js`, `signalr.min.js`
- **SignalR hub:** `src/channels/BotNexus.Channels.SignalR/GatewayHub.cs` — 13 client events, ~10 hub methods
- **Integration test client:** `tests/BotNexus.IntegrationTests/TestSignalRClient.cs` — mirrors WebUI behavior with hardcoded event list
- **Existing E2E tests:** 91 Playwright tests (all conditional/skipped)
- **Target framework:** .NET 10.0
- **Session-switching bug:** `docs/planning/bug-session-switching-ui/design-spec.md` — partially fixed, send-side race still open, cross-agent receive bleed still open during active streaming

## Delivery Notes

### Phase 1 — Delivered

**Commits:** `5c454b72`, `064c460a`, `c11da069`, `e1b1555a`, `34ada503`

1. **Extension loader infrastructure** — `IEndpointContributor` and `IApiContributor` interfaces, `DiscoverableServiceContracts` updated, `MapExtensionEndpoints()` post-build pipeline, manifest schema for `endpoint-contributor` and `api-contributor`.
2. **`BotNexus.Channels.SignalR` project** — `GatewayHub`, `SignalRChannelAdapter`, `SubAgentSignalRBridge`, `MediaContentPartDto`, `IGatewayHubClient`, `SignalREndpointContributor`. Triggers relocated to `Gateway.Api/Triggers/`.
3. **Typed hub contracts** — `Hub<IGatewayHubClient>` refactor, 11 named record DTOs replace anonymous objects (`HubContracts.cs`).
4. **Gateway decoupling** — Gateway.Api has zero SignalR references. Extension deploys to `~/.botnexus/extensions/signalr/` via MSBuild target. Extension loader discovers and loads it dynamically.
5. **Non-collectible ALC** — Extensions declaring `endpoint-contributor` load with `isCollectible: false` (SignalR's `Hub<T>` uses `Reflection.Emit`).

### Phase 2 — Delivered

**Commits:** `de0fa0cb`, `1c6bd9a8`

1. **`BotNexus.Channels.SignalR.BlazorClient`** — Blazor WASM project with `GatewayHubConnection` service (typed wrapper, C# events for all 13 hub methods, 7 client→server invocations).
2. **Minimal UI** — `Home.razor` page with agent list, `ChatPanel.razor` with streaming chat.
3. **Hosted at `/blazor/`** — `SignalREndpointContributor` serves WASM files via inline middleware. Blazor published output bundled alongside extension DLL.
4. **Client-side DTOs** — Separate `HubContracts.cs` matching JSON wire format (can't share server assembly due to ASP.NET FrameworkReference incompatibility with WASM).

### Phase 3 — Delivered

**Commit:** `1d6371d9`

1. **`AgentSessionManager`** — Routes all SignalR events by `sessionId` → correct agent state. Manages concurrent streaming, unread tracking.
2. **`AgentSessionState`** — Per-agent state: messages, stream buffer, unread count, session/channel tracking. `ChatMessage` record with tool metadata.
3. **Tabbed agent UI** — All agent panels stay in DOM (show/hide via CSS). Unread badges, streaming dots. Instant switching with no re-fetch.
4. **`ConnectionStatus.razor`** — Hub connection indicator.

### Phase 4 — Delivered (partial)

**Commit:** `acc3918f`

1. ✅ **Markdown rendering** — `marked.js` + `DOMPurify` via JS interop.
2. ✅ **Tool execution display** — Collapsible details with ⏳/✅/❌ icons and duration.
3. ✅ **Session management** — Reset with confirmation dialog, session ID display.
4. ✅ **History loading** — Fetches from REST API on first agent visit.
5. ✅ **Reconnection handling** — Re-subscribe + history reload for interrupted agents.
6. ✅ **Keyboard shortcuts** — Enter=send, Shift+Enter=newline, Escape=abort.
7. ✅ **Steer/abort controls** — Visible during streaming.
8. ✅ **Dark theme + responsive CSS** — Scrollbar styling, responsive breakpoints.

**Still pending (Phase 4):**
- ⬜ Audio recording (MediaRecorder JS interop)
- ⬜ Agent configuration sidebar
- ⬜ Sub-agent lifecycle panel
- ⬜ Full syntax highlighting (code blocks have monospace styling only)
- ⬜ bUnit component tests

### Bug fixes during delivery

| Commit | Fix |
|--------|-----|
| `b0e30a77` | `SessionStatus` enum → `[JsonStringEnumConverter]` for SignalR |
| `34a48a9e` | `AgentStreamEventType` enum → `[JsonStringEnumConverter]` for SignalR |
| `603242ed` | `ContentDelta` handler: `ContentDeltaPayload` → `AgentStreamEvent` |
| `b97f11de` | Blazor static file serving via inline middleware (endpoint routing bypassed `UseStaticFiles`) |
| `f4dd0457` | Non-collectible ALC for endpoint extensions + `ContinueOnError` on deploy target |
| `fd48a53b` | Use publish output for WASM hosting (fingerprint placeholders) |
| `159a0f73` | Gateway.Api fully decoupled — test factories use `AddSignalRChannelForTests()` helper |
