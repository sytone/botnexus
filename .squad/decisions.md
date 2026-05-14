# Squad Decisions

## Active Decisions

### 2026-07-29 — Kif: Prompt Template Documentation Strategy

**Author:** Kif (Documentation Engineer)  
**Date:** 2026-07-29  
**Scope:** PR #242 prompt template library docs  
**Status:** Implemented

**Decision:** Document prompt template feature across two primary locations:
1. **CLI Reference** (`docs/cli-reference.md`) — Add `prompt list`, `render`, `run` subcommand docs
2. **Configuration Guide** (`docs/configuration.md`) — Add "Prompt Templates" section explaining setup, parameter resolution, and workflows

**Rationale:**
- CLI docs live in `cli-reference.md` — users learn CLI commands there
- Config docs live in `configuration.md` — users configure templates here
- Dual storage locations (config.json primary, ~/.botnexus/prompts/ file-based) documented in both
- Parameter resolution algorithm (collect → merge → validate → substitute) explained step-by-step

**Implementation:**
- `docs/cli-reference.md`: 4 new sections (prompt, list, render, run) with realistic examples
- `docs/configuration.md`: "Prompt Templates" section, parameter resolution algorithm, 4 worked examples
- Parameter syntax `{{name}}` documented as rigid (no filters/conditions)
- Cron integration example links to scheduling documentation
- All examples copy-paste ready

**Impact:**
- Users discover prompt templates via CLI help, configuration guide, or PR #242
- Documentation scope complete for basic + advanced patterns
- Deferred: UI gallery, template marketplace

**Commits:**
- `5e6deb76`: `docs(cli): add prompt template command documentation and examples`
- `0380cce7`: `docs(config): add prompt template configuration section and examples`

---

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

### 2026-05-14 — Farnsworth: Prompt Template CLI Uses Shared Resolver Pipeline

**Author:** Farnsworth (Platform Dev)  
**Date:** 2026-05-14  
**Status:** proposed  
**Scope:** CLI prompt-template commands, cron/template resolution consistency

**Decision:** Wire `botnexus prompt list|render|run` through `CronOptionsPromptTemplateResolver` so CLI behavior matches cron/template runtime resolution.

- Reuse cron template resolution and rendering semantics (required/default parameter behavior)
- Support discovery from both config templates and prompt files under ~/.botnexus/prompts and agent/workspace prompt folders
- Keep prompt run as an invocation wrapper that renders first, then posts to gateway /api/chat

**Rationale:** A single resolver path prevents drift between cron scheduling and CLI/manual invocation. It also makes storage conventions explicit and testable in one place, reducing duplicated template parsing code in the CLI.

---

### 2026-05-14 — Bender: CLI Locations Database Redaction

**Author:** Bender (Runtime Dev)  
**Date:** 2026-05-14  
**Scope:** CLI commands, location display safety  
**Status:** Implemented

**Context:** `botnexus locations list` was printing `location.Path` directly. For database locations this value can be a full connection string containing credentials.

**Decision:** Use `ResolveSafeDisplayPath` in `LocationsCommand` so database locations with configured secrets always show `(redacted)`, while filesystem/API/mcp/remote-node locations still show their configured path or endpoint.

**Consequences:**
- CLI output now matches the API/UI secret-redaction contract for database location values.
- Location listing remains useful for non-secret location types.
- Regression tests assert both that secrets are absent and that non-database values remain visible.

---

### 2026-05-14 — Bender: Database Location Secret Redaction

**Author:** Bender (Runtime Dev)  
**Date:** 2026-05-14  
**Scope:** Gateway location APIs, secret exposure prevention  
**Status:** Implemented

**Context:** Gateway location APIs were echoing database connection strings through `PathOrEndpoint`, which can expose credentials in browser UI and API clients.

**Decision:** Treat database location values as secrets in all location responses (`list/get/create/update`). Return a fixed placeholder (`[connection string configured]`) and `hasConfiguredSecret=true` instead of the raw connection string.

**Consequences:**
- UI can safely display configuration status without receiving secret material.
- Create/update still accept and persist connection strings.
- Database updates with blank `value` preserve the existing secret to avoid accidental credential loss during metadata-only edits.

---

### 2026-07-29 — Fry: Locations Config UI

**Author:** Fry (Web Dev)  
**Date:** 2026-07-29  
**Scope:** BlazorClient UI — Locations management  
**Status:** Implemented

**Context:** The configuration UI had no locations section. Users needed to edit `config.json` manually to add, update, or remove named locations.

**Decision:** Created a standalone `LocationsConfigPanel` component that calls the locations REST API directly (via a new `LocationsApiClient` service) rather than going through the generic `PlatformConfigService` JSON section approach.

**Rationale:** The locations REST API provides domain-aware validation, health checks, and proper per-entry CRUD — features that the generic config section save cannot provide. Using the dedicated API gives users real-time feedback and prevents invalid entries.

**Implementation:**
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

---

### 2026-08-01 — Leela: Locations Config UI — Dynamic CRUD with No-Restart Reload

**Author:** Leela (Lead/Architect)  
**Date:** 2026-08-01  
**Status:** In progress  
**Scope:** Gateway API, Blazor WebUI, runtime location resolver

**Context:** Users want to dynamically add, update, and remove locations from the Web UI without restarting. `DefaultLocationResolver` is a singleton snapshot that never refreshes when API writes occur to config.json.

**What's Done:**
1. Navigation: Added `📍 Locations` link in MainLayout sidebar
2. Routing: Added `case "locations"` in Configuration.razor
3. Build fixes: Added missing `using` and `NSubstitute` package reference

**What Remains:**

1. **Bender (Runtime):** Make `DefaultLocationResolver` hot-reload via `IOptionsMonitor<PlatformConfig>` subscription
   - Change: Inject `IOptionsMonitor<PlatformConfig>` (not snapshot)
   - Subscribe to `OnChange` and rebuild internal `_locations` dictionary atomically
   - Use `Volatile.Write`/`Interlocked.Exchange` for thread-safe swap

2. **Hermes (Testing):** Fix pre-existing test compilation errors
   - Fix `EmptyAgentRegistry` to match current `IAgentRegistry` interface
   - Add `RichardSzalay.MockHttp` package reference
   - Add tests for `DefaultLocationResolver` reload behavior

3. **Fry (Web UI):** No changes needed — panel and API client already complete

**Architecture Notes:**
- Config persistence: `LocationsController` → `PlatformConfigWriter` → config.json → file watcher → `IOptionsMonitor` → `DefaultLocationResolver` rebuild
- No new API contracts needed
- System-managed locations remain read-only in the UI

---
