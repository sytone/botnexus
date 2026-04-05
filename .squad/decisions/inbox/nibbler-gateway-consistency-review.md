# Gateway Service — Consistency Review

**Reviewer:** Nibbler (Consistency Reviewer)
**Date:** 2026-07-18
**Scope:** All 5 new Gateway projects + Channels.Core, cross-referenced against AgentCore and Providers.Core

---

## Summary

The Gateway Service is **well-built and highly consistent internally**. XML doc coverage is effectively 100% on public APIs, naming conventions are clean, and the project structure is exemplary. I found **0 P0 issues**, **4 P1 issues**, and **7 P2 issues**. The most significant finding is a `ConfigureAwait(false)` policy divergence between Gateway (never uses it) and AgentCore (uses it everywhere). This is a deliberate architectural choice that should be documented, not a bug.

---

## 1. Naming Conventions

### ✅ Namespace Pattern: Consistent

All Gateway projects follow `BotNexus.{Module}.{Submodule}`:
- `BotNexus.Gateway.Abstractions.Activity`
- `BotNexus.Gateway.Agents`
- `BotNexus.Gateway.Api.Controllers`
- `BotNexus.Gateway.Sessions`
- `BotNexus.Channels.Core`

This matches existing patterns: `BotNexus.AgentCore.Loop`, `BotNexus.Providers.Core.Models`, etc.

### ✅ Interface/Class Names: Consistent

Gateway follows the same patterns as the rest of the codebase:
- Interfaces prefixed with `I` (`IAgentRegistry`, `ISessionStore`, `IMessageRouter`)
- Implementations use `Default*` prefix for primary implementations (`DefaultAgentRegistry`, `DefaultMessageRouter`)
- This matches AgentCore/Providers.Core conventions

### ⚠️ P1-01: CancellationToken Parameter Naming Inconsistency

**Finding:** Gateway API layer uses `ct` for CancellationToken parameters; abstractions and core implementations use `cancellationToken`.

| Location | Parameter Name |
|----------|---------------|
| `src/gateway/BotNexus.Gateway.Abstractions/**` | `cancellationToken` |
| `src/gateway/BotNexus.Gateway/**` | `cancellationToken` |
| `src/gateway/BotNexus.Gateway.Api/Controllers/AgentsController.cs:72` | `ct` |
| `src/gateway/BotNexus.Gateway.Api/Controllers/ChatController.cs:29` | `ct` |
| `src/gateway/BotNexus.Gateway.Api/Controllers/SessionsController.cs:20,25,33` | `ct` |
| `src/gateway/BotNexus.Gateway.Api/WebSocket/GatewayWebSocketHandler.cs:110,146,181` | `ct` |
| `src/gateway/BotNexus.Gateway.Sessions/FileSessionStore.cs:123,156` | `ct` (private methods) |
| `src/agent/BotNexus.AgentCore/**` | `cancellationToken` (everywhere) |
| `src/agent/BotNexus.AgentCore/Loop/ContextConverter.cs` | `ct` (1 exception) |

**Assessment:** The `ct` abbreviation in the API layer is common in ASP.NET Core controller patterns but breaks consistency with the rest of the BotNexus codebase which overwhelmingly uses `cancellationToken`. The `FileSessionStore` private methods also use `ct`.

**Suggested Fix:** Rename `ct` → `cancellationToken` in all Gateway.Api controllers and FileSessionStore private methods for consistency. The one `ct` usage in AgentCore's ContextConverter is a pre-existing exception.

---

## 2. Code Patterns

### ⚠️ P1-02: ConfigureAwait Policy Divergence

**Finding:** Gateway code **never** uses `ConfigureAwait(false)`. AgentCore uses it **extensively** (79+ usages across 5 files).

| Project | ConfigureAwait Usage |
|---------|---------------------|
| `src/gateway/**` (all 4 projects) | **0 usages** |
| `src/channels/BotNexus.Channels.Core/` | **0 usages** |
| `src/agent/BotNexus.AgentCore/` | **79+ usages** (Agent.cs, AgentLoopRunner.cs, ContextConverter.cs, StreamAccumulator.cs, ToolExecutor.cs) |

