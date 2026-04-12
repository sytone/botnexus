# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. SOLID patterns. Comprehensive testing.
- **Stack:** C# (.NET latest), modular class libraries, dynamic extension loading, Copilot provider with OAuth, centralized cron service
- **Created:** 2026-04-01

## Core Context

**Phases 1-11 Complete, Phase 12 Wave 1 Initiated.** Kif owns developer experience, training materials, documentation. Phase 12 Wave 1 assignment: WebSocket channel README (P0). Created lifecycle skill, dev guide, deployment scripts. Built OpenAPI spec export infrastructure. Manages project documentation and knowledge transfer. Currently: protocol docs, API reference updates, config reference, architecture diagrams queued for Wave 2-3.

---

## Session: Phase 3 Port Audit Design Review (2026-04-05T09:49:50Z)

Participated in design review ceremony for Phase 3 architecture. All ADs approved (9–17):
- **AD-9** DefaultMessageConverter → Farnsworth
- **AD-10** --thinking CLI + /thinking command → Bender  
- **AD-11** ListDirectoryTool → Bender
- **AD-12** ContextFileDiscovery → Bender
- **AD-14** session metadata entries → Bender
- **AD-15** ModelRegistry utilities → Farnsworth
- **AD-17** /thinking slash command → Bender
- **AD-13** deferred (OpenRouter routing types, no provider yet)
- **AD-16** already present (maxRetryDelayMs)

**Orchestration logs:** .squad/orchestration-log/2026-04-05T09-49-50Z-{agent}.md

**Session log:** .squad/log/2026-04-05T09-49-50Z-port-audit-phase-3.md

**Boundaries:** AgentCore ↔ CodingAgent (DefaultMessageConverter), CodingAgent ↔ Session (MetadataEntry), Providers.Core (ModelRegistry utilities).

**Next:** Parallel execution tracks. Farnsworth + Bender begin implementation. Kif writes training docs. Nibbler runs consistency review.


---

## 2026-04-10T16:30Z — Sub-Agent Spawning Feature: Wave 4 Documentation (Documentation)

**Status:** ✅ Complete  
**Commit:** ad72475

**Your Role:** Documentation. Feature documentation.

**Deliverables:**
- 470-line feature documentation (`docs/features/sub-agent-spawning.md`)
  - User stories (delegation, cost optimization, visibility, result delivery, safety)
  - API reference (endpoints, WebSocket events, tool schemas)
  - Configuration reference (`SubAgentOptions`)
  - Architecture overview (relationship to existing `CallSubAgentAsync`)
  - Security considerations (tool scoping, depth limits, resource protection)
  - Example scenarios
- 7 DRAFT markers for domain review:
  - Model override behavior with default fallback
  - Completion delivery timing with parent idle vs running
  - Tool scoping inheritance vs explicit allowlist
  - Session metadata tagging for cleanup queries
  - Error handling for timeout vs maxTurns simultaneous
  - Foreground mode Phase 2 planning
  - Cost tracking integration roadmap

**Integration:**
- API reference updated with new endpoints
- Tool documentation aligned with existing patterns
- Architecture docs updated with sub-agent lifecycle diagram

---

## 2026-04-05T11:52:58Z — Sprint 4 Documentation Delivery

**Status:** ✅ COMPLETE  
**Timestamp:** 2026-04-05T11:52:58Z  
**Orchestration Log:** .squad/orchestration-log/2026-04-05T11-52-58Z-kif.md

**Your Deliverables (Kif — Documentation):**

1. **Training Modules Updated (5 modules):**
   - Agent Loop Training: Queue state visibility + listener error handling patterns
   - Provider Architecture: Provider decomposition + JSON standardization
   - Tool Implementation: EditTool diff strategy + ShellTool platform handling
   - Message Lifecycle: MessageStartEvent/MessageEndEvent timing clarifications
   - Testing Patterns: New test cases for functionality

2. **New Content:**
   - Changelog Module: Sprint 4 summary (7 P0 decisions, 18+ P1 items, 16 new tests)

3. **Audience:**
   - Developers: Implementation patterns for each decision
   - Reviewers: Decision rationale and validation evidence
   - Contributors: Up-to-date architecture reference

**Build Status:** ✅ All documentation built and validated

