# Squad Decisions

## Active Decisions

### 2026-07-29 — Team Reskill & Context Optimization (Leela)

**Decision:** Performed team-wide reskill pass per `.squad/templates/skills/reskill/SKILL.md`.

**Changes:**
1. All 11 charters trimmed to minimal template (~1-2KB each). Removed: Collaboration (→ spawn prompt), Voice (→ tagline), verbose Model (→ single line), "When I'm unsure" boilerplate.
2. 7 oversized histories (>12KB) summarized to Core Context + Learnings format (target ≤8KB).
3. New shared skill: `.squad/skills/agent-collaboration/SKILL.md` — documents the collaboration protocol previously duplicated in every charter.

**Impact:** ~70% reduction in cold-loaded context. Agent identity, domain ownership, and unique working patterns preserved.

**Precedent:** Reskill should be re-run whenever total charter+history context exceeds ~80KB or when 3+ charters show new shared patterns worth extracting.

---

### 2026-05-11 — Fry: Cron Conversations Are Closable via the Same Archive API

**Author:** Fry (Web Dev)  
**Date:** 2026-05-08  
**Scope:** BlazorClient UI + API contract  
**Status:** Implemented

**Context:** Cron (virtual session) conversations previously had no UI affordance for cleanup — the archive button was hidden for them. Users with many cron conversations couldn't clean up the sidebar.

**Decision:**
- **Cron conversations can now be closed** using the same `DELETE /api/conversations/{id}` endpoint used for archiving regular conversations.
- The UI shows a **close button (✕)** for cron conversations with a tooltip: "Close conversation — reopens on next trigger".
- Regular conversations keep the **archive button (🗑️)** with existing semantics.
- Default conversations remain protected — no close or archive button.

**Rationale:**
- The backend already handles reopening archived default conversations (`GetOrCreateDefaultAsync` checks for archived defaults). Cron conversations follow the same reopening pattern when the next trigger fires.
- No backend changes required — the existing archive/close semantics work for both conversation types.
- Differentiating the label (close vs archive) sets correct user expectations about permanence.

**Impact:**
- **Bender:** No backend changes needed. The `DELETE` endpoint works for cron conversations already.
- **Hermes:** Old test `Virtual_cron_conversation_shows_badge_and_hides_archive_button` replaced with new tests verifying the close button is shown.

---

### 2026-05-11 — Bender: Conversation cleanup uses close+reopen lifecycle

**Author:** Bender (Runtime Dev)  
**Date:** 2026-05-11  
**Scope:** Gateway conversations, cron trigger continuity, multi-channel bindings  
**Status:** Implemented

**Decision:**
DELETE /api/conversations/{id} remains a soft-delete/archive operation and is treated as **close conversation** semantics. Closing now clears ActiveSessionId so subsequent activity creates a fresh session. Archived conversations may be reopened automatically when inbound activity matches an existing binding or the conversation is explicitly addressed by ID.

**Rationale:**
Hard-deleting conversations breaks cron and bound-channel continuity. Reopening archived conversations preserves existing bindings, so a cleaned-up conversation can naturally reappear across attached channels when activity resumes.

**Runtime Effects:**
- Archived conversation is hidden from active summaries.
- Close operation clears active session linkage.
- First inbound message after close reactivates the archived conversation and creates/rebinds a fresh active session.

---

### 2026-05-11 — Hermes: Conversation Cleanup Test Strategy

**Author:** Hermes (Tester)  
**Date:** 2026-05-11  
**Status:** Implemented

**Decision:** Treat cron virtual conversation cleanup as **session lifecycle** (DELETE /api/sessions/{id}) instead of conversation archive.

**Why:** Cron rows in the Blazor sidebar are virtual projections from session summaries; archiving by conversation ID cannot remove them reliably.

**Test Impact:**
- Blazor client tests verify cleanup affordance remains available for cron rows and routes through session deletion.
- Gateway tests verify archiving a conversation closes its active session and clears active session linkage.
- Existing router tests continue to validate archived conversations reopen on subsequent inbound activity with bindings preserved.

