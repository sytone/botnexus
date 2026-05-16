# Squad Decisions

## Active Decisions

### 2026-05-14 — Leela: Add `.prompt.md` Format for Readable Multi-Line Templates

**Author:** Leela (Lead)  
**Date:** 2026-05-14  
**Scope:** PR #242 — Prompt Template Library  
**Status:** Approved

**Problem:** Current `.prompt.json` format stores prompt bodies as single-line JSON strings. Multi-line prompts with headings, bullet lists, numbered lists, and paragraphs become unreadable. This defeats the purpose of file-based templates — humans can't author or review them comfortably.

**Decision: Dual-Format Support (`.prompt.md` + `.prompt.json`)**

Add **`.prompt.md`** as the primary authoring format for multi-line prompts using YAML front matter for metadata and markdown body for prompt content. `.prompt.json` remains fully supported for backward compatibility. When both formats exist, `.prompt.md` takes precedence.

**Format Example:**
```markdown
---
name: status-report
description: Weekly status report
parameters:
  project:
    description: Name of the project
    required: true
  owner:
    description: Team lead email
    default: team@example.com
---
Generate a weekly status report for {{project}}.

## Details
- **Owner:** {{owner}}
- **Summary:** {{summary}}
```

**Implementation Scope:**
- Farnsworth: Add `ParseMarkdownTemplateFile` to `CronOptionsPromptTemplateResolver`, update glob patterns for both `*.prompt.json` and `*.prompt.md`
- Hermes: Unit tests for parsing, parameter validation, precedence, round-trip render
- Kif: Update `docs/configuration.md` and `docs/cli-reference.md`, add `.prompt.md` samples

**Rationale:**
- Repo already uses YAML-front-matter pattern in `.github/prompts/`
- GitHub Copilot uses `.prompt.md` with front matter — users know this format
- Markdown is universally readable and editable
- Aligns with emerging MCP prompt resource pattern
- Implementation cost low — one parser method, glob update, tests, docs

**Verdict:** APPROVED for PR #242. Solves real usability problem with contained scope.

---

### 2026-05-14 — Farnsworth: Implement Readable `.prompt.md` Support

**Author:** Farnsworth (Platform Dev)  
**Date:** 2026-05-14  
**Scope:** PR #242 — Prompt Template Implementation  
**Status:** Implemented

**Decision:** Adopt dual file-format support in the shared prompt resolver:
- `.prompt.md` with YAML front matter (name, defaults, parameters) and markdown body as prompt text
- `.prompt.json` retained for compatibility and machine-generated templates
- When both formats exist for the same template, `.prompt.md` takes precedence

**Implementation Details:**
- Added `ParseMarkdownTemplateFile()` to `CronOptionsPromptTemplateResolver` using `YamlDotNet`
- Updated `TryFindTemplatePath` and `DiscoverTemplates` to glob both `*.prompt.json` and `*.prompt.md`
- Body parsing: Everything after closing `---` of front matter is used verbatim; whitespace and formatting preserved
- Parameter substitution (`{{parameter}}`) works identically to JSON format
- Leading/trailing blank lines after front matter trimmed

**Why:** Keeps existing JSON workflows stable while making real-world prompt authoring substantially easier for humans.

**Commits:**
- `e91d130c`: Implementation with `.prompt.md` parsing and tests

---

### 2026-05-14 — Kif: Documentation for `.prompt.md` Format

**Author:** Kif (Documentation Engineer)  
**Date:** 2026-05-14  
**Scope:** PR #242 — Documentation and Samples  
**Status:** Complete

**What:** Comprehensive documentation for the new `.prompt.md` format as the primary authoring format for human-readable, multi-line prompts.

**Documentation Changes:**
- **`docs/configuration.md`:** Updated to document `.prompt.md` (recommended for multi-line) and `.prompt.json` (compatibility). Added new "File-Based Templates: `.prompt.md` Format" section explaining YAML front matter, properties, body parsing, and advantages. Added "File-Based Templates: `.prompt.json` Format (Compatibility)" section. Added Example 4: Sprint retrospective template with multi-line structure, bullet lists, numbered lists.
- **`docs/cli-reference.md`:** Updated prompt section overview to mention both formats with brief format guide. Updated prompt list section to note templates can be either format.

**Sample Files:**
- `prompts/sample-greeting.prompt.md` — Simple multi-line greeting with basic parameter
- `prompts/sample-code-review-checklist.prompt.md` — Structured code review template with multiple sections, checkboxes, parameters, approval workflow

**Alignment:**
- ✅ Aligns with Leela's approved decision (2026-05-14)
- ✅ Addresses user concern about readability of multi-line prompts
- ✅ Maintains backward compatibility with `.prompt.json`
- ✅ Documentation is reader-first with real workflow examples

**Implementation Status:**
- ✅ Documentation complete and committed (commit `dd82a343`)
- ✅ Sample `.prompt.md` files provided

---

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

### 2026-05-14 — Bender: Database Location Secret Redaction

**Author:** Bender (Runtime Dev)  
**Date:** 2026-05-14  
**Scope:** Gateway location APIs, secret exposure prevention  
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

### 2026-07-29 — Fry: Locations Config UI

**Author:** Fry (Web Dev)  
**Date:** 2026-07-29  
**Scope:** BlazorClient UI — Locations management  
**Status:** Implemented

**Context:** The configuration UI had no locations section. Users needed to edit `config.json` manually to add, update, or remove named locations.

**Decision:** Created a standalone `LocationsConfigPanel` component that calls the locations REST API directly (via a new `LocationsApiClient` service) rather than going through the generic `PlatformConfigService` JSON section approach.

**Rationale:** The locations REST API provides domain-aware validation, health checks, and proper per-entry CRUD — features that the generic config section save cannot provide. Using the dedicated API gives users real-time feedback and prevents invalid entries.

**Implementation:**
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

### 2026-05-14 — Farnsworth: Bundle CLI Prompt Samples as Internal Resources

