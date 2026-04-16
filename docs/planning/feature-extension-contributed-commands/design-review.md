# Extension-Contributed Commands — Design Review

**Reviewer:** Leela (Lead/Architect)  
**Date:** 2026-04-15  
**Spec:** `docs/planning/feature-extension-contributed-commands/design-spec.md`  
**Status:** Approved with modifications

---

## 1. Spec Assessment — Grade: B+

**Strengths:**
- Problem statement is clear and well-motivated. The OCP/Extension-First violations are real and documented with actual code.
- Architecture diagram is correct: backend-driven command registry with client discovery is the right pattern.
- The Skills `/skills` command is fully specified with sub-commands, output format, and session-awareness — excellent reference implementation detail.
- Phased delivery is well-structured. The Phase 1-3 core path is sound.
- Considerations section anticipates real issues (client-side vs backend, collision handling, streaming deferral).

**Gaps and Issues:**

### G1: Wrong namespace for contracts (MUST FIX)
The spec proposes `BotNexus.Gateway.Abstractions.Extensions` for `ICommandContributor`. This is the correct namespace pattern (matches `IExtensionLoader`, `ExtensionManifest`, etc. in Gateway.Contracts project), but the spec later references "Gateway.Contracts" as the project. These are the same — the project is `BotNexus.Gateway.Contracts` but uses `BotNexus.Gateway.Abstractions.*` namespaces. Spec should be explicit: **project** = `BotNexus.Gateway.Contracts`, **namespace** = `BotNexus.Gateway.Abstractions.Extensions`.

### G2: `Func<>` delegate in record is an anti-pattern (MUST FIX)
`CommandDefinition.ExecuteAsync` is a `Func<CommandExecutionContext, CancellationToken, Task<CommandResult>>`. This:
- Breaks serialization (records with `Func<>` cannot be serialized for the API response)
- Mixes definition (data) with execution (behavior)
- Makes `GET /api/commands` awkward — the delegate must be stripped before returning

**Fix:** Split into `CommandDescriptor` (pure data for API/palette) and `ICommandHandler` (execution). Or keep `CommandDefinition` as internal registry state and return a DTO for the API.

### G3: Extension loader integration not specified (MUST FIX)
The spec says "Registered at startup via extension loader" but never explains HOW. Currently `AssemblyLoadContextExtensionLoader.DiscoverableServiceContracts` contains a fixed array of types. `ICommandContributor` must be added to this array. The spec must specify this explicitly.

### G4: `ResolveSessionTool` coupling concern (SHOULD FIX)
`CommandExecutionContext.ResolveSessionTool` introduces a `Func<string, IAgentTool?>` that tightly couples the command system to the tool registry. This is adequate for Phase 1 but should be abstracted. For now, accept it — but the property should be `IServiceProvider?` or a narrower `ICommandServiceLocator` interface to avoid leaking tool internals into every command contributor.

### G5: No authorization model (SHOULD FIX)
The spec has no concept of command permissions. Can any WebUI user execute `/skills add`? What about admin-only commands? Phase 1 can defer this, but the `CommandDefinition` should have a `RequiresAuth` or `MinimumRole` field as a placeholder.

### G6: No error handling contract for `POST /api/commands/execute` (SHOULD FIX)
What HTTP status codes? What happens if the command name is unknown? What if the extension throws? The spec says "return error result, don't crash" but doesn't define the HTTP response shape for failures. Need: 200 with `isError: true` for command-level errors, 404 for unknown commands, 400 for malformed input.

### G7: `/new` command backend behavior underspecified
The spec says `/new` "seals the current session and creates a fresh one" but doesn't specify: which API endpoint does this call? Is it the existing session lifecycle or a new command handler? This needs clarity — `/new` should be a built-in command handler that calls into `ISessionStore` / session lifecycle, not a separate API path.

### G8: SkillTool instance access model unclear
The spec says the `/skills` command needs "a reference to the active SkillTool instance for the session" but the SkillTool is per-agent-session and owned by the isolation strategy. The `ResolveSessionTool` approach works only if the CommandsController has access to the active agent handle's tool registry. This requires the `POST /api/commands/execute` to resolve the current agent handle from the session.