---

### 2026-05-06T18:12:51-07:00: User Directive — Conversation Project Extraction

**By:** Sytone (via Copilot)  
**What:** Conversation stores and code related to conversations should be in the gateway conversation namespace. Keep this clean and better abstracted by creating `src\gateway\BotNexus.Gateway.Conversations` and moving conversation store/object tests into `tests\BotNexus.Gateway.Conversations.Tests`.  
**Why:** User request — captured for team memory

---

### Conversation Project Extraction — Architectural Design Review

**Decision Date:** 2026-05-06  
**Decided By:** Leela (Lead/Architect)  
**Status:** Approved

**Context:** User directive: conversation stores and related code currently live in `BotNexus.Gateway.Sessions` alongside session stores. They should be extracted to a dedicated `BotNexus.Gateway.Conversations` project to improve separation of concerns and make conversation lifecycle independently testable.

**Decision:** Extract 3 conversation stores (InMemory, File, Sqlite) + DefaultConversationRouter from Gateway.Sessions/Gateway into new BotNexus.Gateway.Conversations. Keep contracts in Gateway.Contracts, domain models in Domain. Dependency direction preserved.

**Key points:**
- **New project:** `BotNexus.Gateway.Conversations` with namespace `BotNexus.Gateway.Conversations`
- **What moves:** InMemoryConversationStore, FileConversationStore, SqliteConversationStore, DefaultConversationRouter
- **What stays:** IConversationStore/IConversationRouter (in Contracts), Domain models, API layer, DI root
- **Project references:** Gateway.Conversations → Gateway.Contracts, Domain (no circular deps with Gateway.Sessions)
- **Tests:** 7 conversation-focused tests move from Gateway.Tests → new test project

**Rationale:**
1. Separation of concerns — conversation lifecycle independent of session stores
2. Dependency inversion — contracts stay at abstraction layer
3. Clear ownership — conversations project owns its stores and router
4. Bounded risk — no architectural changes, single-file DI update

**Risks (all mitigated):**
| Risk | Severity | Mitigation |
|------|----------|------------|
| SQLite coupling in SqliteSessionStore | MEDIUM | Takes IConversationStore interface; DI container resolves it. No compile-time coupling. |
| Shared SQLite database file | MEDIUM | Independent tables and schemas. Runtime config concern, no code change. |
| DI registration uses concrete types | LOW | Composition root already references Gateway.Sessions. Normal pattern. |
| Namespace change | LOW | All consumers reference interface via Contracts/DI. Single file update. |
| DefaultConversationRouter depends on ISessionStore | LOW | Takes interface from Contracts. No project coupling. |
| Test helper sharing | LOW | Check at implementation; extract or duplicate if needed. |

---

### Bender Decision: Conversation Project Refactor Implementation

**Date:** 2026-05-06  
**Status:** Implemented

Implemented Leela's boundary decision by extracting conversation runtime code into `BotNexus.Gateway.Conversations` and moving conversation store/router tests into `BotNexus.Gateway.Conversations.Tests`.

**Completed:**
- Created `src\gateway\BotNexus.Gateway.Conversations` project
- Created `tests\BotNexus.Gateway.Conversations.Tests` test project
- Moved 4 runtime classes: InMemoryConversationStore, FileConversationStore, SqliteConversationStore, DefaultConversationRouter
- Moved 7 conversation-focused test files
- Updated all namespaces and project references
- Updated DI wiring in GatewayServiceCollectionExtensions
- Added shared TestOptionsMonitor to new test project via linked compile
- Full solution build succeeded
- BotNexus.Gateway.Conversations.Tests: 66/66 passing

**Key callouts:**
- GatewayServiceCollectionExtensions remains composition root with concrete registration
- BotNexus.Gateway.Tests keeps integration/API/E2E-adjacent tests
- Targeted validation passed (Leela checklist)

**PR:** https://github.com/sytone/botnexus/pull/178

---

