# Leela Design Decision: Locations Config UI — Dynamic CRUD with No-Restart Reload

**Decision Date:** 2026-08-01  
**Decided By:** Leela (Lead/Architect)  
**Status:** In progress  
**Scope:** Gateway API, Blazor WebUI, runtime location resolver

---

## Context

The user wants to dynamically add, update, and remove locations from the Web UI configuration page without restarting the platform. PR #203 merged initial locations API + CLI support, but:

1. **No UI section** existed for locations in the Configuration page.
2. **`DefaultLocationResolver` is a singleton snapshot** — it reads `PlatformConfig` once at construction and never refreshes, so API writes to config.json don't propagate to runtime consumers until restart.
3. The `LocationsController` already has full CRUD (POST/PUT/DELETE) and writes directly to config.json via `PlatformConfigWriter`, which is the correct persistence path.
4. A `LocationsConfigPanel.razor` and `LocationsApiClient` service already exist but were not wired into the Configuration page routing.

## What's Done (Leela — this commit)

1. **Navigation:** Added `📍 Locations` link in `MainLayout.razor` sidebar under Configuration.
2. **Routing:** Added `case "locations"` in `Configuration.razor` switch to render `LocationsConfigPanel`.
3. **Build fix:** Added missing `using System.Text.Json.Nodes` in `LocationsController.cs` (pre-existing compilation error).
4. **Build fix:** Added missing `NSubstitute` package reference to `BotNexus.Gateway.Tests.csproj` (pre-existing).
5. **Removed duplicate** `case "locations"` in Configuration.razor that passed wrong parameters.

## What Remains — Assignments

### Bender (Runtime): DefaultLocationResolver no-restart reload

**Problem:** `DefaultLocationResolver` is registered as `TryAddSingleton<ILocationResolver>` and takes a snapshot of `IOptionsMonitor<PlatformConfig>.CurrentValue` at construction (line 252-256 of `GatewayServiceCollectionExtensions.cs`). After config.json is updated, the `IOptionsMonitor<PlatformConfig>` fires its change callback, but the resolver holds stale data.

**Required change:**
1. Inject `IOptionsMonitor<PlatformConfig>` (not `PlatformConfig`) into `DefaultLocationResolver`.
2. Subscribe to `OnChange` and rebuild the internal `_locations` dictionary atomically.
3. Use `Volatile.Write`/`Volatile.Read` or `Interlocked.Exchange` for the dictionary swap — callers may be on different threads.
4. Also inject `IAgentRegistry` and `IEnumerable<IIsolationStrategy>` (already present) so the rebuild produces the same WorldDescriptor-based resolution.

**Pattern to follow:** See `ApiKeyGatewayAuthHandler` which already takes `IOptionsMonitor<PlatformConfig>` and reacts to changes.

**DI registration change** (in `GatewayServiceCollectionExtensions.cs`):
```csharp
services.TryAddSingleton<ILocationResolver>(sp =>
    new DefaultLocationResolver(
        sp.GetRequiredService<IOptionsMonitor<PlatformConfig>>(),
        sp.GetService<IAgentRegistry>(),
        sp.GetServices<IIsolationStrategy>()));
```

**Files:**
- `src/gateway/BotNexus.Gateway/Configuration/DefaultLocationResolver.cs`
- `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs` (line ~252)

### Hermes (Testing): Fix pre-existing test compilation errors

**Problem:** Two test files don't compile:
1. `tests/gateway/BotNexus.Gateway.Tests/LocationsControllerTests.cs` — `EmptyAgentRegistry` class doesn't implement current `IAgentRegistry` interface (missing `Register(AgentDescriptor)`, wrong return types for `Get`/`GetAll`). Also missing `using BotNexus.Gateway.Abstractions.Agents;` for `AgentDescriptor`.
2. `tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests/LocationsConfigPanelTests.cs` — missing `RichardSzalay.MockHttp` package reference.

**Required:**
1. Fix `EmptyAgentRegistry` to implement the current `IAgentRegistry` contract (check `src/gateway/BotNexus.Gateway.Abstractions/Agents/IAgentRegistry.cs` for current shape).
2. Add `RichardSzalay.MockHttp` to `Directory.Packages.props` and the BlazorClient.Tests csproj.
3. Add tests for the `DefaultLocationResolver` reload behavior (after Bender's change).
4. All existing tests must continue to pass.

### Fry (Web UI): No changes needed

The `LocationsConfigPanel.razor` and `LocationsApiClient` are already implemented with full CRUD + health check UI. Routing is now wired. No further Fry work unless Bender's reload change requires UI adjustments (it shouldn't — the panel talks to the API which already re-reads config.json on each request).

## Architecture Notes

- **Config persistence path:** `LocationsController` → `PlatformConfigWriter.UpdateSectionAsync("gateway", ...)` → writes config.json → file watcher triggers `IOptionsMonitor<PlatformConfig>` reload → `DefaultLocationResolver` rebuilds (after Bender's fix).
- **No new API contracts needed.** The existing `POST/PUT/DELETE /api/locations` endpoints are sufficient.
- **LocationsConfigPanel uses the dedicated API** (not the generic config JSON editing pattern used by other panels). This is correct because locations have validation, health checks, and system-managed entries that the generic approach can't handle.
- **System-managed locations** (agents-directory, sessions-directory, gateway-api, agent workspaces, MCP servers) are read-only in the UI — the panel correctly shows them without edit/delete buttons.

## Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| Stale resolver after config change (current state) | HIGH | Bender's `IOptionsMonitor` fix eliminates this |
| Thread safety during dictionary swap | MEDIUM | Use `Interlocked.Exchange` for atomic swap |
| Test compilation blockers | MEDIUM | Hermes fixes pre-existing interface mismatches |
| UI panel already functional but untested in integration | LOW | Hermes adds bUnit tests for panel rendering |
