# Phase 1 Design Review — Extract SignalR Channel + Extend Extension Loader

**Reviewer:** Leela (Lead Architect)
**Date:** 2026-07-19
**Status:** Approved with notes
**Scope:** Phase 1 only (Part A: extension loader + Part B: SignalR extraction)

---

## 1. Architecture Validation

### 1.1 Design Spec vs. Codebase — Confirmed Alignment

The design spec accurately describes the current state:

| Claim | Verified | Evidence |
|-------|----------|----------|
| `GatewayHub` depends only on `Gateway.Abstractions` interfaces | ✅ | Lines 1–16 of `GatewayHub.cs` — injects `IAgentSupervisor`, `IAgentRegistry`, `ISessionStore`, `IChannelDispatcher`, `IActivityBroadcaster`, `ISessionCompactor`, `ISessionWarmupService`, `IOptionsMonitor<CompactionOptions>`. All from `Gateway.Abstractions`. |
| `SignalRChannelAdapter` extends `ChannelAdapterBase` from `Channels.Core` | ✅ | `SignalRChannelAdapter.cs` line 16 |
| `SubAgentSignalRBridge` depends on `IActivityBroadcaster` + `IHubContext<GatewayHub>` | ✅ | `SubAgentSignalRBridge.cs` line 14 |
| `CronTrigger`/`SoulTrigger` have zero SignalR dependencies | ✅ | Both only use `Gateway.Abstractions` interfaces |
| `DiscoverableServiceContracts` has no endpoint/API contributor | ✅ | `AssemblyLoadContextExtensionLoader.cs` lines 29–43 — 12 contracts, none for endpoints |
| `Program.cs` hardcodes `AddSignalR()`, `MapHub<GatewayHub>()`, `UseStaticFiles()`, `MapFallbackToFile()` | ✅ | `Program.cs` lines 103, 202–203, 213, 225 |
| `GatewayApiServiceCollectionExtensions` hardcodes SignalR adapter registration | ✅ | Line 28–29 |
| Extension manifest schema allows 12 extension types, none for endpoints | ✅ | `AssemblyLoadContextExtensionLoader.cs` lines 251–266 |

### 1.2 Interface Contracts — Precise Definitions

#### `IEndpointContributor`

**Location:** `BotNexus.Gateway.Abstractions` (new file, e.g., `Extensions/IEndpointContributor.cs`)

```csharp
using Microsoft.AspNetCore.Builder;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Extension-owned endpoints, static files, middleware, and transport surfaces.
/// Called during app startup after WebApplication is built.
/// </summary>
public interface IEndpointContributor
{
    /// <summary>
    /// Registers extension-owned endpoints (hubs, webhooks), static files, and middleware.
    /// </summary>
    void MapEndpoints(WebApplication app);
}
```

**Risk — ASP.NET dependency in Abstractions:** `WebApplication` lives in `Microsoft.AspNetCore.App`. Currently `Gateway.Abstractions` targets `net10.0` (not `Microsoft.NET.Sdk.Web`). Adding `IEndpointContributor` requires either:
- **(A)** Adding a `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to `Gateway.Abstractions.csproj`, or
- **(B)** Placing `IEndpointContributor` in a separate thin package (e.g., `Gateway.Abstractions.Web`)

**Recommendation:** Option A. `Gateway.Abstractions` already depends on domain primitives used by web-facing services; adding the framework reference is minimal friction and avoids a proliferation of abstraction packages. The `RouteGroupBuilder` for `IApiContributor` has the same requirement, so both interfaces land in the same project.

#### `IApiContributor`

**Location:** Same project as `IEndpointContributor`.

```csharp
using Microsoft.AspNetCore.Routing;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Contributes to the gateway's shared API surface.
/// Receives a scoped RouteGroupBuilder pre-namespaced to prevent route collisions.
/// </summary>
public interface IApiContributor
{
    /// <summary>
    /// Registers API endpoints within the provided scoped route group
    /// (e.g., /api/extensions/{extensionId}/).
    /// </summary>
    void MapApiRoutes(RouteGroupBuilder apiGroup);
}
```

**Note:** `IApiContributor` is not needed by the SignalR channel in Phase 1 — it has no REST endpoints. It's defined now to complete the contract surface and avoid a second loader refactor later.

#### `IGatewayHubClient`

**Location:** `BotNexus.Channels.SignalR` (internal to the channel extension — NOT in `Gateway.Abstractions`).

```csharp
namespace BotNexus.Channels.SignalR;