---

## 2. Architectural Decisions

### AD1: Contract Split — CommandDescriptor + ICommandHandler

```
Project:   BotNexus.Gateway.Contracts
Namespace: BotNexus.Gateway.Abstractions.Extensions
Files:     ICommandContributor.cs, CommandModels.cs
```

```csharp
// CommandModels.cs
namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Describes a command for API/palette (pure data, serializable).
/// </summary>
public sealed record CommandDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? Category { get; init; }
    public bool ClientSideOnly { get; init; }
    public IReadOnlyList<SubCommandDescriptor>? SubCommands { get; init; }
}

public sealed record SubCommandDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<CommandArgumentDescriptor>? Arguments { get; init; }
}

public sealed record CommandArgumentDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool Required { get; init; }
}

/// <summary>
/// Context passed to command execution.
/// </summary>
public sealed record CommandExecutionContext
{
    public required string RawInput { get; init; }
    public string? SubCommand { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }
    public required string HomeDirectory { get; init; }
    /// <summary>Resolves a tool from the active agent session. Null if no session active.</summary>
    public Func<string, IAgentTool?>? ResolveSessionTool { get; init; }
}

/// <summary>
/// Result of command execution.
/// </summary>
public sealed record CommandResult
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public bool IsError { get; init; }
}
```

```csharp
// ICommandContributor.cs
namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Implemented by extensions that contribute user-facing slash commands.
/// </summary>
public interface ICommandContributor
{
    IReadOnlyList<CommandDescriptor> GetCommands();
    Task<CommandResult> ExecuteAsync(
        string commandName,
        CommandExecutionContext context,
        CancellationToken cancellationToken = default);
}
```

**Rationale:** Separating descriptor from handler means `GET /api/commands` returns clean DTOs. The `ExecuteAsync` method takes `commandName` so a single contributor can handle multiple commands (e.g., Skills contributor handles `/skills`).

### AD2: Extension Loader Registration

Add `ICommandContributor` to `AssemblyLoadContextExtensionLoader.DiscoverableServiceContracts`:

```csharp
// AssemblyLoadContextExtensionLoader.cs line ~28
private static readonly Type[] DiscoverableServiceContracts =
[
    typeof(IChannelAdapter),
    typeof(IIsolationStrategy),
    typeof(ISessionStore),
    typeof(IGatewayAuthHandler),
    typeof(IMessageRouter),
    typeof(IAgentRegistry),
    typeof(IAgentSupervisor),
    typeof(IAgentCommunicator),
    typeof(IActivityBroadcaster),
    typeof(IAgentTool),
    typeof(ICommandContributor)   // ← NEW
];
```

### AD3: CommandRegistry Location and Pattern

```
Project:   BotNexus.Gateway
Namespace: BotNexus.Gateway.Commands
File:      CommandRegistry.cs
```

`CommandRegistry` is an internal Gateway service (not in Contracts). It:
1. Collects built-in commands (registered in DI)
2. Collects extension-contributed commands (via `IEnumerable<ICommandContributor>` from DI)
3. Provides `GetAll()` → `IReadOnlyList<CommandDescriptor>` for the API
4. Provides `ExecuteAsync(string input, CommandExecutionContext ctx)` for dispatch
5. Handles name collision (first-registered wins, log warning for duplicates)

### AD4: Built-In Commands as ICommandContributor

Built-in commands (`/help`, `/status`, `/agents`, `/new`) should also implement `ICommandContributor` — not be a separate code path. This proves the extension model works for the platform's own commands.

```
File: BotNexus.Gateway/Commands/BuiltInCommandContributor.cs
```

**Exception:** `/reset` remains client-side only. It's in the palette (via `GET /api/commands` with `clientSideOnly: true`) but never hits the backend.

### AD5: API Endpoints in Existing Controller Pattern

```
Project:   BotNexus.Gateway.Api
File:      Controllers/CommandsController.cs
Namespace: BotNexus.Gateway.Api.Controllers
```