**Author:** Farnsworth (Platform Dev)  
**Date:** 2026-05-14  
**Status:** Implemented  
**Scope:** CLI artifact portability, prompt sample distribution

**Context:** Prompt sample templates were stored in repo-root `prompts/`, which does not ship with installed/published CLI artifacts. When `botnexus prompt create samples` ran in a tool install context, the root samples folder was unavailable, and sample initialization failed.

**Decision:** Move sample templates to `src\gateway\BotNexus.Cli\Resources\Prompts\` and embed them in `BotNexus.Cli` with `EmbeddedResource` items and stable logical names (`PromptSamples/<file>`). `botnexus prompt create samples` now copies from assembly resources into `~/.botnexus/prompts`.

**Implementation:**
1. Moved all `.prompt.md` and `.prompt.json` files from `prompts/` (repo root) to `src\gateway\BotNexus.Cli\Resources\Prompts\`
2. Added `EmbeddedResource` ItemGroup to `BotNexus.Cli.csproj` with logical name prefix `PromptSamples/`
3. Refactored `PromptCommands.ExecuteCreateSamplesAsync` to enumerate embedded resources from assembly via `GetType().Assembly.GetManifestResourceNames()` and copy to user home
4. Deleted repo-root `prompts/` folder (no longer needed for published/tool installs)
5. Updated docs and tests

**Consequences:**
- CLI sample initialization works in published/tool installs without requiring repo-root files
- Runtime prompt lookup remains unchanged (`~/.botnexus/prompts`, agent prompts, workspace prompts)
- Repository root no longer serves as runtime sample source of truth

**Validation:**
- ✅ Targeted tests: `PromptCommandsTests` — all passing
- ✅ Full build: no warnings, no errors
- ✅ Full test suite: all passing
- ✅ Pre-commit hook: build + test — passing

**Commit:** `4aa53e4` — `fix(cli): embed prompt sample templates`  
**PR:** #247 — https://github.com/sytone/botnexus/pull/247

---

### 2026-07-29 — Leela: Portal Agent Workspace Tabs — PR Scope (Issue #245)

**Author:** Leela (Lead)  
**Date:** 2026-07-29  
**Issue:** #245 — [Portal] Agent workspace UI - tabbed agent panel  
**Status:** Approved  

**Problem:** Issue #245 requests a full tabbed agent panel (Conversation, Workspace, Reports, Canvas). Implementing all four tabs with backend APIs in a single PR is an unreviewable mega-change. We need a vertical slice that delivers visible value, proves the tab architecture, and keeps mobile responsive.

Additionally, the current top banner (`app-banner`) is too large/cramped on phones — it needs mobile-first attention.

**Decision: PR-1 Scope — AgentPanel Shell + Tab Bar + Mobile Header**

**What's IN this PR:**
1. **AgentPanel component** — new `AgentPanel.razor` wrapping the tab structure
   - Tab bar with icons: 💬 Chat | 📁 Workspace | 📊 Reports | 🎨 Canvas
   - Only **Chat** tab is functional; other tabs show placeholder "Coming soon" states
   - Tab bar is horizontal, scrollable on mobile (no wrapping)
   - Active tab stored in component state via `?tab=` query parameter deep-linking

2. **ChatPanel extraction** — existing `ChatPanel.razor` renders inside the Chat tab unchanged
   - No behaviour changes to chat; purely re-parented into tab content area
   - All existing ChatPanel parameters/state preserved

3. **Mobile-first responsive header**
   - Banner height reduced on `≤480px`: hide "BotNexus" text, keep burger + logo only (~36px height)
   - Tab bar uses compact icons-only mode on `≤480px`, icons+labels on `≥769px`
   - `chat-header-actions` moves into a "⋮" overflow menu on mobile

4. **CSS variables/structure** for tab theming (consistent with dark theme vars)

5. **bUnit tests** for AgentPanel:
   - Renders tab bar with 4 tabs
   - Default tab is Chat
   - Clicking tab switches content
   - Mobile class variants applied correctly

**What's DEFERRED:**
- Workspace tab (file tree, agent memory) — requires gateway API
- Reports tab (run history, cost) — requires new domain model
- Canvas tab (freeform drawing) — needs design spec
- Backend APIs — no API work in PR-1

**Architecture Constraints:**
- No new backend APIs — PR-1 is frontend-only
- No new NuGet packages — CSS + Blazor component only
- Tab content uses `display:none`/`display:block` toggling to preserve ChatPanel scroll position
- AgentPanel receives `AgentId` parameter, delegates to ChatPanel internally
- Home.razor changes from `<ChatPanel>` directly to `<AgentPanel>` — single substitution

**Verdict:** APPROVED. Vertical slice delivers tab architecture + mobile header improvement without backend coupling.

---

### 2026-07-29 — Amy: Mobile-First Responsive Design for AgentPanel — Issue #245

**Author:** Amy (UI Designer)  
**Date:** 2026-07-29  
**Status:** Active

**Executive Summary:** The new AgentPanel component (tab strip + conversation/workspace/reports/canvas UI) must remain usable on mobile phones. This decision documents the responsive breakpoints, layout strategies, and CSS patterns for mobile responsiveness.

**Key Decisions:**

**Breakpoints:**
```css
/* Mobile: 320px–767px (phones, small tablets) */
/* Tablet: 768px–1023px (landscape phones, small tablets) */
/* Desktop: 1024px+ (laptops, large monitors) */
@media (max-width: 767px) { /* mobile-only */ }
@media (min-width: 768px) and (max-width: 1023px) { /* tablet */ }
@media (min-width: 1024px) { /* desktop */ }
```

**Mobile Layout Strategy:**
- **Top Banner:** 40px on phones (reduced from 48px); hide "BotNexus" text
- **Tab Strip:** 36px height; icon-only on phones; scrollable (not wrapping); momentum scroll on iOS
- **Tab Content:** Single-column stacked, full width, no fixed-width side panes
- **Touch targets:** Minimum 44×44px (WCAG AA)

**Tab Content Responsiveness:**
- **Conversation Tab:** Message area flex 1, input area sticky at bottom
- **Workspace Tab:** Stack file tree + editor vertically on phones
- **Reports Tab:** Full width stacked layout
- **Canvas Tab:** Responsive iframe, full width

**Vertical Space Budget (iPhone 12 Mini — 375×812px):**
- Status bar: 47px
- App banner: 40px (optimized)
- Agent header: 36px (new, compact)
- Tab strip: 36px (scrollable)
- **Available content:** ~664px
- Chat input: 42px (sticky)
- **Scrollable chat area:** ~622px ✓ Usable

**Accessibility Checklist:**
- ✓ Touch targets: 44×44px minimum
- ✓ Focus indicators: Visible outline (2px solid)
- ✓ Color contrast: 4.5:1 (AA)
- ✓ Reduced motion: Respected `prefers-reduced-motion: reduce`
- ✓ Keyboard navigation: Tab order logical
- ✓ Screen reader: ARIA live regions for updates
- ✓ Zoom: Content readable at 200%

**Implementation Guidance:**
1. Mobile-first CSS: base styles for 320px, `@media (min-width: 768px)` enhancements, `@media (min-width: 1024px)` polish
2. Test on real devices: iPhone SE 2 (375px), iPhone 12/13 (390px), iPad Air 4 (820px landscape), Surface Duo (540px)
3. Use DevTools for iteration (Chrome DevTools device emulation, Safari responsive design mode)

**Decision Rationale:**
- Collapse top banner on mobile → frees ~8px height
- Compact agent header (36px on mobile) → preserves vertical space; truncate name if needed
- Scrollable tab strip (36px on mobile) → icon-only tabs avoid crowding; horizontal scroll expected on mobile
- Single-column stacked layouts → simplifies mobile layout; no fixed-width side panes
- 44px touch targets → WCAG AA + iOS convention
- Momentum scroll on iOS → native feel with `-webkit-overflow-scrolling: touch`
- Horizontal scroll prevention → no `100vw` width; prevents frustration

---

### 2026-07-29 — Hermes: Issue #245 Vertical Slice Test Contract

**Author:** Hermes (QA)  
**Date:** 2026-07-29  
**Issue:** #245 — Portal agent panel tabs  
**Status:** Active

**Decision:** For PR-1, QA will enforce a narrow vertical-slice contract that is implementation-agnostic but concrete enough for TDD:

1. Selecting an agent/conversation must render an AgentPanel shell hook: `[data-testid='agent-panel']`
2. The shell must expose exactly four tabs in this order: `Conversation`, `Workspace`, `Reports`, `Canvas`
3. Conversation is the default active tab hook: `.agent-panel-tab.active[data-tab='conversation']`
4. Conversation tab must host chat parity surface: `[data-testid='agent-panel-conversation'] .chat-panel`
5. Mobile/responsive CSS hooks must exist in `app.css`:
   - `.agent-panel`, `.agent-panel-header`, `.agent-panel-tab-strip`, `.agent-panel-tab`
   - `@media (max-width: 768px)` block retained for phone layout rules

**Why:** The existing test suite already covers ChatPanel behavior deeply; this contract verifies **re-parenting and shell structure** without duplicating chat internals. Selector-based hooks give Fry/Amy freedom in markup details while still preserving stable QA gates. Mobile hook checks ensure responsive work is not deferred behind desktop-only implementation.

**Test Artifact:** `tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests/AgentPanelVerticalSliceTests.cs`

---

### 2026-07-29 — Fry: AgentPanel PR-1 Vertical Slice Shell

**Author:** Fry (Web Dev)  
**Date:** 2026-07-29  
**Status:** Implemented

**Scope:** Added `AgentPanel.razor` as the chat canvas shell for Issue #245 PR-1.

**Key Decisions:**

1. **State preservation:** Tab switching uses CSS class visibility (`.agent-tab-pane` / `.active`) instead of conditional rendering, so the mounted `ChatPanel` keeps local UI state.
2. **Deep-linking in-slice:** Tab selection is persisted via `?tab=` query string (`conversation`, `workspace`, `reports`, `canvas`) to avoid route expansion during PR-1.
3. **Mobile behavior:** At `≤480px`, banner is compacted, tab labels collapse to icons, and chat header actions move to an overflow menu (`⋮`) to keep controls usable without horizontal overflow.

**Files:**
- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Components/AgentPanel.razor`
- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Pages/Home.razor`
- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Components/ChatPanel.razor`
- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/wwwroot/css/app.css`

---

### 2026-08-01 — Hermes: Unskip AgentPanel Vertical-Slice QA Gates

**Author:** Hermes (QA)  
**Date:** 2026-08-01  
**Issue:** #245 — Portal agent workspace tabs  
**Status:** Implemented

**Decision:** Removed all `[Fact(Skip=...)]` markers from `AgentPanelVerticalSliceTests.cs` and kept the original vertical-slice scope intact:

1. Agent panel shell selector renders (`[data-testid='agent-panel']`).
2. Tab contract remains fixed to `Conversation`, `Workspace`, `Reports`, `Canvas`.
3. Conversation tab remains default-active via `.agent-panel-tab.active[data-tab='conversation']`.
4. Conversation pane still hosts chat parity surface (`[data-testid='agent-panel-conversation'] .chat-panel`).
5. Mobile CSS hooks remain in `app.css`, including `@media (max-width: 768px)` and placeholder styling hooks.

**QA Notes:**
- Stabilized tab-label assertion to target `.agent-tab-label` instead of full button text (avoids icon-text brittleness).
- Added explicit placeholder-content checks for Workspace/Reports/Canvas copy.
- Hardened CSS file lookup by resolving repository root via `BotNexus.slnx`, avoiding brittle relative traversal from test bin paths.

**Validation:**
- Targeted Blazor client tests pass with **0 skipped** in `AgentPanelVerticalSliceTests`.
- `dotnet build BotNexus.slnx --nologo --tl:off` passes.
- `dotnet test BotNexus.slnx --nologo --tl:off` passes.

---

### 2026-08-01 — Leela: PR-1 Agent Panel Slice — Review Outcome

**Date:** 2026-08-01  
**Reviewer:** Leela  
**Verdict:** APPROVED

**Scope Compliance:**
The slice correctly delivers:
- AgentPanel shell component with 4 tabs (Conversation, Workspace, Reports, Canvas)
- Conversation tab wraps existing ChatPanel — parity preserved
- Workspace/Reports/Canvas are labeled placeholders
- Deep-link support via `?tab=` query parameter (URL-synced)
- Mobile responsive styles in `app.css` with proper media queries
- Proper ARIA: tablist/tab/tabpanel roles, aria-selected, aria-controls
- No backend, API, or tool overreach — purely frontend

**Build:** 0 warnings, 0 errors. ✅

---

### 2026-07-30 — Leela: Phase 3 Decision — Reports Tab Read-Only Report Listing

**Author:** Leela (Lead/Architect)  
**Date:** Phase 3 kick-off  
**Issue:** #245  
**Branch:** `dev/botnexus/feature-portal-reports-tab`  

## 1. Scope Decision

**Phase 3 delivers read-only Reports tab + Report listing API.**
Canvas tab, workspace write operations, realtime refresh, and report-generation tooling are **deferred**.

### Rationale

- Phase 2 established the workspace file API pattern (path-sandboxed, read-only, `IFileSystem`-abstracted). Reports follow the same pattern: they are markdown files written to an agent's workspace under a `reports/` convention directory.
- A read-only listing PR keeps the diff ≤ 400 lines of production code — reviewable in one sitting.
- Report creation (tool integration for agents to write reports) and SignalR push are orthogonal features that can follow without rework.

### Explicit Deferrals

| Item | Deferred To |
|------|-------------|
| `report_publish` tool (agents writing reports) | Phase 4 |
| SignalR realtime refresh on new reports | Phase 4 |
| Canvas tab | Phase 5+ |
| Workspace write operations | Phase 5+ |
| Final mobile polish / cross-tab swipe | Phase 5+ |

## 2. Backend API / Tool Boundaries

### New Endpoint: `GET /api/agents/{agentId}/workspace/reports`

Returns a filtered view of the existing workspace tree scoped to the `reports/` subdirectory.

**Implementation strategy:** Reuse `WorkspaceController`'s path validation and `IFileSystem` abstraction. Add a dedicated action method (or a thin `ReportsController`) that:

1. Resolves `{workspaceRoot}/reports/` via `IAgentWorkspaceManager`.
2. Lists markdown files (`.md`) with metadata: filename, size, last-modified timestamp.
3. Returns `404` if agent unknown; returns empty list if `reports/` dir absent.

### Report Content

`GET /api/agents/{agentId}/workspace/reports/{fileName}` — delegates to existing `WorkspaceController.GetFile` logic. No new code needed; the workspace file-read endpoint already serves files under any relative path.

### Security

- Same sandbox rules as Phase 2: path traversal blocked by `DefaultPathValidator`.
- No authentication changes needed (local gateway trust model).
- Reports directory is read-only from the API perspective in this phase.

### Report Storage Convention

Reports live at: `{agentWorkspaceRoot}/reports/{name}.md`
Agents write them via filesystem tools (today) or a future `report_publish` tool (Phase 4).

## 3. Frontend Reports Tab

### Component: `ReportsPanel.razor`

Replaces the current placeholder in `AgentPanel.razor`.

**Responsibilities:**

1. On mount, call `GET /api/agents/{agentId}/workspace/reports` via `IGatewayRestClient`.
2. Render a list of report filenames with size and last-modified.
3. On click, fetch markdown content via existing workspace file endpoint and render with `marked.js` (already bundled).
4. Loading / empty / error states matching WorkspacePanel patterns.

### Mobile Behavior

- Single-column layout: list view → tap → detail view with back button (same pattern as `WorkspacePanel`'s mobile file viewer).
- Reuse `_showMobileViewer` / `ShowTreeOnMobile` pattern.
- No new mobile CSS classes needed beyond what WorkspacePanel established.

## 4. SignalR / Realtime

**Deferred to Phase 4.** This PR uses poll-on-tab-switch only (fetch on mount / tab activation). Rationale: realtime requires a hub event contract (`ReportPublished`) that couples to the not-yet-built `report_publish` tool.

## 5. Test Requirements & Agent Handoff

### Bender (Backend)

1. **Unit tests** for reports listing endpoint:
   - Agent not found → 404
   - Reports dir missing → 200 with empty list
   - Reports dir with mixed files → only `.md` files returned
   - Path traversal attempt → blocked
2. Use `MockFileSystem` from `System.IO.Abstractions.TestingHelpers`.
3. If a new `ReportsController` is created, it needs constructor injection tests.

### Fry (Frontend)

1. **bUnit tests** for `ReportsPanel.razor`:
   - Loading state renders spinner
   - Empty report list shows friendly message
   - Report list renders items with names
   - Click fetches and renders markdown content
   - Error state on API failure
2. Mobile back-button behavior test.

### Hermes (Integration / CI)

- No new CI config needed; existing `dotnet test` covers new test projects.
- Verify build passes with zero warnings after new files added.

### Amy (UX / CSS)

- Style `ReportsPanel` using existing `.workspace-panel` patterns.
- Ensure report list + viewer matches visual language of WorkspacePanel.
- Mobile breakpoint behavior: single-column with back nav.

## Summary

Phase 3 is a **thin read-only vertical**: list reports from workspace filesystem, render them in the portal. No new tools, no SignalR, no write paths. This keeps the PR small, independently shippable, and establishes the report-rendering contract that Phase 4's `report_publish` tool will target.

---

### 2026-07-30 — Bender: Reports Read API (Issue #245 Phase 3)

**Author:** Bender (Runtime Dev)  
**Scope:** Phase 3 Reports backend implementation  
**Status:** Implemented  

## Scope Delivered
- Added `ReportsController` with read-only endpoints:
  - `GET /api/agents/{agentId}/reports`
  - `GET /api/agents/{agentId}/reports/{**name}`
- Scoped reads to `{workspace}/reports` only.
- Enforced markdown-only report names (`.md`) and blocked rooted/segmented/invalid names.
- Kept phase read-only (no PUT/DELETE/tool integration/SignalR).

## Security Model
- Reused `DefaultPathValidator` workspace-jail checks from Phase 2.
- Extracted shared path/symlink resolution into `WorkspacePathSecurity` and reused in `WorkspaceController` + `ReportsController`.
- Added explicit containment check that final resolved report target remains under resolved reports root (prevents symlink escape from reports folder).

## API Contract
- List response DTO: `ReportsListResponse { reports: ReportListItemDto[] }`.
- Content response DTO: `ReportContentResponse` (name, size, lastModifiedUtc, content, encoding).

## Validation
- `dotnet build BotNexus.slnx --nologo --tl:off -warnaserror` ✅
- `dotnet test tests\gateway\BotNexus.Gateway.Tests\BotNexus.Gateway.Tests.csproj --no-build --nologo --tl:off --filter "FullyQualifiedName~ReportsControllerTests|FullyQualifiedName~ReportsControllerIntegrationTests|FullyQualifiedName~WorkspacePathSecurityTests|FullyQualifiedName~WorkspaceControllerIntegrationTests"` ✅
- `dotnet test BotNexus.slnx --no-build --nologo --tl:off` ✅

## Notes / Deferrals
- No workspace write APIs, report publishing tool, or realtime report events in this phase.

---

### 2026-07-30 — Fry: Reports Tab UI (Issue #245 Phase 3)

**Author:** Fry (Web Dev)  
**Date:** 2026-07-30  
**Branch:** `dev/botnexus/feature-portal-reports-tab`  

## Decision Summary

The Reports tab is implemented as a read-only `ReportsPanel` that:

1. Lists reports from `GET /api/agents/{agentId}/reports`
2. Loads selected report content from `GET /api/agents/{agentId}/reports/{name}`
3. Renders markdown through `BotNexus.renderMarkdown` (marked + DOMPurify)
4. Falls back to escaped plain-text preview if markdown JS render is unavailable

## Why

- Aligns frontend contract with Bender's dedicated reports API and DTO shape (`{ reports: [...] }`, `ReportContentResponse`).
- Preserves the Phase 3 security boundary: read-only access, no writes, no realtime coupling, no report tool dependency.
- Reuses existing safe markdown helper instead of introducing raw HTML rendering.

## UX/Behavior Notes

- Mobile behavior mirrors workspace panel patterns (`mobile-list` / `mobile-viewer`, back button).
- Empty, loading, and error states are explicit in-list and in-viewer.
- Long content is truncated client-side with an explanatory notice.
- Conversation, Workspace, and Canvas tab behavior remain unchanged.

## Test Coverage Added

- `ReportsPanelTests` for loading, empty, selection/rendering, fallback, error, and mobile back behavior.
- `GatewayRestClientTests` for reports listing/content endpoint URLs and request path handling.
- `AgentPanelVerticalSliceTests` now verifies mounted reports panel in tab shell.

---

### 2026-08-03 — Hermes: Reports Phase Test Contract

**Date:** 2026-08-03  
**Owner:** Hermes (QA)  
**Issue:** #245 Phase 3  

## Decision Context
- Current implementation and tests align on `GET /api/agents/{agentId}/reports` and `GET /api/agents/{agentId}/reports/{name}`.
- Report list payload contract is object-wrapped: `{ reports: [...] }`.
- Report content contract uses `ReportContentDto` (`name`, `size`, `lastModifiedUtc`, `content`, `encoding`).

## QA Outcome
- Added/validated test coverage for:
  - backend reports list + content + missing + invalid extension/name + directory-as-report + traversal/symlink escape
  - Blazor ReportsPanel loading/empty/error/select/render + safe plain-text fallback + mobile CSS hooks/back button
  - client contract tests for report URL encoding and DTO naming

## Follow-up
- Prior design note referenced `/workspace/reports`; tests now lock the live `/reports` route contract to prevent accidental regressions.

---

### 2026-07-30 — Leela: Reports Tab Phase 3 — Architecture Review

**Date:** 2025-07-30  
**Author:** Leela (Lead/Architect)  
**Status:** APPROVED  

## Scope Verification

Phase 3 delivers read-only reports only. No write endpoints (POST/PUT/DELETE), no report tool integration, no SignalR realtime, no Canvas rendering. Confirmed via code inspection — `ReportsController` exposes only `[HttpGet]` actions.

## Security Assessment

The backend implements defense-in-depth for path traversal and symlink escape:
1. `IsSafeReportName` — rejects rooted paths, directory separators, non-`.md` extensions, null bytes, invalid chars
2. `DefaultPathValidator.ValidateAndResolve` — workspace-scoped path resolution
3. `WorkspacePathSecurity.ResolveFinalTargetPath` — resolves symlink chains
4. `WorkspacePathSecurity.IsUnderPath` — confirms final target remains within reports directory
5. Binary content detection and 512KB read cap

Symlink escape tests confirm 403 for both listing and single-file retrieval.

## Frontend Assessment

`ReportsPanel.razor` handles all required UI states:
- Loading, empty, error, selection, mobile back-navigation
- Markdown rendered via JS interop with safe fallback to `<pre>` (plain text, HTML-escaped by Blazor)
- Client-side truncation at 200K chars with notice
- XSS mitigated: fallback renders in `<pre>` which Blazor auto-escapes

## Test Coverage

- **Unit (8 tests):** ReportsControllerTests — happy path, missing dir, missing file, directory-as-file, unsafe names (theory), symlink escape read + list
- **Integration (2 tests):** Full HTTP pipeline via WebApplicationFactory — list+read, empty directory
- **bUnit (8 tests):** Loading, empty, selection with markdown render, error state, plain-text fallback, XSS escaping in fallback, mobile navigation, CSS hooks

All 34 reports-related tests pass green.

## Verdict

**APPROVED** — Implementation is scope-compliant, security-hardened, and adequately tested for PR merge.

**All Gates Passed:**
- ✅ Tests unskipped and passing (0 skipped, 0 failures)
- ✅ Frontend-only scope preserved (no backend APIs)
- ✅ Mobile responsive CSS implemented
- ✅ Tab architecture proven
- ✅ Code review complete

**PR Result:** https://github.com/sytone/botnexus/pull/248

---

---

# Phase 2 Decision: Portal Workspace Tab

**Decision:** Option A — Workspace tab + secured workspace REST API only.  
**Deferred:** Reports tab, Canvas tab, and final deep-link/mobile validation across all tabs.

---

## 1. Scope Decision & Deferrals

### In scope (Phase 2)

1. **Backend: WorkspaceController** — New `api/agents/{agentId}/workspace` REST API with directory listing and file read endpoints.
2. **Backend: Path traversal enforcement** — Reuse `DefaultPathValidator` + `FileAccessPolicy` to jail all API requests inside `~/.botnexus/agents/{agentId}/workspace/`.
3. **Frontend: WorkspacePanel component** — Replace the placeholder in `AgentPanel.razor` with a file-tree + content-viewer component.
4. **Frontend: Mobile behavior** — Workspace tab collapses file tree on narrow viewports (single-pane mode).
5. **Tests: Backend** — Path traversal adversary tests, controller unit tests, integration test for the full request pipeline.
6. **Tests: bUnit** — WorkspacePanel rendering, file selection, empty-state, error-state, mobile layout.

### Deferred to Phase 3+

- **Reports tab** — Needs report-generation tooling/conventions defined first; different data flow (generated artifacts vs. browsed files).
- **Canvas tab** — Requires sandboxed iframe design; separate security review.
- **Final deep-link + mobile validation** — Cross-tab integration test pass after all tabs are live.
- **File editing via portal** — Phase 2 is read-only; write support is a separate risk surface.

### Why not Option B (Workspace + Reports)?

Reports share the "list files, read file" pattern at the API level, but differ materially:
- Reports need a *reports directory convention* (`workspace/reports/` or a separate root).
- Reports may need real-time refresh (SignalR push when agent writes a new report).
- Bundling both doubles the PR review surface without the shared-API savings justifying it.
- Better to land Workspace, validate the pattern, then stamp out Reports quickly in Phase 3.

---

## 2. Backend Contract / API Boundaries

### New endpoints

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `api/agents/{agentId}/workspace` | List workspace root (flat directory listing) |
| `GET` | `api/agents/{agentId}/workspace?path={relativePath}` | List subdirectory or read file content |

### Response shapes

```
// Directory listing
{
  "type": "directory",
  "path": "memory",
  "entries": [
    { "name": "2026-01-15.md", "type": "file", "size": 1234 },
    { "name": "archive", "type": "directory" }
  ]
}