/// <summary>
/// Typed hub client interface. Defines server→client event contract.
/// </summary>
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

### 1.3 Extension Loader Post-Build Phase — Design

The current `LoadAsync` flow (lines 112–192 of `AssemblyLoadContextExtensionLoader.cs`) only touches `IServiceCollection`. The post-build phase is a separate call site — it runs in `Program.cs` **after** `builder.Build()` and **before** `app.Run()`.

**Proposed call site in Program.cs** (replaces all hardcoded SignalR lines):

```csharp
var app = builder.Build();

// ... existing middleware ...

// Extension post-build phase: endpoint + API contributors
var extensionLoader = app.Services.GetRequiredService<IExtensionLoader>();
foreach (var ext in extensionLoader.GetLoaded())
{
    // Resolve IEndpointContributor implementations from the extension's registered services
    foreach (var contributor in app.Services.GetServices<IEndpointContributor>())
        contributor.MapEndpoints(app);

    foreach (var apiContributor in app.Services.GetServices<IApiContributor>())
    {
        var group = app.MapGroup($"/api/extensions/{ext.ExtensionId}");
        apiContributor.MapApiRoutes(group);
    }
}
```

**Design decision:** `IEndpointContributor` and `IApiContributor` implementations are registered during the existing `DiscoverImplementations` + `RegisterServices` flow (they're in `DiscoverableServiceContracts`). The post-build phase simply resolves them from DI and calls their methods. This keeps the two phases cleanly separated:
1. **Service registration** (existing) — discovers and registers all contract implementations including `IEndpointContributor`/`IApiContributor`
2. **Endpoint mapping** (new) — resolves registered contributors and calls their `MapEndpoints`/`MapApiRoutes` methods

**Critical detail:** `IEndpointContributor` gets the full `WebApplication`. This is intentionally powerful — the SignalR channel needs `MapHub<T>()`, `UseStaticFiles()`, and `MapFallbackToFile()`. Governance is the extension author's responsibility for endpoint contributors. API contributors get the scoped route group for automatic namespacing.

---

## 2. Risks and Edge Cases

### R1 — Service Registration Timing for `AddSignalR()`

**Risk:** `SignalRChannelAdapter` needs `IHubContext<GatewayHub>` which requires `services.AddSignalR()`. Currently this is called in `Program.cs` line 103. After extraction, the SignalR extension must call `AddSignalR()` during service registration — but the current extension loader only does `services.AddSingleton(contract, implementation)`. There's no hook for extensions to register framework services.

**Mitigation:** Add an `IServiceConfigurator` pattern or simply have `SignalREndpointContributor` implement a `ConfigureServices(IServiceCollection)` method called during the service registration phase. Alternatively, since `AddSignalR()` is idempotent, it could be called in `MapEndpoints()` — but this is too late (services are already built).

**Recommended approach:** Add a new discoverable interface `IServiceConfigurator` with `void ConfigureServices(IServiceCollection services)`, called during the existing service registration phase. The SignalR extension's configurator calls `AddSignalR()` and `AddHostedService<SubAgentSignalRBridge>()`.

**Update:** On reflection, this can be simpler. The `RegisterServices` method in the loader already has access to `IServiceCollection`. We can extend it to check if the implementation also implements a marker like `IRequiresServiceConfiguration` — but that's over-engineering. The simplest approach: add `AddSignalR()` to the gateway core's `Program.cs` unconditionally (it's a no-op if unused) and let the extension focus on `MapHub<T>()` in the endpoint contributor. The `AddHostedService<SubAgentSignalRBridge>()` can be handled by adding `IHostedService` to `DiscoverableServiceContracts` or by having the extension call it during a configure phase.

**Final recommendation:** Keep `builder.Services.AddSignalR()` in `Program.cs` as an unconditional framework capability (like `AddControllers()` already is). The extension loader discovers `SubAgentSignalRBridge` as an `IHostedService` — add `IHostedService` to `DiscoverableServiceContracts` to enable this pattern for any extension.

### R2 — `IHostedService` Registration via Extension Loader

**Risk:** `SubAgentSignalRBridge` is a `BackgroundService` (which implements `IHostedService`). Currently registered via `services.AddHostedService<SubAgentSignalRBridge>()` in `GatewayApiServiceCollectionExtensions`. The extension loader's `RegisterServices` uses `AddSingleton`, but hosted services need `AddHostedService` or at minimum `AddSingleton<IHostedService, T>()`.

**Mitigation:** The loader's `RegisterServices` already handles multi-registration contracts (line 356–361: `IChannelAdapter`, `IIsolationStrategy`, `IAgentTool`, `ICommandContributor`, `IMediaHandler`). Add `IHostedService` to that list. This lets any extension contribute background services.

### R3 — Static File Path Resolution

**Risk:** After extraction, the SignalR extension's `wwwroot` is in `src/channels/BotNexus.Channels.SignalR/wwwroot/`. At runtime, the extension DLL is loaded from the extensions directory (e.g., `~/.botnexus/extensions/signalr/`). Static files need to be either embedded resources or physically present alongside the DLL.

**Mitigation:** Two options:
- **Embedded resources** (reliable, no path issues, slightly larger DLL)
- **Physical files** copied to the extension's output directory (standard for web projects)

**Recommendation:** Use `<Content Include="wwwroot\**" CopyToOutputDirectory="PreserveNewest" />` in the csproj. The extension's `MapEndpoints` resolves static files relative to `Assembly.GetExecutingAssembly().Location`. This matches how ASP.NET web projects work.

### R4 — Manifest Schema Extension

**Risk:** Adding `"endpoint-contributor"` and `"api-contributor"` to the `allowedTypes` set in `ValidateManifest` (line 251–266) is straightforward but must happen before any extension uses these types.

**Mitigation:** Part A of the implementation adds these types. Low risk.

### R5 — `GatewayHub` Anonymous Return Types

**Risk:** `GatewayHub` methods return anonymous objects (`new { sessionId = ..., agentId = ... }`). These can't be shared with the Blazor client as typed DTOs. The `TestSignalRClient` currently deserializes them as `JsonElement`.

**Mitigation (Phase 1):** Define named record types (`SendMessageResult`, `SubscribeAllResult`, `JoinSessionResult`, `CompactSessionResult`) inside `BotNexus.Channels.SignalR`. Refactor `GatewayHub` to return these instead of anonymous types. This is a breaking change for `TestSignalRClient` deserialization — but since the JSON shape is identical, only the C# consumption code changes.

### R6 — Test Impact

**Risk:** Three test files reference `GatewayHub` or `SignalRChannelAdapter`:
- `tests/BotNexus.Gateway.Tests/SignalRHubTests.cs`
- `tests/BotNexus.Gateway.Tests/SignalRChannelAdapterTests.cs`
- `tests/BotNexus.Gateway.Tests/ChannelCapabilityTests.cs`
- `tests/BotNexus.IntegrationTests/TestSignalRClient.cs`
- `tests/BotNexus.IntegrationTests/ScenarioRunner.cs`

After extraction, these tests need updated project references to `BotNexus.Channels.SignalR` and updated `using` statements (namespace changes from `BotNexus.Gateway.Api.Hubs` to `BotNexus.Channels.SignalR`).

**Mitigation:** Mechanical refactor — namespace changes and project reference updates. No logic changes needed. The `TestSignalRClient` integration tests validate that the extraction didn't break the hub contract.

### R7 — `CronTrigger`/`SoulTrigger` Relocation

**Risk:** These live in `Gateway.Api.Hubs` namespace but have zero SignalR dependency. They must move out before the `Hubs/` directory is removed. The design spec suggests `Gateway/Triggers/` or `Gateway/Scheduling/`.

**Mitigation:** Move to `BotNexus.Gateway.Api/Triggers/` (new folder in the same project). Namespace becomes `BotNexus.Gateway.Api.Triggers`. This is a pure rename — the types are only referenced via `IInternalTrigger` interface from DI. Registration in `GatewayApiServiceCollectionExtensions` updates from `using BotNexus.Gateway.Api.Hubs` to `using BotNexus.Gateway.Api.Triggers`.

### R8 — `WebApplication` Coupling in `IEndpointContributor`

**Risk:** Passing the full `WebApplication` to extension code is powerful but dangerous — a malicious or buggy extension could call `app.Run()`, register conflicting middleware, or override core routes.

**Mitigation (acceptable for Phase 1):** All extensions are first-party. Document that `MapEndpoints` is for endpoint registration only. Future hardening could wrap `WebApplication` in a constrained adapter, but that's over-engineering for now.

---

## 3. Wave Breakdown

### Wave 1 — Foundation: Interfaces + Manifest Schema

**What:** Define the new contracts and update the extension loader's discovery/validation.

| Work Item | Agent | Details |
|-----------|-------|---------|
| Define `IEndpointContributor` interface | Farnsworth (platform) | New file in `Gateway.Abstractions/Extensions/`. Add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to `Gateway.Abstractions.csproj`. |
| Define `IApiContributor` interface | Farnsworth (platform) | Same file or adjacent file in `Gateway.Abstractions/Extensions/`. |
| Add both to `DiscoverableServiceContracts` | Farnsworth (platform) | `AssemblyLoadContextExtensionLoader.cs` line 29–43: add `typeof(IEndpointContributor)` and `typeof(IApiContributor)` to the array. |
| Add `IHostedService` to `DiscoverableServiceContracts` | Farnsworth (platform) | Same array. Enables extensions to contribute background services. Add to multi-registration list (line 356–361). |
| Add `"endpoint-contributor"` and `"api-contributor"` to manifest `allowedTypes` | Farnsworth (platform) | `AssemblyLoadContextExtensionLoader.cs` line 251–266: add two strings. |
| Add `IEndpointContributor`/`IApiContributor` type-forwards | Farnsworth (platform) | `TypeForwards.cs` in `Gateway.Abstractions`. |
| Unit tests for new discoverable contracts | Hermes (tests) | Verify extension loader discovers `IEndpointContributor`/`IApiContributor` implementations. Test manifest validation accepts new types. |

**Parallelism:** Farnsworth works all items sequentially (they're in the same files). Hermes writes tests in parallel once interfaces are defined.

**Verification:**
- `dotnet build BotNexus.slnx` passes
- `dotnet test BotNexus.slnx` passes — all existing tests green
- Extension loader unit tests verify discovery of new contract types
- Existing extensions remain unaffected (they don't implement either interface)

---

### Wave 2 — Post-Build Phase in Loader + Program.cs Hook

**What:** Wire the post-build phase so discovered endpoint/API contributors get called after `WebApplication` is built.

| Work Item | Agent | Details |
|-----------|-------|---------|
| Add post-build method to `IExtensionLoader` (or a new `IExtensionEndpointMapper` service) | Farnsworth (platform) | New method or helper that iterates loaded extensions, resolves `IEndpointContributor` and `IApiContributor` from DI, and calls their methods. |
| Wire post-build call in `Program.cs` | Bender (runtime) | After `builder.Build()`, before `app.Run()`. Replaces where hardcoded SignalR lines will eventually go (but doesn't remove them yet). |
| Integration test with a mock `IEndpointContributor` | Hermes (tests) | Create a test extension that implements `IEndpointContributor`, verify `MapEndpoints` is called during startup. |

**Parallelism:** Farnsworth and Bender work sequentially (Bender depends on Farnsworth's method). Hermes writes tests once the hook exists.

**Verification:**
- Post-build phase calls `MapEndpoints` on all discovered `IEndpointContributor` implementations
- Scoped route groups created for `IApiContributor` at `/api/extensions/{extensionId}/`
- All existing tests pass (no behavioral change — existing code still hardcoded alongside the hook)

---

### Wave 3 — Create `BotNexus.Channels.SignalR` + Move Types

**What:** Create the new project, move types, relocate triggers. The gateway still hardcodes SignalR registration — this wave is about code movement only.

| Work Item | Agent | Details |
|-----------|-------|---------|
| Create `BotNexus.Channels.SignalR` project | Bender (runtime) | Under `src/channels/`. References: `Channels.Core`, `Gateway.Abstractions`. SDK: `Microsoft.NET.Sdk.Web` (or `Microsoft.NET.Sdk` + FrameworkReference). |
| Move `GatewayHub.cs` → `Channels.SignalR/` | Bender (runtime) | Change namespace to `BotNexus.Channels.SignalR`. |
| Move `SignalRChannelAdapter.cs` → `Channels.SignalR/` | Bender (runtime) | Change namespace. |
| Move `SubAgentSignalRBridge.cs` → `Channels.SignalR/` | Bender (runtime) | Change namespace. |
| Move `MediaContentPartDto.cs` → `Channels.SignalR/` | Bender (runtime) | Change namespace. |
| Define `IGatewayHubClient` interface | Bender (runtime) | New file in `Channels.SignalR/`. Based on research.md contract. |
| Define typed hub return DTOs | Bender (runtime) | `SendMessageResult`, `SubscribeAllResult`, `JoinSessionResult`, `CompactSessionResult` — records in `Channels.SignalR/`. |
| Refactor `GatewayHub` to `Hub<IGatewayHubClient>` | Bender (runtime) | Replace string-based `SendAsync` with typed client calls. Replace anonymous returns with named DTOs. |
| Create `SignalREndpointContributor` | Bender (runtime) | Implements `IEndpointContributor`. Calls `MapHub<GatewayHub>()`, `UseStaticFiles()`, `MapFallbackToFile()`. |
| Create `botnexus-extension.json` manifest | Bender (runtime) | `extensionTypes: ["channel", "endpoint-contributor"]`. |
| Move `CronTrigger` → `Gateway.Api/Triggers/` | Fry (web) | Namespace: `BotNexus.Gateway.Api.Triggers`. Update registration in `GatewayApiServiceCollectionExtensions`. |
| Move `SoulTrigger` → `Gateway.Api/Triggers/` | Fry (web) | Same as above. |
| Move `wwwroot/` static files → `Channels.SignalR/wwwroot/` | Fry (web) | Copy existing JS WebUI files. Configure csproj to include as content. |
| Update all test project references | Hermes (tests) | Add reference to `BotNexus.Channels.SignalR`. Update `using` statements. |
| Update test namespaces | Hermes (tests) | `BotNexus.Gateway.Api.Hubs` → `BotNexus.Channels.SignalR` in all test files. |

**Parallelism:**
- Bender (type moves + new types) and Fry (trigger relocation + static files) work **in parallel** — different files, no conflicts.
- Hermes starts test updates once Bender's namespace changes are committed.

**Verification:**
- `dotnet build BotNexus.slnx` passes (Gateway.Api temporarily references Channels.SignalR for the transition)
- All existing tests pass with updated references
- `GatewayHub` compiles as `Hub<IGatewayHubClient>` with typed returns
- `Gateway.Api/Hubs/` directory contains only `CronChannelAdapter.cs` (if it stays) — review whether `CronChannelAdapter` also needs relocation

---

### Wave 4 — Cut Over: Remove Hardcoded SignalR from Gateway.Api

**What:** Remove all SignalR-specific code from `Program.cs` and `GatewayApiServiceCollectionExtensions.cs`. The extension loader handles everything.

| Work Item | Agent | Details |
|-----------|-------|---------|
| Remove `services.AddSignalR()` specificity | Bender (runtime) | Keep `AddSignalR()` in `Program.cs` as a framework capability (like `AddControllers()`), but remove SignalR channel adapter registration from `GatewayApiServiceCollectionExtensions`. |
| Remove `SignalRChannelAdapter` registration from `GatewayApiServiceCollectionExtensions` | Bender (runtime) | Lines 28–30. The extension loader discovers it via `IChannelAdapter`. |
| Remove `SubAgentSignalRBridge` hosted service registration | Bender (runtime) | Line 30. Discovered via `IHostedService` in `DiscoverableServiceContracts`. |
| Remove `app.MapHub<GatewayHub>()` from `Program.cs` | Bender (runtime) | Line 213. `SignalREndpointContributor.MapEndpoints()` handles this. |
| Remove `app.UseStaticFiles()` / `app.MapFallbackToFile()` from `Program.cs` | Bender (runtime) | Lines 202–203, 225. Extension handles static files. Evaluate whether `UseDefaultFiles()` (line 202) can also move. |
| Remove `GatewayHub` using/import from `Program.cs` | Bender (runtime) | Line 2. |
| Configure extension deployment | Bender (runtime) | Ensure `BotNexus.Channels.SignalR` output lands in the extensions directory (build target or post-build copy). |
| Remove now-empty `Gateway.Api/Hubs/` directory | Fry (web) | After all types are moved. Verify no remaining files. |
| Remove `Gateway.Api` → `Channels.Core` reference if no longer needed | Fry (web) | Check if any remaining code in `Gateway.Api` references `Channels.Core`. |
| Full regression test suite | Hermes (tests) | Run all unit + integration tests. The hub URL (`/hub/gateway`) must remain identical. |
| Verify extension loading at startup | Hermes (tests) | Integration test: gateway starts, extension loader discovers and loads `BotNexus.Channels.SignalR`, hub is accessible, static files served. |
| Update docs | Kif (docs) | Update `README.md` if it references the hub location. Update any architecture diagrams. |

**Parallelism:** Bender works sequentially through the removals (all in `Program.cs` / `GatewayApiServiceCollectionExtensions`). Fry cleanup in parallel. Hermes validates after Bender completes. Kif works independently.

**Verification:**
- `dotnet build BotNexus.slnx` passes
- `dotnet test BotNexus.slnx` passes — **zero test failures**
- Gateway starts successfully with the SignalR channel loaded as an extension
- `http://localhost:5005/hub/gateway` responds (SignalR negotiation)
- `http://localhost:5005/` serves the legacy JS WebUI
- `http://localhost:5005/api/version` still works (core API unaffected)
- Extension loader logs show `signalr` extension loaded with `IChannelAdapter`, `IEndpointContributor`
- No SignalR-specific imports remain in `Program.cs` or `GatewayApiServiceCollectionExtensions`

---

## 4. Dependency Graph

```
Wave 1 ──→ Wave 2 ──→ Wave 3 ──→ Wave 4
(interfaces)  (post-build)  (move types)  (cut over)
```

All waves are **strictly sequential**. Each wave must pass `dotnet test` before proceeding to the next.

Within each wave, the parallel assignments noted above can execute simultaneously.

---

## 5. Open Items for Jon

1. **`AddSignalR()` placement:** Keep in `Program.cs` unconditionally (recommended) or move into the extension? If in the extension, we need an `IServiceConfigurator` pattern since services are locked after `Build()`.

2. **`CronChannelAdapter.cs`** still exists in `Gateway.Api/Hubs/`. It's not mentioned in the design spec. Should it move to `Gateway.Api/Triggers/` alongside `CronTrigger`/`SoulTrigger`?

3. **Extension deployment model:** Should `BotNexus.Channels.SignalR` build output go to `extensions/signalr/` (like other extensions) or stay as a project reference during Phase 1 development? Project reference is simpler for development; extension directory is the target architecture.

4. **`IHostedService` in `DiscoverableServiceContracts`:** Adding this enables any extension to contribute background services. Are there security/lifecycle concerns with arbitrary extensions running background tasks?

---

## 6. Summary

The design is **sound and well-researched**. The spec correctly identifies the extension system gap and proposes a clean two-interface solution. The 4-wave breakdown stages the work to minimize integration risk — each wave is independently buildable and testable.

**Key architectural decisions validated:**
- Hub contracts (`IGatewayHubClient`, DTOs) are internal to the channel extension — correct
- `IEndpointContributor` gets full `WebApplication` — appropriate for first-party extensions
- `IApiContributor` gets scoped `RouteGroupBuilder` — proper governance for API surface
- Triggers are correctly identified as non-SignalR concerns to relocate
- `Gateway.Abstractions` is the right home for both new interfaces

**Primary risk:** R1 (service registration timing for `AddSignalR()`). The recommended mitigation (keep `AddSignalR()` in `Program.cs` as a framework capability) is pragmatic and avoids over-engineering the service configurator pattern in Phase 1.