```
GET  /api/commands                    → 200 OK: CommandDescriptor[]
POST /api/commands/execute            → 200 OK: CommandResult
                                        400 Bad Request: missing/malformed input
                                        404 Not Found: unknown command
```

### AD6: WebUI Dynamic Command Loading

Replace `const COMMANDS = [...]` with a fetch from `GET /api/commands` on startup. Cache the result. Refresh on reconnect. Commands with `clientSideOnly: true` execute locally; all others POST to `/api/commands/execute`.

### AD7: SkillTool Exposure for Session-Aware Commands

Add three members to `SkillTool`:
- `TryUnload(string skillName) → bool`
- `DiscoveryPaths` property (tuple of nullable strings)
- `Config` property (expose the SkillsConfig)

These are narrow, read/write surface additions — not new interfaces. Acceptable for Phase 1.

### AD8: Session Tool Resolution

The `POST /api/commands/execute` endpoint must resolve the active agent handle for the given `sessionId` + `agentId`, then extract the tool registry to populate `CommandExecutionContext.ResolveSessionTool`. This flows through `IAgentSupervisor.GetHandle()` → `IAgentHandle` → tool access.

The `IAgentHandle` (or `IAgentHandleInspector`) must expose a tool resolution method. If it doesn't exist, add `IAgentTool? ResolveTool(string toolName)` to `IAgentHandleInspector`.

---

## 3. Risk Analysis

### R1: Session Tool Resolution (HIGH)
**Risk:** The CommandsController needs access to the live SkillTool instance in the active agent session. Currently, tools are resolved during agent creation and held inside the isolation strategy. There's no public API to query an active handle's tools by name.  
**Mitigation:** Add `ResolveTool(string)` to `IAgentHandleInspector`. Keep it narrow — returns `IAgentTool?` by name. This is the minimum surface area needed.  
**Owner:** Farnsworth

### R2: Extension Command Registration Timing (MEDIUM)
**Risk:** Extensions are loaded during startup, but the extension loader injects into `IServiceCollection`, which is frozen after `Build()`. If `ICommandContributor` implementations need runtime DI (e.g., `IAgentRegistry`), they must be resolved from the built `IServiceProvider`, not from `IServiceCollection`.  
**Mitigation:** `CommandRegistry` resolves `IEnumerable<ICommandContributor>` from the service provider at runtime, not at registration time. The extension loader registers the type; the registry resolves instances lazily.  
**Owner:** Farnsworth

### R3: WebUI Race Condition on Startup (LOW)
**Risk:** WebUI fetches `/api/commands` before extensions finish loading. Gets partial command list.  
**Mitigation:** Gateway returns whatever is registered at query time. WebUI re-fetches on SignalR reconnect. Acceptable for Phase 1. Phase 5 (hub) enables push-based updates.  
**Owner:** Fry

### R4: Command Name Collision Across Extensions (LOW)
**Risk:** Two extensions register `/foo`.  
**Mitigation:** First-registered wins. Log warning. Same pattern as tool name collisions in the existing tool registry.  
**Owner:** Farnsworth

### R5: SkillTool State Mutation Thread Safety (MEDIUM)
**Risk:** `/skills add` and `/skills remove` mutate `_sessionLoaded` (ConcurrentDictionary) which is also read by `SkillPromptHookHandler` during prompt build. The ConcurrentDictionary handles thread safety for individual operations, but the add/remove + resolve sequence is not atomic.  
**Mitigation:** ConcurrentDictionary is sufficient. The `TryAdd`/`TryRemove` operations are individually atomic. The worst case is a skill appearing/disappearing between a list and a load — user retries. No data corruption risk.  
**Owner:** Bender

### R6: Client-Side Command Enumeration (LOW)
**Risk:** `/help` currently enumerates the hardcoded `COMMANDS` array. After migration, it must enumerate from the fetched command list. If the fetch fails, `/help` shows nothing.  
**Mitigation:** Keep a fallback minimum command set in the client. If fetch fails, show at least `/help`, `/new`, `/reset`.  
**Owner:** Fry