// File content
{
  "type": "file",
  "path": "SOUL.md",
  "content": "...",
  "size": 1234,
  "encoding": "utf-8"
}
```

### Security requirements

1. **Path traversal prevention:** Resolve `path` parameter against workspace root using `Path.GetFullPath()` + containment check. Reject any resolved path that escapes `~/.botnexus/agents/{agentId}/workspace/`.
2. **Reuse existing infra:** `FileAgentWorkspaceManager.GetWorkspacePath(agentId)` gives the root. `DefaultPathValidator` or equivalent containment check prevents escape.
3. **Symlink resolution:** Follow the `PathUtils.ResolveFinalTargetPath` pattern — resolve symlinks, then re-check containment.
4. **Agent existence check:** Return 404 if agent is not registered in `IAgentRegistry`.
5. **Size limit:** Cap file content responses at a reasonable limit (e.g., 512 KB) to avoid portal OOM.
6. **Binary files:** Return metadata only (name, size, type=binary) — no content for non-text files.
7. **Read-only:** No PUT/POST/DELETE on workspace files in Phase 2.

### Dependencies

- `IAgentRegistry` — validate agent exists.
- `IAgentWorkspaceManager` — resolve workspace path.
- `IFileSystem` (System.IO.Abstractions) — all file I/O for testability.

---

## 3. Frontend Component Responsibilities

### Component hierarchy

```
AgentPanel.razor
  └─ WorkspacePanel.razor          (new — replaces placeholder)
       ├─ WorkspaceFileTree.razor   (new — recursive directory tree)
       └─ WorkspaceFileViewer.razor (new — file content display)
