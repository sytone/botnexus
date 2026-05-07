### Conversation Project Extraction — Architectural Design Review

**Decision Date:** 2026-05-06  
**Decided By:** Leela (Lead/Architect)  
**Status:** Approved — ready for implementation  

---

## Context

User directive: conversation stores and related code currently live in `BotNexus.Gateway.Sessions` alongside session stores. They should be extracted to a dedicated `BotNexus.Gateway.Conversations` project to improve separation of concerns and make conversation lifecycle independently testable.

## Current Layout

| Layer | Where conversations live today | Namespace |
|-------|-------------------------------|-----------|
| **Domain model** | `src/domain/BotNexus.Domain/Gateway/Models/Conversation.cs`, `ConversationEnums.cs`, `ConversationId.cs` | `BotNexus.Domain.Primitives`, `BotNexus.Gateway.Abstractions.Models` |
| **Contracts** | `src/gateway/BotNexus.Gateway.Contracts/Conversations/` — `IConversationStore`, `IConversationRouter`, `ConversationSummary` | `BotNexus.Gateway.Abstractions.Conversations` |
| **Store implementations** | `src/gateway/BotNexus.Gateway.Sessions/` — `InMemoryConversationStore`, `FileConversationStore`, `SqliteConversationStore` | `BotNexus.Gateway.Sessions` |
| **Router** | `src/gateway/BotNexus.Gateway/Conversations/DefaultConversationRouter.cs` | `BotNexus.Gateway.Conversations` |
| **API** | `src/gateway/BotNexus.Gateway.Api/Controllers/ConversationsController.cs`, `ConversationDtos.cs` | `BotNexus.Gateway.Api.Controllers` |
| **DI wiring** | `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs` — `ConfigureConversationStore()` + default registrations | `BotNexus.Gateway.Extensions` |

**Tests:**
- `tests/BotNexus.Gateway.Tests/InMemoryConversationStoreTests.cs`
- `tests/BotNexus.Gateway.Tests/SqliteConversationStoreTests.cs`
- `tests/BotNexus.Gateway.Tests/Conversations/` — 5 routing/binding test files
- `tests/BotNexus.Gateway.ConversationTests/` — 1 routing scenario file (references Gateway + Sessions)
- `tests/BotNexus.ConversationTests/` — Integration/E2E tests (REST + SignalR, live gateway fixture)

---

## Decision: Target Project Structure

### 1. What moves to `src/gateway/BotNexus.Gateway.Conversations`

| File | Current location | Notes |
|------|-----------------|-------|
| `InMemoryConversationStore.cs` | Gateway.Sessions | Pure conversation logic, no session coupling |
| `FileConversationStore.cs` | Gateway.Sessions | Pure conversation logic, uses IFileSystem |
| `SqliteConversationStore.cs` | Gateway.Sessions | Pure conversation logic, own SQLite schema |
| `DefaultConversationRouter.cs` | Gateway/Conversations/ | Already in `BotNexus.Gateway.Conversations` namespace |

**New project namespace:** `BotNexus.Gateway.Conversations`

**New project references:**
```
BotNexus.Gateway.Conversations → BotNexus.Gateway.Contracts  (for IConversationStore, IConversationRouter, ISessionStore)
BotNexus.Gateway.Conversations → BotNexus.Domain             (for primitives)
```

**Package references (moved from Gateway.Sessions):**
- `Microsoft.Data.Sqlite` — needed by SqliteConversationStore
- `Microsoft.Extensions.Logging.Abstractions` — needed by all stores
- `TestableIO.System.IO.Abstractions.Wrappers` — needed by FileConversationStore

### 2. What stays where it is

| Item | Location | Reason |
|------|----------|--------|
| `IConversationStore`, `IConversationRouter`, `ConversationSummary` | **Gateway.Contracts** | Contracts live at the abstraction layer — correct placement. Moving them down into the implementation project would invert the dependency. |
| `Conversation`, `ConversationStatus`, `BindingMode`, `ThreadingMode`, `ChannelBinding` | **Domain** | Domain models belong in Domain. Not conversation-store concerns. |
| `ConversationId` | **Domain.Primitives** | Value-type primitive, shared across the stack. |
| `ConversationsController`, `ConversationDtos` | **Gateway.Api** | API surface stays in the API project. References `IConversationStore` via Contracts. No change needed. |
| `ConfigureConversationStore()` in `GatewayServiceCollectionExtensions` | **Gateway** | DI composition root stays in the Gateway host. It will need a new `using` for the new namespace + a new `<ProjectReference>` to `BotNexus.Gateway.Conversations`. |
| `SessionStoreBase`, `InMemorySessionStore`, `FileSessionStore`, `SqliteSessionStore`, `SessionCompaction`, etc. | **Gateway.Sessions** | Pure session concerns, remain as-is. |

### 3. What moves to `tests/BotNexus.Gateway.Conversations.Tests`

| Test file | Current location | Reason to move |
|-----------|-----------------|----------------|
| `InMemoryConversationStoreTests.cs` | Gateway.Tests | Unit tests for moved class |
| `SqliteConversationStoreTests.cs` | Gateway.Tests | Unit tests for moved class |
| `Conversations/DefaultConversationRouterTests.cs` | Gateway.Tests | Unit tests for moved class |
| `Conversations/FanOutBindingAwareTests.cs` | Gateway.Tests | Tests conversation binding fan-out |
| `Conversations/MultiChannelFanOutTests.cs` | Gateway.Tests | Tests conversation multi-channel |
| `Conversations/ThreadIdRoutingTests.cs` | Gateway.Tests | Tests conversation thread routing |
| `Conversations/GatewayHostBindingRoutingTests.cs` | Gateway.Tests | Tests conversation routing through host — **review at implementation time** whether this is integration-level or can move |