**Assessment:** This is likely an intentional decision — AgentCore is a library consumed by various hosts, so `ConfigureAwait(false)` prevents deadlocks. Gateway runs under ASP.NET Core where the synchronization context is already handled. However, the Gateway.Sessions `FileSessionStore` is a library class that could be consumed outside ASP.NET Core, where lacking `ConfigureAwait(false)` could cause issues.

**Suggested Fix:** Document the `ConfigureAwait` policy in a code conventions doc or in a comment in `FileSessionStore`. Consider adding `ConfigureAwait(false)` to `FileSessionStore` since it's a reusable library component.

### ✅ Thread Safety Pattern: Intentional and Well-Chosen

The Gateway uses two thread-safety mechanisms and the choice is deliberate:

| Mechanism | Where Used | Why |
|-----------|-----------|-----|
| C# 13 `Lock` type | `DefaultAgentRegistry`, `DefaultAgentSupervisor`, `InMemoryActivityBroadcaster`, `InMemorySessionStore` | Synchronous or sync-over-async code; `Lock` is more efficient than `object` locks |
| `SemaphoreSlim(1,1)` | `FileSessionStore` | Needs async-compatible locking for `File.ReadAllLinesAsync()` etc. |

For comparison, AgentCore uses `object` locks (older style) and `SemaphoreSlim`. The Gateway's use of C# 13 `Lock` is a modernization — no inconsistency issue.

### ⚠️ P2-01: Collection Initialization Mixed Style for Dictionary Defaults

**Finding:** `Dictionary<>` default initialization uses two different styles in the same project:

| File | Style |
|------|-------|
| `Models/GatewaySession.cs:35` | `Dictionary<string, object?> Metadata { get; init; } = [];` |
| `Models/Messages.cs:37-38` | `IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();` |
| `Models/AgentDescriptor.cs:54-55` | `IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();` |
| `Models/AgentExecution.cs:19` | `IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();` |

**Assessment:** The `[]` syntax works for `Dictionary<>` on the left side but not for `IReadOnlyDictionary<>` (which requires explicit `new Dictionary<>()`). This is a language constraint, not a real inconsistency. The usage is correct everywhere.

**Suggested Fix:** No code change needed. Add a brief comment in the one `[]` case explaining the difference if it causes confusion in reviews.

### ✅ Record vs Class Usage: Appropriate

- **Records:** Used for immutable DTOs (`InboundMessage`, `OutboundMessage`, `SessionEntry`, `AgentDescriptor`, `AgentStreamEvent`, `GatewayActivity`, etc.)
- **Classes:** Used for mutable state containers (`GatewaySession` with mutable `History`, `AgentInstance` with mutable `Status`)
- This matches AgentCore patterns where records represent immutable data and classes represent stateful objects.

### ✅ Async Pattern: Consistent

All async methods use `Async` suffix. `IAsyncEnumerable` returns use `[EnumeratorCancellation]` attribute correctly.

---

## 3. Project Structure

### ✅ .csproj Conventions: Exemplary

All Gateway projects share:
- `net10.0` target framework
- `Nullable: enable`
- `ImplicitUsings: enable`
- Explicit `RootNamespace`
- Descriptive `Description` element
- Consistent package versions (`Microsoft.Extensions.*.Abstractions 10.0.0-preview.3.25171.5`)

### ⚠️ P2-02: .csproj Property Order Inconsistency (Pre-existing)

**Finding:** `Providers.Core.csproj` orders properties differently from all other projects:
- **Gateway/Channels/most others:** `TargetFramework` → `ImplicitUsings` → `Nullable` → `RootNamespace`
- **Providers.Core:** `TargetFramework` → `Nullable` → `ImplicitUsings` → `RootNamespace`

**Assessment:** Pre-existing; not introduced by Gateway. Gateway is the more consistent one here.

### ⚠️ P2-03: AgentCore Missing RootNamespace (Pre-existing)

**Finding:** `BotNexus.AgentCore.csproj` lacks an explicit `<RootNamespace>` property. All Gateway projects and most other projects define it.

**Assessment:** Pre-existing; not introduced by Gateway. Not a functional issue since the folder name matches the desired namespace.

