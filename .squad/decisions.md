# Squad Decisions

## Active Decisions

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

**Loading sequence at startup:**
1. Read config — Enumerate keys under Providers, Channels.Instances, Tools.Extensions
2. Resolve folders — Compute `{ExtensionsPath}/{type}/{key}/` for each key
3. Validate folder — Log warning and skip if missing
4. Load assemblies — Create `AssemblyLoadContext` per extension (collectible for hot-reload)
5. Scan for types — Search loaded assemblies for concrete types implementing target interface
6. Register in DI — `ServiceProvider.AddSingleton<ILlmProvider>(instance)`

---