### Leela Design Review: bug-blazor-autoscroll (2026-04-20)

**Decision Date:** 2026-04-20  
**Decided By:** Leela (Lead/Architect)  
**Status:** Delivered

**Context:** Blazor chat UI regression — messages do not auto-scroll to bottom when new content arrives. Users must manually scroll after every message. This is a regression of the previously delivered `improvement-blazor-chat-autoscroll` (Apr '26).

**Root Cause:** Race condition between scroll execution and markdown rendering in `ChatPanel.razor` `OnAfterRenderAsync`:
1. **Scroll fires first** — calls `chatScroll.forceScrollToBottom` via JS interop
2. **Markdown renders** — iterates messages, calls `BotNexus.renderMarkdown`, populates cache
3. **Re-render triggered** — calls `StateHasChanged()` on markdown change
4. **Second cycle runs** — new DOM content from markdown rendering has changed layout, making scroll threshold check fail

**Decision:** Fix render-then-scroll ordering + harden JS scroll functions:

**Contracts:**
1. **Reorder `OnAfterRenderAsync` in `ChatPanel.razor`** — markdown first, scroll last. Only scroll when `needsRender == false`. When markdown found, `StateHasChanged()` and return.
2. **Update `forceScrollToBottom` in `chat.js`** — add `setTimeout(50)` backstop after `requestAnimationFrame` to catch residual DOM changes.
3. **Update `scrollToBottom` in `chat.js`** — accept optional `isStreaming` parameter, use 200px threshold when streaming (vs 100px normally).
4. **Pass streaming state** — `ChatPanel.razor` invokes `scrollToBottom(element, State.IsStreaming)`.

**Files Modified:**
- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Components/ChatPanel.razor`
- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/wwwroot/js/chat.js`

**Wave Plan:**
- **Wave 1 (Fry):** Implementation of fix contracts. Build + 2545 tests green. ✅ Complete
- **Wave 2 (Hermes):** Manual verification of 7 spec edge cases, bUnit test for render lifecycle. ✅ Complete
- **Wave 3 (Nibbler):** Post-work consistency review (JSDoc, archived spec cleanup). ✅ Complete

**Rationale:**
1. **Root cause identified** — Race condition between scroll timing and markdown re-render, not missing JS interop
2. **Bounded fix** — 2-file change, no architectural changes, no new dependencies
3. **Low risk** — Threshold increase to 200px only during streaming; 50ms backstop below perceptual threshold; reorder doesn't introduce new race conditions (Blazor WASM is single-threaded)
4. **Verified** — All 7 spec edge cases manually tested; bUnit test added for lifecycle ordering

**Risks Mitigated:**
| Risk | Mitigation |
|------|------------|
| Reorder may cause first-render flash (raw markdown visible) | Already current behavior — markdown is rendered async today. No regression. |
| `setTimeout(50)` backstop could cause visible jump | 50ms below perceptual threshold. User won't notice. |
| Streaming threshold (200px) may scroll when user doesn't want | 200px ≈ 2-3 lines. Only during active streaming. Acceptable trade-off. |
| `StateHasChanged()` loop if markdown keeps finding new messages | Loop terminates because `_markdownCache` is populated — each message rendered once. |

---

### SessionStoreBase Status-Filter Overload (2026-04-12)

**Decision Date:** 2026-04-12  
**Decided By:** Farnsworth (Platform Dev)  
**Status:** Implemented

**Context:** New contract tests assert that session stores expose status-based filtering via a ListAsync overload while still honoring the existing ISessionStore contract.

**Decision:** Implement ListAsync(AgentId?, GatewaySessionStatus?, CancellationToken) on SessionStoreBase as a public overload (not interface change), and keep existing ISessionStore.ListAsync(AgentId?, CancellationToken) unchanged.

**Rationale:** This keeps API compatibility for interface consumers while enabling consistent status filtering behavior in all concrete stores through shared base logic.

---

### Leela Decision: AGENTS.md Memory Tool Naming & Portability (2025-05-07)

**Decision Date:** 2025-05-07  
**Decided By:** Leela (Lead/Architect)  
**Status:** Implemented

**User Directive:** Sytone requested root AGENTS.md be updated to reflect that BotNexus runs on Windows and Linux, and to clarify the confusing "memory save" vs "memory store" tool terminology.

**Context:** PR #179 established that `memory_save` is the single agent-facing tool for writing memory. The codebase also uses terms like "memory store" and "store_memory" in different contexts (Copilot CLI built-in, internal indexing). Root AGENTS.md had no memory guidance and no explicit portability statement.

**Decisions:**

1. **Agent-Facing Tool Name: `memory_save`**
   - Single canonical tool name is **`memory_save`**
   - It writes append-only daily notes to `memory/YYYY-MM-DD.md`
   - `MEMORY.md` is **read-only** during normal turns — loaded at session start for long-term context
   - Future consolidation ("dreaming") updates `MEMORY.md` from daily notes automatically
   - SQLite indexes, search state, and external `store_memory` primitives are **implementation details** — never referenced in agent-facing docs

2. **Platform Statement — Added to root AGENTS.md**
   - New "Platform / Runtime" section at top of document
   - States: "BotNexus runs on **Windows and Linux**. All guidance applies to both platforms unless explicitly noted otherwise."
   - References cross-platform path handling section

3. **Memory Tool Naming — Added to root AGENTS.md**
   - New "Memory Tool Naming" section after "Code Practices"
   - Clarifies `memory_save` appends to `memory/YYYY-MM-DD.md` (append-only)
   - `MEMORY.md` is read-only during turns
   - Consolidation updates `MEMORY.md` automatically — agents never write directly
   - Prohibits surface use of "memory store", "store_memory", SQLite in agent-facing docs
   - Distinguishes external Copilot CLI `store_memory` as separate mechanism

**Rationale:**
- **Single canonical name** eliminates "save vs store" ambiguity
- **Explicit prohibition** of "memory store" in agent docs prevents future confusion
- **Platform statement** up front anchors doc for contributors, prevents OS-specific assumptions

**Implementation:**
- **Kif** added both sections to root AGENTS.md per exact wording
- Left per-agent template `src/gateway/BotNexus.Gateway/Templates/AGENTS.md` untouched (already correct)
- Committed as 851a6509: `docs(agents): add platform statement and memory tool naming guidance`

---

### 1.1 Folder Structure Convention

```
{AppRoot}/
  extensions/
    channels/
      discord/        → BotNexus.Channels.Discord.dll + dependencies
      telegram/       → BotNexus.Channels.Telegram.dll + dependencies
      slack/          → BotNexus.Channels.Slack.dll + dependencies
    providers/
      copilot/        → BotNexus.Agent.Providers.Copilot.dll + dependencies
      openai/         → BotNexus.Agent.Providers.OpenAI.dll + dependencies
      anthropic/      → BotNexus.Agent.Providers.Anthropic.dll + dependencies
    tools/
      github/         → BotNexus.Tools.GitHub.dll + dependencies
```

Each sub-folder is a self-contained deployment unit. The `extensions/` root path is configurable via `ExtensionsPath` in `BotNexusConfig`. Default: `./extensions`.

### 1.2 Configuration Model

Current config has hard-coded typed properties. These must become **dictionary-based** so the set of extensions is driven entirely by config.

**Key changes:**
- `ProvidersConfig` becomes `Dictionary<string, ProviderConfig>` keyed by folder name
- `ProviderConfig` gains `Auth` discriminator: `"apikey"` (default) or `"oauth"`
- `ChannelsConfig` moves per-channel config to `Instances: Dictionary<string, ChannelConfig>`
- `ToolsConfig` adds `Extensions: Dictionary<string, Dictionary<string, object>>` for dynamic tools
- Root config adds `ExtensionsPath: string`

### 1.3 Discovery and Loading Process

**Core class:** `ExtensionLoader` (in `BotNexus.Core` or new `BotNexus.Extensions` project)

---

### 2026-05-14 — Gateway Extension Boundary Enforcement

**Decision Date:** 2026-05-14  
**Scope:** Gateway architecture, extension dependencies, hot-reload notifications  
**Status:** ✅ Implemented & Approved

**Context:** PR #237 introduced a gateway→extension compile dependency by directly injecting SignalR hub types into `AgentsController` for real-time agent config change broadcasts. This crossed the architectural boundary and created an invalid inversion of control.

**Decision:** Sever the compile-time gateway→extension dependency and route notifications through a neutral gateway contract.

**Implementation:**

1. **New Contract: `IAgentChangeNotifier`**
   - Location: `BotNexus.Gateway.Contracts\Agents\` (namespace: `BotNexus.Gateway.Abstractions.Agents`)
   - Single method: `Task NotifyAgentConfigurationChangedAsync(AgentId agentId)`
   - Transport-neutral (no SignalR coupling)

2. **Refactored `AgentsController`**
   - Old: Injected `IHubContext<GatewayHub, IGatewayHubClient>` (SignalR type)
   - New: Injected `IEnumerable<IAgentChangeNotifier>?` (nullable, fallback to empty list)
   - Best-effort broadcast: loop, log on error, continue

3. **Removed Dependency**
   - Removed `ProjectReference` to `BotNexus.Extensions.Channels.SignalR` from `BotNexus.Gateway.Api.csproj`
   - Zero other gateway projects referenced extensions

4. **Extension-Side Implementation**
   - New class: `SignalRAgentChangeNotifier` in `BotNexus.Extensions.Channels.SignalR`
   - Implements contract, bridges to SignalR hub
   - Auto-discovered via `AssemblyLoadContextExtensionLoader` (added to `DiscoverableServiceContracts`)

5. **Guard Test**
   - Location: `tests\gateway\BotNexus.Gateway.Tests\Architecture\GatewayProjectDependencyBoundaryTests.cs`
   - Scans all `src\gateway\**\*.csproj` for forbidden references
   - Fails on `ProjectReference`, `PackageReference`, or `Reference` to `src\extensions` or `BotNexus.Extensions.*`
   - Fail-before/pass-after validated by Hermes

**Root Cause Analysis:**

PR #237 needed fast `AgentsChanged` broadcasts for hot-reload UX. The shortcut—injecting SignalR hub types directly into the controller—solved the problem quickly but violated architectural boundaries. The proper solution: minimal notification contract (preserves behavior, restores dependency direction).

**Rationale:**

1. **Transport-neutral abstraction** — notifications can be delivered via SignalR, Server-Sent Events, Webhooks, or any future transport
2. **Correct dependency direction** — Gateway publishes to abstraction; extensions implement
3. **Zero-cost pattern** — nullable `IEnumerable<T>?` with fallback makes implementation optional
4. **Guard prevents regression** — architecture test catches future violations in CI

**Review Outcome (Leela):**

✅ **APPROVED**
- Boundary cleanly severed (zero gateway→extension references)
- Contract is minimal and transport-neutral
- SignalR implementation remains extension-side
- Guard test is adequate and correctly placed
- No over-abstraction

**Commits:**

- `1a8a8863` — refactor(gateway): extract IAgentChangeNotifier, sever extension dependency
- `8f7a4a21` — test(gateway): add architecture boundary guard for extension dependencies

---

### Bender Decision: CLI locations database redaction

**Date:** 2026-05-XX  
**Status:** Implemented

**Context:** `botnexus locations list` was printing `location.Path` directly. For database locations this value can be a full connection string containing credentials.

**Decision:** Use `ResolveSafeDisplayPath` in `LocationsCommand` so database locations with configured secrets always show `(redacted)`, while filesystem/API/mcp/remote-node locations still show their configured path or endpoint.

**Consequences:**
- CLI output now matches the API/UI secret-redaction contract for database location values.
- Location listing remains useful for non-secret location types.
- Regression tests assert both that secrets are absent and that non-database values remain visible.

---

### Bender Decision: Database location secret redaction

**Date:** 2026-05-XX  
**Status:** Implemented

**Context:** Gateway location APIs were echoing database connection strings through `PathOrEndpoint`, which can expose credentials in browser UI and API clients.

**Decision:** Treat database location values as secrets in all location responses (`list/get/create/update`). Return a fixed placeholder (`[connection string configured]`) and `hasConfiguredSecret=true` instead of the raw connection string.

**Consequences:**
- UI can safely display configuration status without receiving secret material.
- Create/update still accept and persist connection strings.
- Database updates with blank `value` preserve the existing secret to avoid accidental credential loss during metadata-only edits.

---

### Fry Decision: Locations Config UI

**Date:** 2026-07-29  
**Author:** Fry (Web Dev)  
**Status:** Implemented

**Context:** The configuration UI had no locations section. Users needed to edit `config.json` manually to add, update, or remove named locations. Bender had already built a full REST API at `/api/locations` (CRUD + health check) in `LocationsController`.

**Decision:** Created a standalone `LocationsConfigPanel` component that calls the locations REST API directly (via a new `LocationsApiClient` service) rather than going through the generic `PlatformConfigService` JSON section approach.

**Why a dedicated API client?** The locations REST API provides domain-aware validation, health checks, and proper per-entry CRUD — features that the generic config section save (`PUT /api/config/gateway`) cannot provide. Using the dedicated API gives users real-time feedback and prevents invalid entries.

**Files Added/Modified:**
- **Added:** `Services/LocationsApiClient.cs` — typed HTTP client with DTOs
- **Added:** `Components/Config/LocationsConfigPanel.razor` — full CRUD + health check UI
- **Added:** `LocationsConfigPanelTests.cs` — 16 bUnit component tests
- **Added:** `LocationsApiClientTests.cs` — 10 service-level tests
- **Modified:** `Program.cs` — DI registration for `LocationsApiClient`
- **Modified:** `Pages/Configuration.razor` — `case "locations"` routing

**UX Details:**
- System-managed locations (e.g., agents-dir, sessions) are shown read-only with a "system" badge
- User-defined locations have edit (✏️) and delete (🗑️) buttons
- Health check button (🩺) updates status inline
- Form validates name and value before submitting
- API errors surface in a dismissible banner without crashing
- Changes take effect immediately via the REST API — no restart needed

---

### Fry Decision: Virtual Cron Cleanup Routes Through Session Deletion

**Date:** 2026-05-11  
**Status:** Implemented (then revised)

**Context:** Deleting old cron conversations from the sidebar returned 404 because `ArchiveConversationAsync` sent `DELETE /api/conversations/{virtualId}` using the UI-only virtual key (e.g. `cron-session:cron:20260509002033:...`). These IDs don't exist as real conversations — they're projections created by `MergeVirtualCronSessions` from session summaries.

**Initial Decision (then revised):**
- Virtual cron sessions routed cleanup to `DELETE /api/sessions/{ActiveSessionId}` via the existing `DeleteSessionAsync` REST method.
- This was later revised to preserve session records during cleanup (see below).

---

### Fry Decision: Cron Conversation Cleanup Preserves Session Records

**Date:** 2026-05-11  
**Status:** Implemented

**Context:** The prior implementation routed virtual cron conversation cleanup through `DELETE /api/sessions/{sessionId}`, which permanently deletes session records and history. This was a data-loss regression — users expected "close/archive" semantics that hide conversations while preserving underlying session data.

**Decision:**
- Virtual cron conversation cleanup now routes through `DELETE /api/conversations/{conversationId}` (the same `ArchiveConversationAsync` path used for regular conversations).
- The conversation ID sent to the backend is the full `cron-session:{sessionId}` string, URL-encoded.
- Backend returns 204 idempotently for `cron-session:` prefixed IDs even when no backing session exists (handles stale orphans).
- `DeleteSessionAsync` removed from `IGatewayRestClient` — it was added solely for this cleanup path and has no other UI use.
- Stale orphans (no `ActiveSessionId`) still call the conversations endpoint rather than being cleaned up locally-only, since the backend handles them gracefully.

**Rationale:**
1. **Session preservation** — Sessions contain execution history that must survive conversation cleanup.
2. **Unified cleanup path** — All conversation types (regular, cron, legacy projections) use the same API.
3. **Idempotent backend** — No need for client-side branching logic; backend handles all cron-session: variants.
4. **Reduced surface area** — Removing DeleteSessionAsync from the UI client prevents accidental session destruction.

**Impact:**
- **Bender:** Backend already returns 204 for cron-session: archives. No changes needed.
- **Hermes:** Tests rewritten to assert conversations endpoint is called (not sessions) and that session delete is never invoked.

---

### Hermes Decision: No Session Delete Regression Guard

**Date:** 2026-05-11  
**Status:** Implemented

**Scope:** Blazor cron virtual cleanup + Gateway virtual cron archive contract

**Recommendation:** Lock regression coverage to this invariant:

1. **Blazor client (`AgentInteractionService`)** must always route cron virtual cleanup via `ArchiveConversationAsync` (`DELETE /api/conversations/{cron-session-id}`) and must never call `DeleteSessionAsync`.
2. **Gateway (`ConversationsController`)** must continue returning **204 No Content** for `cron-session:{sessionId}` in linked, orphan, and missing-session cases, while sealing/preserving existing session records when present.

**Why this matters:** Session deletion breaks conversation history guarantees and can remove persisted records users expect to retain. Archive-path cleanup keeps sidebar cleanup behavior while preserving history for reopen and audit scenarios.

**Evidence:**
- `tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests/AgentInteractionServiceTests.cs`
- `tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests/GatewayRestClientTests.cs`
- `tests/gateway/BotNexus.Gateway.Tests/ConversationsControllerHistoryTests.cs`

---

### Leela Decision: Agent Editing Hot-Reload

**Date:** 2025-07-25  
**Status:** Proposed

**Scope:** Gateway agent lifecycle, config reload, UI editing

**Context:** When an agent is added or edited (via config file change, REST API, or Blazor UI), the gateway already has infrastructure to detect changes and update the in-memory registry. However, newly registered or updated descriptors take effect for *future* sessions only — active sessions retain the stale descriptor snapshot. Additionally, the UI editing experience is limited.

**Decision:** Implement descriptor-change notifications that allow active sessions to safely adopt updated fields on next turn.

**Safe vs Unsafe Field Updates on Active Sessions:**

| Field | Hot-reload safe? | Rationale |
|-------|-----------------|-----------|
| `ModelId` | ✅ Yes | Next LLM call uses new model |
| `AllowedModelIds` | ✅ Yes | Validation only |
| `DisplayName` / `Description` | ✅ Yes | Cosmetic |
| `SystemPrompt` / `SystemPromptFile` | ⚠️ Opt-in | May invalidate conversation context |
| `ToolIds` | ⚠️ Opt-in | Adding tools is safe; removing mid-conversation risky |
| `SubAgentIds` | ⚠️ Opt-in | Same as tools |
| `ApiProvider` | ❌ No | Requires new handle/connection |
| `IsolationStrategy` | ❌ No | Requires session restart |
| `MaxConcurrentSessions` | ✅ Yes | Supervisor-level, no handle impact |

**Implementation Boundaries:**

**Gateway (Farnsworth/Hermes):**
- Add `event Action<AgentId, AgentDescriptor>? DescriptorChanged` to `IAgentRegistry`
- `DefaultAgentRegistry.Update()` fires the event
- `DefaultAgentSupervisor` subscribes to the event and notifies active handles
- `IAgentHandle` gains `void ApplyDescriptorUpdate(AgentDescriptor updated)` — each handle decides which fields to adopt
- System prompt reload: re-run prompt pipeline on next turn if `SystemPromptFile` changed

**UI (Fry):**
- Enhance `AgentConfigPanel.razor` to support inline editing of more fields (not just model)
- Add real-time feedback: after PUT succeeds, show "Applied to N active sessions" badge

**Config persistence (Hermes):**
- `PlatformConfigAgentWriter` writes back to `config.json` `agents` section
- Ensure write triggers `IOptionsMonitor` change notification

---

### Leela Design Decision: Locations Config UI — Dynamic CRUD with No-Restart Reload

**Decision Date:** 2026-08-01  
**Status:** In Progress

**Scope:** Gateway API, Blazor WebUI, runtime location resolver

**Context:** Users want to dynamically add, update, and remove locations from the Web UI without restarting the platform.

**Problem Analysis:**
1. No UI section existed for locations in Configuration page.
2. `DefaultLocationResolver` is a singleton snapshot — reads `PlatformConfig` once at construction, never refreshes.
3. `LocationsController` has full CRUD and writes to config.json via `PlatformConfigWriter`.
4. `LocationsConfigPanel.razor` and `LocationsApiClient` service already exist but weren't wired into Configuration routing.

**What's Done (Leela):**
1. **Navigation:** Added `📍 Locations` link in sidebar under Configuration.
2. **Routing:** Added `case "locations"` in `Configuration.razor` to render `LocationsConfigPanel`.
3. **Build fix:** Added missing `using System.Text.Json.Nodes` in `LocationsController.cs`.
4. **Build fix:** Added missing `NSubstitute` package reference to test csproj.
5. **Removed duplicate** `case "locations"` in Configuration.razor.

**What Remains — Assignments:**

**Bender (Runtime): DefaultLocationResolver no-restart reload**
- Inject `IOptionsMonitor<PlatformConfig>` (not `PlatformConfig`) into `DefaultLocationResolver`
- Subscribe to `OnChange` and rebuild internal `_locations` dictionary atomically
- Use `Volatile.Write`/`Volatile.Read` or `Interlocked.Exchange` for atomic swap
- Pattern: See `ApiKeyGatewayAuthHandler` which already does this

**Hermes (Testing): Fix pre-existing compilation errors**
1. Fix `EmptyAgentRegistry` to implement current `IAgentRegistry` interface
2. Add `RichardSzalay.MockHttp` package reference
3. Add tests for `DefaultLocationResolver` reload behavior

---

### Leela Decision: Unify Configuration to .NET Provider/Options Pattern

**Author:** Leela (Lead/Architect)  
**Date:** 2025-07-24  
**Status:** Proposed

**Context:** BotNexus has two parallel configuration pipelines:
1. **IConfiguration + IOptions/IOptionsMonitor** — standard .NET pattern with hot-reload
2. **PlatformConfigLoader** — custom static loader used by CLI and some API controllers

This creates duplication, inconsistency, and bypasses the reload pipeline.

**Target Architecture:**

**Reads — Three Tiers:**

| Tier | When | Pattern | Hot-reload |
|------|------|---------|------------|
| **Runtime** (gateway) | Normal operation | `IOptionsMonitor<T>` | Yes |
| **Request** (API) | HTTP handlers | `IOptionsMonitor<T>` injected | Yes |
| **Offline** (CLI) | No DI host | `PlatformConfigLoader.Load()` | No |

**Writes — Single Writer:**
All config writes must go through `PlatformConfigWriter` (provides locking, backup, atomic writes).

**Migration Rules:**

For Bender:
1. Remove `PlatformConfigLoader.LoadAsync` calls from API controllers — replace with `IOptionsMonitor<PlatformConfig>`
2. Migrate `CompactionOptions` and `CronOptions` to section binding
3. Migrate CLI writes to `PlatformConfigWriter`
4. Do NOT migrate CLI reads (acceptable offline pattern)
5. Do NOT migrate auth.json (separate credential file)

For Hermes:
1. Verify hot-reload works end-to-end
2. Verify `CompactionOptions` reload after migration
3. Verify CLI writes produce valid config with backups
4. Test `PlatformConfigWriter` concurrent safety

---