### ✅ Solution File Organization

The `.slnx` file properly groups Gateway projects under `/src/gateway/` and Channels.Core under `/src/channels/`. Test projects under `/tests/`. Clean and organized.

---

## 4. Cross-Reference with Archive

### ✅ Intentional Renames — No Conflicts in Live Code

The archive and new Gateway share type names but with intentional evolution:

| Archive Type | Current Type | Assessment |
|-------------|-------------|------------|
| `InboundMessage` (Core) | `InboundMessage` (Gateway.Abstractions) | Same name, different namespace, different properties (`Channel`→`ChannelType`, `ChatId`→`ConversationId`). No conflict since archive is not compiled. |
| `OutboundMessage` (Core) | `OutboundMessage` (Gateway.Abstractions) | Same pattern. No conflict. |
| `Session` (Core) | `GatewaySession` (Gateway.Abstractions) | **Wisely renamed** to avoid confusion. |
| `SessionEntry` (Core) | `SessionEntry` (Gateway.Abstractions) | Same name. Archive uses `MessageRole` enum; new uses `string Role`. No conflict. |
| `IAgentRouter` (Gateway) | `IMessageRouter` (Gateway.Abstractions) | **Renamed** — more precise semantics. |
| `IChannel` (Core) | `IChannelAdapter` (Gateway.Abstractions) | **Renamed** — adapter pattern explicit. |
| `Gateway` class | `GatewayHost` | **Renamed** — avoids namespace/class collision. |

### ⚠️ P2-04: SessionEntry Role Type Change Deserves Documentation

**Finding:** Archive `SessionEntry.Role` is `MessageRole` enum (`User`, `Assistant`, `System`, `Tool`). New `SessionEntry.Role` is `string` ("user", "assistant", "system", "tool").

**File:** `src/gateway/BotNexus.Gateway.Abstractions/Models/GatewaySession.cs:44`

**Assessment:** Using strings is more flexible for serialization and extensibility. However, it loses compile-time safety. This is a valid design choice, but the rationale should be captured.

**Suggested Fix:** Add an XML doc remark on `SessionEntry.Role` noting the valid values or consider a constants class (e.g., `SessionRoles.User`, `SessionRoles.Assistant`).

---

## 5. Test Conventions

### ✅ Test Framework: Consistent

All projects use **xUnit** with `[Fact]` and `[Theory]/[InlineData]`. No framework mixing.

### ✅ Test Method Naming: Consistent

All Gateway tests follow `MethodName_Condition_ExpectedResult`:
- `Register_WithValidDescriptor_AddsAgent`
- `GetOrCreateAsync_WithUnknownSession_CreatesSession`
- `ResolveAsync_WithExplicitTarget_RoutesToTargetAgent`

This matches AgentCore (`PromptAsync_WhenAlreadyRunning_ThrowsInvalidOperationException`) and Providers.Core (`GetApiKey_UnknownProvider_ReturnsNull`).

### ✅ Assertion Library: Consistent

FluentAssertions exclusively across all three test projects. No mixing with xUnit Assert or other libraries.

### ⚠️ P1-03: Test File Name ↔ Class Name Mismatches

**Finding:** Several Gateway test files have names that don't match their test class:

| File | Test Class |
|------|-----------|
| `AgentLifecycleTests.cs` | `DefaultAgentRegistryTests` |
| `ChannelAdapterTests.cs` | `SessionsControllerTests` |
| `IsolationStrategyTests.cs` | `InMemoryActivityBroadcasterTests` |
| `RestApiTests.cs` | `AgentsControllerTests` |
| `WebSocketProtocolTests.cs` | `DefaultMessageRouterTests` |

**Assessment:** These are misleading. Someone looking for router tests wouldn't think to check `WebSocketProtocolTests.cs`. In the existing codebase, file names match class names:
- `AgentTests.cs` → `AgentTests`
- `PendingMessageQueueTests.cs` → `PendingMessageQueueTests`
- `EnvironmentApiKeysTests.cs` → `EnvironmentApiKeysTests`