**Next Phase:** Documentation ready for release notes and contributor onboarding.

---

## Dev Loop Documentation Overhaul

**Status:** ✅ Complete  
**Scope:** Full audit and update of dev loop docs, scripts, and getting-started guides.

**Issues Found & Fixed:**

1. **`scripts/install-pre-commit-hook.ps1`** — Path bug: used `$PSScriptRoot` (resolves to `scripts/`) for `.git/hooks/` path, which put the hook in `scripts/.git/hooks/` instead of repo root `.git/hooks/`. Fixed to use `Split-Path -Parent $PSScriptRoot` pattern. Also fixed reference to non-existent `tests/BotNexus.Tests.Unit/` — changed to `tests/BotNexus.Gateway.Tests` which is the actual test project.

2. **`docs/dev-loop.md`** — Rewrote entirely. Added: quick start section, full dev loop diagram (edit→build→test→run→verify), project structure table for edit targets, `-SkipBuild` and `-SkipTests` parameters for `dev-loop.ps1`, `-SkipBuild` for `start-gateway.ps1`, `export-openapi.ps1` reference, live testing with Copilot section (OAuth flow + WebSocket testing), auth.json documentation, environment variables table, expanded troubleshooting.

3. **`docs/development-workflow.md`** — Removed phantom references to `scripts/pack.ps1` and `scripts/install.ps1` (don't exist), fixed `BotNexus.Agent.Tests` → `BotNexus.AgentCore.Tests`, fixed port 18790 → 5005, fixed `gateway.log` → `botnexus-*.log`, removed stale CLI install section.

4. **`docs/getting-started-dev.md`** — Fixed missing section 4 (skipped from 3 to 5), renumbered sections 4–12, fixed `dotnet run --project src/gateway/BotNexus.Cli -- validate` (CLI project is at `src/gateway/BotNexus.Cli/`, but validation via API is more reliable).

5. **`docs/dev-guide.md`** — Added `-SkipBuild` and `-SkipTests` to `dev-loop.ps1` parameter table, added `-SkipBuild` to `start-gateway.ps1` table, added fast restart example.

**Verified:** Build succeeds (0 warnings, 0 errors). All documented file paths confirmed against BotNexus.slnx and actual directory structure.

---

## 2026-04-06 — XML Doc Comments + Dev Loop Documentation

**Status:** ✅ Complete  
**Scope:** Add XML documentation to Gateway API + verify dev loop docs + create module READMEs

**Deliverables:**

1. **XML Doc Comments (Gateway API)**
   - Added 14 XML documentation comments across 6 files in `src/gateway/BotNexus.Gateway.Api/`:
     - `AgentsController.cs` — constructor documentation
     - `ChatController.cs` — constructor documentation
     - `SessionsController.cs` — constructor + SessionHistoryResponse record documentation
     - `GatewayAuthMiddleware.cs` — class, constructor, and InvokeAsync method documentation
     - `ActivityWebSocketHandler.cs` — HandleAsync method documentation
     - `Program.cs` — Program partial class documentation
   - All comments enable Swagger documentation generation and IntelliSense support
   - Verified with `/p:TreatWarningsAsErrors=true` — build succeeds with 0 CS1591 errors

2. **Dev Loop Documentation Audit**
   - Verified all references are current: ports (5005), file paths, test projects
   - Added missing test project: `tests/BotNexus.Providers.Conformance.Tests` to dev-loop.md test table
   - Confirmed no stale gateway port references (no 18790 found)
   - All dev scripts (dev-loop.ps1, start-gateway.ps1, export-openapi.ps1, install-pre-commit-hook.ps1) documented and working

3. **Module READMEs (Gateway Sub-Projects)**
   - Created `src/gateway/BotNexus.Gateway/README.md` (146 lines)
     - Documents orchestration runtime, key services (AgentSupervisor, MessageRouter, ActivityBroadcaster)
     - Covers configuration watching, thread safety patterns, extension points
   - Created `src/gateway/BotNexus.Gateway.Api/README.md` (198 lines)
     - Documents all REST endpoints (/api/agents, /api/chat, /api/sessions)
     - Documents WebSocket endpoints and activity stream
     - Details authentication middleware and CORS configuration
   - Created `src/gateway/BotNexus.Cli/README.md` (197 lines)
     - Documents all CLI commands: init, validate, agent list/add/remove, config get/set
     - Includes common workflows and troubleshooting guide
     - Covers environment variables and exit codes
   - All READMEs follow consistent template: overview, key types, usage examples, configuration, dependencies

**Build Verification:**
- Full solution builds: ✅ 0 errors, 15 pre-existing warnings
- Gateway API project with TreatWarningsAsErrors=true: ✅ 0 errors
- Test suite: ✅ 312 Gateway tests pass, 146 Coding Agent tests pass, all provider tests pass
- Git commits: 2 commits (5a169ba module READMEs, plus prior xml-docs work)

**Cross-References Established:**
- Each module README cross-references related modules
- Dev documentation links to new module READMEs
- API reference up-to-date with conformance test inclusion

## 2026-04-06T07:50:00Z — Phase 11 Wave 1: Documentation & XML Comments

**Status:** ✅ Complete  
**Agents:** Farnsworth (Config/Schema), Bender (Extension Loading), Hermes (Testing), Kif (Docs)

**Documentation Work (Kif):**
- Added 14 XML doc comments to Gateway.Api public types
- CS1591 warnings: 0 (0 remaining)
- Created 3 module READMEs (Gateway, Gateway.Api, Cli)
- Updated dev-loop.md with conformance test project
- Build warnings: 0 (down from 39)
- Commits: 5a169ba, 6f602e9

**Cross-Team Results:**
- Farnsworth: Config schema generation, path resolution, CLI integration
- Bender: Dynamic extension loading with IExtensionLoader interface
- Hermes: 23 new tests achieving full coverage of new subsystems
- **Total:** 891 tests passing (868→891, +23), Build clean, 0 warnings

- Created `src/channels/BotNexus.Channels.WebSocket/README.md` — the last channel project without documentation. Covers full WebSocket message protocol (6 inbound types: message, reconnect, abort, steer, follow_up, ping; 10 outbound types: connected, message_start, thinking_delta, content_delta, tool_start, tool_end, message_end, error, pong, reconnect_ack), sequence ID tracking, reconnection replay flow with ASCII diagram, all 5 capability flags (streaming, steering, follow-up, thinking display, tool display), GatewayWebSocketOptions configuration table, JavaScript usage examples, and Gateway integration component map. All content verified against WebSocketChannelAdapter, GatewayWebSocketHandler, WebSocketMessageDispatcher, and WebSocketConnectionManager source code. Commit: 04e8da3.

---

## 2026-04-11 — Sub-Agent Spawning Feature Documentation (Draft)

**Status:** ✅ COMPLETE (Draft — pending implementation finalization)

Created docs/features/sub-agent-spawning.md — comprehensive feature documentation covering:
- Quick start with minimal spawn example
- Architecture: parent/child session model, relationship to existing IAgentCommunicator.CallSubAgentAsync()
- Tools reference: spawn_subagent, list_subagents, manage_subagent with full parameter tables, return values, and examples
- Configuration reference: SubAgentOptions fields with defaults
- Completion flow: how results deliver back to parent via FollowUpAsync and PendingMessageQueue
- Security: tool scoping, no workingDir, recursion prevention, session isolation, ownership enforcement
- Phase 1 limitations: no steer, no foreground, depth limit = 1
- API endpoints and WebSocket events
- 7 DRAFT markers for sections requiring implementation verification

All interface names cross-referenced against Leela's design review (leela-subagent-design-review.md). Updated README.md Getting Started table with link to new feature doc. Created docs/features/ directory (first feature doc in this location). Commit: ad72475.

## 2026-04-12 — DDD Patterns Developer Reference Guide (Wave 1)

**Status:** ✅ COMPLETE  
**Commit:** (post-commit pending — documentation only)

Created `docs/development/ddd-patterns.md` — comprehensive developer reference documenting value object and smart enum patterns from Leela's DDD design review.

**Content (20.5 KB markdown):**

1. **Introduction** — Problem statement: primitive obsession (raw strings for domain concepts), lack of type safety (duplicated logic across 13 projects), domain language misalignment (string concat for identities). Solution: value objects (AgentId, SessionId, ChannelKey) + smart enums (MessageRole, SessionType).

2. **Value Object Pattern** — `readonly record struct` with:
   - When to use (domain concepts with validation, high-ROI targets like AgentId P0, ChannelKey P0)
   - Code pattern with validation factory `From()`, implicit/explicit operators
   - JsonConverter implementation (Read() calls From() for validation, Write() outputs string)
   - Conversion semantics: implicit for migration phase (backward compat), explicit for new code (type safety)
   - Dos/Don'ts (validate in factory, no business logic, no mutators, round-trip serialization)

3. **Smart Enum Pattern** — `sealed class` with:
   - When to use (discriminators, plugin-extensible types like MessageRole, SessionType)
   - Code pattern with static instances, `ConcurrentDictionary<string, T>` registry, `FromString()` factory
   - Registry-based extensibility: plugins call `FromString("custom-value")` at init time, registry stores + retrieves
   - Thread safety: `ConcurrentDictionary.GetOrAdd()` guarantees single instance per value even under concurrent calls
   - Extension registration flow for plugins (no core code modifications needed)

4. **Migration Guide** — 5-step additive process:
   - Step 1: Add new type alongside old (no breaking changes)
   - Step 2: Use implicit conversion (existing tests compile without changes)
   - Step 3: Update producers (API callers)
   - Step 4: Update consumers (API implementations)
   - Step 5: Remove old type (Wave 2-3 timeline)

5. **Type Catalog** — Table tracking all 15+ value objects + 6 smart enums:
   - **Phase 1 (Wave 1):** AgentId, SessionId, ChannelKey, ConversationId, SenderId, AgentSessionKey, ToolName (value objects); MessageRole, SessionStatus, SessionType, ExecutionStrategy (smart enums)
   - **Phase 2 (Wave 2-3):** SessionParticipant, SubAgentArchetype, TriggerType
   - **Future:** World (YAGNI), AgentCapabilities

**Cross-references:**
- design-spec.md (specification)
- research.md (primitive obsession analysis)
- leela-ddd-design-review.md (decisions D1-D8, wave plan, risk analysis)
- docs/architecture/ (system design)

**Key design decisions documented:**
- D1 preserved: `readonly record struct` + JsonConverter ensures serialization safety
- D2 preserved: implicit conversion allows ~2,000 tests to migrate incrementally
- Both patterns verified against design review source material

**Audience:**
- Developers implementing Phase 1 (Farnsworth will use this as reference for BotNexus.Domain)
- Reviewers (code pattern contracts are documented)
- Plugins/extensions (extensibility model explained)

## Learnings

- BotNexus docs use numbered sections (## 1. Overview) with matching TOC anchors (#1-overview). Anchors must include the section number prefix.
- Feature docs should live under docs/features/ — this is a new convention established with sub-agent spawning.
- Design review documents in .squad/decisions/inbox/ are the authoritative source for interface names and architectural decisions.
- Developer reference guides for architectural patterns should live in docs/development/ — new convention for Wave 1 DDD patterns guide.
- Value object and smart enum patterns are best explained via concrete code examples + decision rationale (why `readonly record struct` over regular record? → stack allocation + immutability).
- Migration guides should be explicit about timing: implicit conversion during Phase 1, explicit conversion in Phase 2+. This manages team expectations about when old code patterns are retired.
- Type catalogs with phase/wave breakdown help teams parallelize work (Wave 1 types are immediate dependencies, Wave 2-3 types are sequenced by dependencies).
- **Wave 2-3 Summary:** Documentation must reflect completed DDD implementation. Update type catalogs to show status (✅ Done vs. Planned), add new sections for Wave 2-3 patterns (e.g., SessionParticipant, SubAgentArchetype, TriggerType, IInternalTrigger), and update architecture sections that reference changed models (Session, Cron).
- Backward compatibility is critical when documenting DDD changes: the `CallerId` field remains on `GatewaySession` even though `Participants` is canonical. Implicit operators on value objects let existing tests compile without modification. Document both the new pattern and the legacy path.
- Session model redesign (Wave 2-3) includes domain language improvements: "Closed" → "Sealed" reflects semantics better (sealed = terminal and preserved, not deleted). This distinction matters in documentation because it affects team mental model of session lifecycle.