---

## 4. Wave Breakdown

### Wave 1: Contracts + Registry (Foundation)
**Goal:** Define the extension point and build the command registry. No clients, no extensions yet — just the infrastructure.

| Agent | Deliverable | Files |
|-------|-------------|-------|
| **Farnsworth** | `ICommandContributor`, `CommandDescriptor`, `CommandResult`, `CommandExecutionContext` contracts | `src/gateway/BotNexus.Gateway.Contracts/Extensions/ICommandContributor.cs`, `src/gateway/BotNexus.Gateway.Contracts/Extensions/CommandModels.cs` |
| **Farnsworth** | `CommandRegistry` service | `src/gateway/BotNexus.Gateway/Commands/CommandRegistry.cs` |
| **Farnsworth** | Update `DiscoverableServiceContracts` to include `ICommandContributor` | `src/gateway/BotNexus.Gateway/Extensions/AssemblyLoadContextExtensionLoader.cs` |
| **Farnsworth** | DI registration in `GatewayServiceCollectionExtensions` | `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs` |
| **Hermes** | Unit tests: CommandRegistry (registration, collision, dispatch, unknown command) | `tests/BotNexus.Gateway.Tests/Commands/CommandRegistryTests.cs` |
| **Kif** | API docs for `GET /api/commands`, `POST /api/commands/execute` | `docs/api-reference.md` (update) |

**Dependencies:** None — this is the foundation.  
**Parallel:** Hermes writes test stubs and structure while Farnsworth implements. Kif documents the API contract.

### Wave 2: Built-In Commands + API (Backend Complete)
**Goal:** Move existing hardcoded commands to the registry, expose via REST.

| Agent | Deliverable | Files |
|-------|-------------|-------|
| **Farnsworth** | `BuiltInCommandContributor` — `/help`, `/status`, `/agents`, `/new`, `/reset` (descriptor only, `clientSideOnly: true`) | `src/gateway/BotNexus.Gateway/Commands/BuiltInCommandContributor.cs` |
| **Bender** | `CommandsController` — `GET /api/commands`, `POST /api/commands/execute` | `src/gateway/BotNexus.Gateway.Api/Controllers/CommandsController.cs` |
| **Bender** | Add `ResolveTool` to `IAgentHandleInspector` for session tool resolution | `src/gateway/BotNexus.Gateway.Contracts/Agents/IAgentHandleInspector.cs`, `src/gateway/BotNexus.Gateway/Isolation/InProcessIsolationStrategy.cs` |
| **Hermes** | Integration tests: `GET /api/commands` returns built-in list, `POST /api/commands/execute` dispatches correctly, 404 on unknown, 400 on malformed | `tests/BotNexus.Integration.Tests/Commands/CommandsApiTests.cs` |
| **Kif** | Update extension docs: `"command"` extension type in manifest reference | `docs/extensions.md` (update) |

**Dependencies:** Wave 1  
**Parallel:** Bender builds controller while Farnsworth finishes built-in contributor. Hermes writes integration test scaffolding targeting the API contract.

### Wave 3: WebUI Integration (Client Dynamic)
**Goal:** Replace hardcoded command palette with dynamic API-driven commands.

| Agent | Deliverable | Files |
|-------|-------------|-------|
| **Fry** | Replace `const COMMANDS` with `GET /api/commands` fetch on startup and reconnect | `src/BotNexus.WebUI/wwwroot/js/chat.js` |
| **Fry** | Route non-client-side commands to `POST /api/commands/execute` | `src/BotNexus.WebUI/wwwroot/js/chat.js` |
| **Fry** | Add `getCommands()` and `executeCommand()` API functions | `src/BotNexus.WebUI/wwwroot/js/api.js` |
| **Fry** | Sub-command palette UX — typing `/skills ` shows sub-commands from descriptor | `src/BotNexus.WebUI/wwwroot/js/chat.js` |
| **Hermes** | E2E test: command palette shows dynamic commands, execution works, sub-commands display | Manual test script or Playwright test |