**Suggested Fix:** Rename test files to match their class names:
- `AgentLifecycleTests.cs` → `DefaultAgentRegistryTests.cs`
- `ChannelAdapterTests.cs` → `SessionsControllerTests.cs`
- `IsolationStrategyTests.cs` → `InMemoryActivityBroadcasterTests.cs`
- `RestApiTests.cs` → `AgentsControllerTests.cs`
- `WebSocketProtocolTests.cs` → `DefaultMessageRouterTests.cs`

### ⚠️ P1-04: Gateway Test Classes Missing `sealed` Modifier

**Finding:** All 6 Gateway test classes are `public class`. In AgentCore.Tests and Providers.Core.Tests, several test classes use `public sealed class`:

| Project | sealed test classes |
|---------|-------------------|
| AgentCore.Tests | `HookExceptionSafetyTests`, `ListenerExceptionSafetyTests`, `RetryDelayCapTests` |
| Providers.Core.Tests | `BuiltInModelsTests`, `RegistryWaveOneTests` |
| Gateway.Tests | **None** |

**Assessment:** The codebase is inconsistent on this — it's not a hard rule. However, `sealed` on test classes is a best practice (prevents accidental inheritance, allows compiler optimizations). Gateway should align with the newer pattern.

**Suggested Fix:** Add `sealed` modifier to all Gateway test classes for consistency with the more recent test additions.

### ⚠️ P2-05: Gateway Tests Don't Use IDisposable Cleanup Pattern

**Finding:** `Providers.Core.Tests` uses `IDisposable` for test cleanup (e.g., `LlmClientTests : IDisposable`, `ApiProviderRegistryTests : IDisposable`). Gateway tests have no cleanup — they construct fresh objects per test.

**Assessment:** Not an issue — Gateway tests don't use shared state that needs cleanup. Correct pattern for the use case.

### ⚠️ P2-06: Gateway Tests Lack [Theory] Parameterized Tests

**Finding:** AgentCore.Tests and Providers.Core.Tests use `[Theory]/[InlineData]` for parameterized testing. Gateway.Tests uses only `[Fact]`.

**Assessment:** Minor. Gateway tests are currently simple enough that `[Fact]` suffices. As tests grow (e.g., routing with multiple scenarios), `[Theory]` should be adopted.

---

## 6. Additional Findings

### ⚠️ P2-07: XML Doc Coverage Gap in API Controllers

**Finding:** API controllers have method-level `/// <summary>` but lack `/// <param>` documentation:

| File | Missing |
|------|---------|
| `Controllers/AgentsController.cs` | No `<param>` tags on any method |
| `Controllers/SessionsController.cs` | No `<param>` tags on any method |
| `Controllers/ChatController.cs` | No `<param>` tags on `Send` method |

**Assessment:** All other Gateway public APIs have full XML docs including parameters. Controllers are a reasonable exception (ASP.NET conventions), but this breaks the otherwise 100% doc coverage.

**Suggested Fix:** Add `<param>` tags to controller methods for consistency, or document the convention that controllers use lighter docs.

---

## Issue Summary

| ID | Severity | Category | Description |
|----|----------|----------|-------------|
| P1-01 | P1 | Naming | CancellationToken `ct` vs `cancellationToken` split |
| P1-02 | P1 | Pattern | ConfigureAwait(false) never used in Gateway vs always in AgentCore |
| P1-03 | P1 | Tests | Test file names don't match test class names (5 files) |
| P1-04 | P1 | Tests | Gateway test classes missing `sealed` modifier |
| P2-01 | P2 | Pattern | Mixed Dictionary init style (language constraint — informational) |
| P2-02 | P2 | Structure | .csproj property order inconsistency (pre-existing) |
| P2-03 | P2 | Structure | AgentCore missing RootNamespace (pre-existing) |
| P2-04 | P2 | Archive | SessionEntry.Role string vs enum deserves doc note |
| P2-05 | P2 | Tests | No IDisposable cleanup (correct for current tests) |
| P2-06 | P2 | Tests | No [Theory] usage yet (acceptable for current scope) |
| P2-07 | P2 | Docs | Controller XML docs missing `<param>` tags |

**Verdict:** Gateway passes consistency review. P1 items should be addressed before the next milestone; P2 items are housekeeping. The overall code quality is high.