**Tests that stay:**
| Test location | Reason |
|---------------|--------|
| `BotNexus.Gateway.ConversationTests/` | Already a separate project; tests conversation routing scenarios using the full Gateway. Keep separate as integration tests. |
| `BotNexus.ConversationTests/` | E2E tests with live gateway fixture (REST + SignalR). Not unit tests. |
| `BotNexus.Gateway.Tests/FanOutStaleBindingTests.cs` | Tests GatewayHost stale-binding recovery, not conversation store logic. |
| `BotNexus.Gateway.Tests/GatewayHostTests.cs` | Tests the host, references conversations peripherally. |

### 4. Project Reference Changes

**Gateway.Sessions (loses conversation code, gets simpler):**
- Remove: `Microsoft.Data.Sqlite` stays only if `SqliteSessionStore` needs it (it does — keep it)
- No new references needed

**BotNexus.Gateway (composition root):**
- Add: `<ProjectReference>` to `BotNexus.Gateway.Conversations`
- Add `using BotNexus.Gateway.Conversations;` in `GatewayServiceCollectionExtensions.cs`
- Remove: `using BotNexus.Gateway.Sessions;` for conversation store types

**BotNexus.Gateway.Conversations.Tests:**
- References: `BotNexus.Gateway.Conversations`, `BotNexus.Gateway.Contracts`, `BotNexus.Domain`
- Test-infra: `xunit`, `Shouldly`, `Moq`, `Microsoft.NET.Test.Sdk`, `Microsoft.Data.Sqlite` (for SQLite store tests), `TestableIO.System.IO.Abstractions.TestingHelpers`

**Dependency graph after refactor:**
```
Domain ← Contracts ← Gateway.Conversations ← Gateway (host)
                    ↖ Gateway.Sessions       ↗
                    ↖ Gateway.Api            ↗
```

No circular dependencies. `Gateway.Conversations` and `Gateway.Sessions` are siblings — neither references the other.

### 5. Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| **SQLite coupling: `SqliteSessionStore` takes `IConversationStore?`** for orphan migration | MEDIUM | The constructor takes the interface, not the concrete class. The DI container resolves it. No compile-time coupling to the conversations project. **Safe.** |
| **Shared SQLite database file (`sessions.sqlite`)** for both session and conversation stores | MEDIUM | `SqliteConversationStore` and `SqliteSessionStore` share the same connection string but have independent tables. Each manages its own schema. This is a runtime config concern, not a project-structure concern. No code change needed. |
| **DI registration in `GatewayServiceCollectionExtensions.cs` uses concrete types** (`InMemoryConversationStore`, `FileConversationStore`, `SqliteConversationStore`) | LOW | The composition root (Gateway project) already references Gateway.Sessions for session stores. It will now also reference Gateway.Conversations for conversation stores. This is normal — DI registration is a composition concern. |
| **Namespace break: store classes move from `BotNexus.Gateway.Sessions` to `BotNexus.Gateway.Conversations`** | LOW | All consumers reference `IConversationStore` (in Contracts namespace) via DI. Only `GatewayServiceCollectionExtensions.cs` references concrete types — single file to update. |
| **`DefaultConversationRouter` depends on `ISessionStore`** | LOW | It takes the interface from Contracts. The new project references Contracts, which defines `ISessionStore`. No coupling to Gateway.Sessions project. |
| **Test helper/fixture sharing** | LOW | If `SqliteConversationStoreTests` has test helpers shared with session store tests, extract to a shared test-infra project or duplicate. Check at implementation time. |

### 6. Recommended Commit Staging

| Commit | Scope | Description |
|--------|-------|-------------|
| 1 | `chore(conversations)` | Create `BotNexus.Gateway.Conversations` project with csproj, add to solution |
| 2 | `refactor(conversations)` | Move `InMemoryConversationStore`, `FileConversationStore`, `SqliteConversationStore` from Gateway.Sessions → Gateway.Conversations. Update namespaces. |
| 3 | `refactor(conversations)` | Move `DefaultConversationRouter` from Gateway → Gateway.Conversations (namespace already matches). Update Gateway csproj references. |
| 4 | `refactor(gateway)` | Update `GatewayServiceCollectionExtensions.cs` usings and Gateway.csproj to reference the new project. Remove Gateway.Sessions reference from Gateway.csproj if no longer needed (unlikely — session stores still there). |
| 5 | `test(conversations)` | Create `BotNexus.Gateway.Conversations.Tests` project. Move conversation store + router tests. |
| 6 | `test(conversations)` | Verify full test suite passes. No test deletions — all tests migrate. |

**Each commit must build green and pass all tests.** Commits 2 and 3 can potentially be combined if the changeset is small enough to review atomically.

---

## Verification Checklist (for implementing agent)

- [ ] `dotnet build BotNexus.slnx --nologo --tl:off` — zero errors
- [ ] `dotnet test BotNexus.slnx --nologo --tl:off` — zero failures, same test count
- [ ] No `using BotNexus.Gateway.Sessions;` in any file that only needs conversation types
- [ ] `IConversationStore` / `IConversationRouter` remain in `BotNexus.Gateway.Contracts` (not moved)
- [ ] `Conversation` model remains in `BotNexus.Domain` (not moved)
- [ ] No circular project references
- [ ] Gateway.Conversations does not reference Gateway.Sessions
- [ ] Gateway.Sessions does not reference Gateway.Conversations