```

### WorkspacePanel

- Calls `GET api/agents/{agentId}/workspace` on mount.
- Maintains state: current directory path, selected file, loading/error flags.
- Desktop: side-by-side tree + viewer. Mobile (<768px): tree-only until file selected, then viewer with back button.

### WorkspaceFileTree

- Renders entries from directory listing response.
- Lazy-loads subdirectories on expand (one API call per expand).
- Icons by type (📄 file, 📁 directory).
- Highlights selected file.

### WorkspaceFileViewer

- Displays file content as preformatted text (monospace, scroll).
- Shows file path breadcrumb at top.
- Shows "Select a file" empty state when nothing selected.
- Shows error state if file read fails.
- Truncation message if file exceeds size cap.

### Mobile behavior

- Single-pane: file tree fills width, tapping a file replaces tree with viewer.
- Back button in viewer returns to tree.
- Same `agent-panel-mobile.css` breakpoint pattern from Phase 1.

### REST client additions

Add to `IGatewayRestClient` / `GatewayRestClient`:
```csharp
Task<WorkspaceDirectoryResponse?> GetWorkspaceAsync(string agentId, string? path = null, CancellationToken ct = default);
```

---

## 4. Test Requirements

### Backend tests

| Test class | Coverage |
|------------|----------|
| `WorkspaceControllerTests` | Valid agent → 200 with directory listing; unknown agent → 404; empty workspace → 200 with empty entries |
| `WorkspacePathSecurityTests` | `../` traversal → 400; `../../etc/passwd` → 400; absolute path → 400; symlink escape → 400; URL-encoded traversal → 400; null/empty path → root listing |
| `WorkspaceControllerIntegrationTests` | Full pipeline with `WebApplicationFactory`: registered agent with seeded workspace files → list + read roundtrip |

### Frontend bUnit tests

| Test class | Coverage |
|------------|----------|
| `WorkspacePanelTests` | Renders loading state; renders empty workspace; renders file tree after load; file selection triggers viewer; error state on API failure |
| `WorkspaceFileTreeTests` | Renders directory entries; expand subdirectory triggers callback; highlights selected item |
| `WorkspaceFileViewerTests` | Shows file content; shows empty state; shows error state; truncation message |
| `AgentPanelVerticalSliceTests` | (extend existing) Workspace tab active → renders WorkspacePanel instead of placeholder |

---

## 5. Agent Handoff Instructions

### Bender (Backend)

**Task:** Implement `WorkspaceController` in `BotNexus.Gateway.Api`.

1. Add `WorkspaceController` with the two GET endpoints defined above.
2. Inject `IAgentRegistry`, `IAgentWorkspaceManager`, `IFileSystem`.
3. Resolve workspace root via `IAgentWorkspaceManager.GetWorkspacePath(agentId)`.
4. Implement path containment check: resolve `path` param against workspace root, reject traversal.
5. Return directory listing or file content based on whether resolved path is a directory or file.
6. Cap file content at 512 KB; detect binary via BOM/null-byte heuristic.
7. Write `WorkspaceControllerTests` and `WorkspacePathSecurityTests` using `MockFileSystem`.
8. Write integration test with `WebApplicationFactory` and seeded workspace files.
9. Add response DTOs in `BotNexus.Gateway.Api/Models/`: `WorkspaceEntryDto`, `WorkspaceDirectoryResponse`, `WorkspaceFileResponse`.
10. **Security is critical** — the path traversal tests are blocking. Do not merge without adversary coverage.

### Fry (Frontend)

**Task:** Implement `WorkspacePanel`, `WorkspaceFileTree`, `WorkspaceFileViewer` Blazor components.

1. Add `GetWorkspaceAsync` to `IGatewayRestClient` and `GatewayRestClient`.
2. Create `WorkspacePanel.razor` — fetches workspace listing on init, manages tree/viewer state.
3. Create `WorkspaceFileTree.razor` — renders entries, handles expand/collapse, emits file-selected event.
4. Create `WorkspaceFileViewer.razor` — displays file content or empty/error state.
5. Replace the placeholder `<div>` in `AgentPanel.razor` Workspace section with `<WorkspacePanel AgentId="@AgentId" />`.
6. Add mobile CSS for single-pane workspace layout using existing breakpoint pattern.
7. Write bUnit tests for all three components.
8. Update `AgentPanelVerticalSliceTests` to verify workspace tab renders the real component.
9. **Depends on:** Bender's API contract (DTOs). Can stub API calls with NSubstitute initially.

### Hermes (Testing/QA)

**Task:** Review test coverage and adversary scenarios.

1. Verify backend path traversal tests cover: `../`, URL-encoded `%2e%2e%2f`, double-encoding, null bytes, Windows `..\\`, symlink escape.
2. Verify bUnit tests cover: loading, empty, populated, error, and mobile states.
3. Run full test suite after both Bender and Fry complete to catch integration gaps.
4. Flag any workspace files that should be excluded from the API (e.g., secrets, tokens).

### Amy (not needed this phase)

No design/UX work required — workspace file tree is a standard pattern. If custom styling is needed, Fry can handle it using existing CSS conventions.

---

# Bender Decision Note: Workspace Read API (Issue #245 Phase 2)

## Decision
Implemented backend **read-only** workspace API in Gateway:
- `GET /api/agents/{agentId}/workspace` returns a depth-limited tree.
- `GET /api/agents/{agentId}/workspace/{**path}` returns file metadata/content.

## Security / Validation
- Agent existence checked via `IAgentRegistry` (404 for unknown agents).
- Workspace root resolved via `IAgentWorkspaceManager`.
- Path jail enforcement uses `DefaultPathValidator` with workspace-only policy.
- Final target (symlink) path is resolved and revalidated to block escapes.
- Traversal/denied paths return 403; malformed input (absolute/null-byte) returns 400; missing file returns 404.

## Deferrals (Intentional)
- No create/update/delete APIs.
- No reports/canvas endpoints in this phase.

## Validation Snapshot
- `dotnet build BotNexus.slnx --nologo --tl:off` ✅
- `dotnet test tests/gateway/BotNexus.Gateway.Tests/BotNexus.Gateway.Tests.csproj --filter FullyQualifiedName~Workspace` ✅
- `dotnet test BotNexus.slnx --nologo --tl:off` ❌ existing unrelated failure in `LiveGatewayIntegrationTests.SignalRHub_ConnectsAndReceivesAgentList` (500 on agent register).

---

# Fry Decision Note: Workspace Tab UI (Phase 2)

**Date:** 2026-05-15  
**Scope:** Issue #245 frontend slice in `BotNexus.Extensions.Channels.SignalR.BlazorClient`

## Implemented

1. Replaced Workspace placeholder in `AgentPanel.razor` with `<WorkspacePanel AgentId="@AgentId" />`.
2. Added `WorkspacePanel`, `WorkspaceFileTree`, and `WorkspaceFileViewer` components for read-only browsing.
3. Added `IGatewayRestClient.GetWorkspaceAsync(...)` and `GatewayRestClient` implementation.
4. Added `WorkspaceContracts.cs` DTOs in the Blazor client.
5. Added mobile-first single-pane behavior (tree→viewer with back button) and desktop split-pane behavior.
6. Added safe long-content handling in viewer (server truncation flag + client-side preview cap).

## Explicit Deferrals Maintained

- Reports tab remains placeholder.
- Canvas tab remains placeholder.
- No workspace write operations (save/new/delete/upload/rename) in this PR.

## Coordination Notes

- Frontend assumes backend workspace API contract from Leela decision (`type`, `path`, `entries`, `content`, `size`, `encoding`, truncation/binary metadata).
- If backend final shape differs, only `WorkspaceContracts.cs` + mapping usage in `WorkspacePanel` should need adjustment.

---

# Hermes QA Decision Note: Workspace Phase 2 Tests

## Scope covered
- Backend API test coverage now explicitly exercises:
  - workspace directory tree listing
  - file content read
  - missing file handling
  - directory-as-file rejection
  - traversal/path escape rejection paths
- Frontend bUnit coverage now explicitly exercises:
  - workspace loading, empty, and error states
  - file selection and content display
  - workspace mobile CSS hooks and responsive pane selectors

## Validation run
- Targeted gateway workspace tests: PASS
- Targeted Blazor workspace/client tests: PASS
- Full validation:
  - `dotnet build BotNexus.slnx` PASS
  - `dotnet test BotNexus.slnx` PASS

## Remaining gaps / watch items
- URL-encoded traversal currently resolves as not-found in integration route tests rather than explicit forbidden in all encoded forms; direct traversal rejection remains covered at controller security tests.
- No binary file viewer UX behavior test in Blazor yet (backend returns binary metadata); consider adding once UX handling is finalized.

---

# Workspace Phase 2 Architecture Review — REJECTED

**Reviewer:** Leela  
**Date:** 2026-05-16  
**Branch:** `dev/botnexus/feature-portal-workspace-tab`  
**Scope:** Issue #245 Phase 2 (read-only workspace API + Workspace tab UI)

## Verdict: REJECTED — 2 blocking issues

### Blocking Issues

#### 1. REST Client URL Routing Mismatch (Critical)

**File:** `src/extensions/.../Services/GatewayRestClient.cs:150-152`

The frontend client sends file paths as a query parameter (`?path=readme.md`), but the backend route expects file paths as URL segments (`/workspace/readme.md`).

- Backend: `[HttpGet("{**path}")]` → matches `/api/agents/{id}/workspace/readme.md`
- Client: sends `/api/agents/{id}/workspace?path=readme.md` → hits the tree endpoint (`[HttpGet]`), not the file endpoint

**Impact:** File viewer will never load content in production. bUnit tests pass because they mock `IGatewayRestClient`, masking the integration failure.

**Fix:** Client must construct `/api/agents/{id}/workspace/{encodedPath}` for file requests (URL path segment, not query param).

#### 2. Frontend DTO Field Name Mismatch

**File:** `src/extensions/.../Services/WorkspaceContracts.cs:19`

- Frontend expects: `[JsonPropertyName("isTruncated")]`
- Backend serializes: `truncated` (from `public bool Truncated { get; set; }` with default camelCase)

**Impact:** Truncation indicator will always read as `null` on the client side.

### Passing Areas

| Area | Status |
|------|--------|
| Scope (no overreach into reports/canvas/write) | ✅ |
| Backend path traversal protection (`../`, absolute, NUL) | ✅ |
| Symlink jail (segment-by-segment resolution + CanRead) | ✅ |
| File-vs-directory semantics | ✅ |
| Gateway boundary (no extension deps in controller) | ✅ |
| UI read-only (no edit/delete/create controls) | ✅ |
| AgentPanel integration (Conversation tab unaffected) | ✅ |
| Mobile CSS hooks (show/hide tree/viewer panes) | ✅ |
| Test coverage (bUnit, unit, integration — all active) | ✅ |
| No skipped tests | ✅ |

### Recommended Revision

**Assign to: Bender** (owns the client service layer; Fry authored the UI components, not the REST wiring).

Fix the two blocking items:
1. `GatewayRestClient.GetWorkspaceAsync` → split into two URL patterns (tree vs file) matching backend routing
2. Align `WorkspaceContracts.cs` field name to `truncated`

After fix, run integration tests end-to-end with `WebApplicationFactory` + real HTTP to confirm wiring.

### Nice-to-Have (non-blocking)

- Add URL-encoded traversal integration test (e.g., `%2e%2e%2f`) for defense-in-depth documentation

---

# Bender Decision Note: Workspace Wiring + DTO Contract Fix (Reviewer Follow-up)

## Decision
Accepted Leela's rejection findings and corrected the runtime contract at both ends:
- Workspace client now requests subpaths using URL path segments (`/api/agents/{agentId}/workspace/{**path}`) rather than `?path=`.
- Gateway workspace path endpoint now returns directory payloads for directory targets (not only files), enabling lazy tree expansion through the same route shape.
- File truncation flag is now serialized as `isTruncated` so Blazor receives truncation state correctly.

## Security / Scope
- Kept existing path-jail and symlink-target validation (`DefaultPathValidator` + final-target check).
- No write capabilities added; API remains read-only.
- Traversal protections and denied-path behavior remain unchanged.

## Test Coverage Added
- Added client-to-controller integration test:
  - `WorkspaceRestClient_UsesControllerPathContract_AndReadsTruncationFlag`
  - Runs `GatewayRestClient` against `WebApplicationFactory<Program>` and validates:
    - directory load via `/workspace/{path}`
    - file load via `/workspace/{path}`
    - `isTruncated` DTO mapping on oversized file payload

## Validation Snapshot
- `dotnet test tests\gateway\BotNexus.Gateway.Tests\BotNexus.Gateway.Tests.csproj --nologo --tl:off --filter "FullyQualifiedName~Workspace"` ✅
- `dotnet test tests\extensions\BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests\BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests.csproj --nologo --tl:off --filter "FullyQualifiedName~Workspace|FullyQualifiedName~GatewayRestClient"` ✅
- `dotnet build BotNexus.slnx --nologo --tl:off` ✅
- `dotnet test BotNexus.slnx --nologo --tl:off` ✅ (after one transient rerun of `BotNexus.Memory.Tests`)