**Dependencies:** Wave 2  
**Parallel:** Fry can start on the client-side refactor as soon as the API contract is locked (Wave 1). Full integration testing waits for Wave 2.

### Wave 4: Skills Extension `/skills` Command (First Extension Consumer)
**Goal:** Implement the reference extension-contributed command.

| Agent | Deliverable | Files |
|-------|-------------|-------|
| **Bender** | `SkillsCommandContributor : ICommandContributor` with all 5 sub-commands | `extensions/skills/BotNexus.Extensions.Skills/SkillsCommandContributor.cs` |
| **Bender** | Add `TryUnload()`, `DiscoveryPaths`, `Config` to `SkillTool` | `extensions/skills/BotNexus.Extensions.Skills/SkillTool.cs` |
| **Bender** | Add `"command"` to extension manifest | `extensions/skills/BotNexus.Extensions.Skills/botnexus-extension.json` |
| **Hermes** | Unit tests: SkillsCommandContributor (list, info, add, remove, reload), TryUnload | `tests/BotNexus.Skills.Tests/SkillsCommandContributorTests.cs`, `tests/BotNexus.Skills.Tests/SkillToolTests.cs` (update) |
| **Hermes** | Integration tests: `/skills list` via API with loaded extension, `/skills add` + `/skills remove` round-trip | `tests/BotNexus.Integration.Tests/Commands/SkillsCommandTests.cs` |
| **Kif** | User-facing docs: `/skills` command reference with examples | `docs/skills-commands.md` (new) |

**Dependencies:** Wave 2 (needs `ICommandContributor` + `CommandsController` + `ResolveTool`)  
**Parallel:** Bender can start `SkillsCommandContributor` once Wave 1 contracts are merged. Full wiring waits for Wave 2.

---

## 5. Scope Decisions

### In Scope (This Delivery)
- **Phase 1:** Backend command registry (`ICommandContributor`, `CommandRegistry`, `CommandsController`)
- **Phase 2:** WebUI dynamic command palette (replace hardcoded commands)
- **Phase 3:** Skills extension `/skills` command (reference implementation)
- Built-in commands migrated to registry (`/help`, `/status`, `/agents`, `/new`)
- `/reset` stays client-side with `clientSideOnly: true` descriptor

### Deferred
- **Phase 4: TUI Integration** — `InteractiveLoop.cs` command refactor. The TUI works standalone without the Gateway, so it cannot use the Gateway's `CommandRegistry` directly. This needs a shared command abstraction or the TUI calling Gateway APIs. Defer to a separate feature.
- **Phase 5: Hub Integration** — SignalR push-based command updates. Nice optimization, not blocking. Defer until we see real latency issues with REST polling.
- **Command permissions/authorization** — No role-based command access in Phase 1. All commands are available to all users.
- **Streaming command results** — Single `CommandResult` response. No streaming for long-running commands.
- **MCP extension `/mcp` command** — Second consumer. Implement after Skills proves the pattern.

### Test Estimates
- Wave 1: ~8-12 unit tests (registry behavior)
- Wave 2: ~6-10 integration tests (API endpoints)
- Wave 3: ~3-5 E2E tests (WebUI behavior)
- Wave 4: ~15-20 unit + integration tests (Skills command + SkillTool changes)
- **Total: ~32-47 new tests**

---

## 6. Open Items for Jon

1. **AD2 confirmation:** Adding `ICommandContributor` to `DiscoverableServiceContracts` — this is the same auto-registration pattern used for `IAgentTool`. Extension assemblies that implement `ICommandContributor` will be registered in DI automatically. Confirm this is acceptable.

2. **AD8 confirmation:** Adding `ResolveTool(string)` to `IAgentHandleInspector` — this exposes per-session tool resolution to the API layer. It's a narrow addition but crosses the handle boundary. Confirm or suggest alternative.

3. **Scope confirmation:** Phases 1-3 + Skills command (Phase 3) in this delivery. TUI (Phase 4) and Hub (Phase 5) deferred. Confirm.

---

*Review complete. All waves can begin upon approval. Wave 1 has no dependencies and can start immediately.*
