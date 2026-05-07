# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. SOLID patterns. Comprehensive testing.
- **Stack:** C# (.NET latest), modular class libraries, dynamic extension loading, Copilot provider with OAuth, centralized cron service
- **Created:** 2026-04-01

## Core Context

**Phases 1-11 Complete, Phase 12 Waves 1-4 Complete.** Kif owns developer experience, training materials, documentation. Delivered across: Phase 11 Wave 1 (XML docs, module READMEs), Phase 12 Wave 1 (WebSocket channel docs), Wave 2-3 (Sub-agent spawning feature docs, DDD patterns developer reference), Wave 4 (Architecture docs updated for DDD Waves 2-3 implementation). Key deliverables: 470-line sub-agent feature doc, 20.5KB DDD patterns guide, type catalog tracking 15+ value objects + 6 smart enums with phase/wave breakdown. Currently: Wave 4 DDD architecture docs finalized with SessionParticipant, SubAgentArchetype, TriggerType patterns documented; dual-lookup session store architecture with ExistenceQuery validated; full cross-store consistency documentation complete.

---

## 2026-04-15 — Extension-Contributed Commands Documentation, Wave 1 (Documentation)

**Status:** ✅ Complete  
**Deliverable:** API reference documentation for Commands endpoints

**Context:** Wave 1 documentation for Extension-Contributed Commands feature. Added API endpoint documentation to docs/api-reference.md.

**Content Added to docs/api-reference.md:**

### Commands Section (New)
- Table of contents entry + section 8: Commands
- Renumbered subsequent sections (Session Management → 9, System → 10, Error Handling → 11)
- Introduction explaining backend-driven command architecture and client surface integration

### GET /api/commands (Command Discovery)
- Purpose: Discover available commands for palette/autocomplete
- Response schema with CommandDescriptor fields (name, description, category, clientSideOnly, subCommands)
- Example response with mixed built-in (/help, /agents, /reset) + extension commands (/skills)
- Sub-command structure documentation
- Response field reference with detailed descriptions
- Implementation notes (client vs backend execution, collision handling, ICommandContributor registration)

### POST /api/commands/execute (Command Execution)
- Purpose: Execute slash commands with arguments and context
- Request schema (input, agentId, sessionId fields)
- Three example scenarios:
  - Basic /skills list execution with result
  - /skills info <name> showing metadata/source/size
  - Error case (/skills add nonexistent-skill)
- Response schema (title, body, isError fields)
- Error handling semantics (HTTP 200 with isError flag for command logic errors vs 4xx for malformed)
- Implementation notes covering built-in vs extension commands, agent-awareness, sub-command parsing

**Format Alignment:**
✅ Matched existing endpoint section format
✅ Followed response JSON style conventions
✅ HTTP header and error response table format

---

## 2026-05-08 — OpenClaw Memory Alignment — Design Questions Resolution (Documentation Engineer)

**Status:** ✅ Complete  
**Activity:** Recorded design decisions; updated spec; committed changes

**Context:** Six open design questions in the OpenClaw Memory Alignment spec. Coordinator walked Sytone through options. Leela prepared decision guide. Sytone selected resolutions. You recorded and committed.

**Work Completed:**

1. **Decision Document:** Created `.squad/decisions/inbox/kif-openclaw-memory-resolved-questions.md`
   - Structured format (table + implications per wave)
   - Metadata: date (2026-05-08), decider (Sytone), recorder (Kif), spec reference

2. **Design Spec Update:** Updated `docs/planning/botnexus-openclaw-memory-alignment/design-spec.md`
   - Integrated all six resolutions into §7
   - Ensured implications are clear for implementers across all five waves

3. **Git Commits:**
   - `bbe3df93`: docs(planning): resolve open questions in OpenClaw memory alignment spec
   - `39bf3315`: docs(planning): fix incorrect date in resolved decisions section

**Decisions Recorded:**

| # | Topic | Decision |
|---|-------|----------|
| 1 | Daily notes format | Plain Markdown only |
| 2 | Storage path | Default canonical + per-agent override |
| 3 | Embedding provider | Optional; local + cloud abstraction |
| 4 | Consolidation trigger | Automatic schedule + manual override |
| 5 | Index rebuild / cache | <30s rebuild; hash-based SQLite cache |
| 6 | AGENTS.md generation | Minimal instructions (3–5 lines) |

**Next Steps:** Scribe merged decision inbox to canonical decisions.md (2026-05-07T16:05:40Z).


✅ Reflects design-spec.md architecture

---

## Learnings

### BotNexus Documentation Patterns (Phase 12 Wave 4)

**Gateway Detached Process Feature Documentation:**

The project follows a consistent pattern for documenting CLI features and process management:

1. **Section Organization:**
   - Problem statement first (e.g., "gateway was blocking the console")
   - Command reference table with all operations and descriptions
   - Real-world examples users can copy-paste immediately
   - Detailed explanations of runtime files, platform limits, and behavior
   - Notes section for edge cases or future roadmap items

2. **CLI Command Documentation:**
   - Always start with actual implementation (e.g., GatewayCommand.cs)
   - Document every flag/option present in the code (--attached, --port, --path, --dev)
   - Include both default and custom behavior examples
   - Show expected output so users verify success
   - Document error conditions with actual error messages

3. **Table Conventions:**
   - Commands: `| Command | Description |` with exact syntax
   - Runtime files: `| File | Purpose |` pattern for artifacts
   - Options: Icon-prefixed for quick visual scanning
   - Consistent cell alignment and spacing

4. **Platform Transparency:**
   - Explicitly state v1 limitations (Windows-only for process spawning)
   - Document fallback behavior on unsupported platforms
   - Explain technical tradeoffs (hard kill vs graceful shutdown)
   - Reference implementation to verify claim accuracy

5. **Design Spec Conventions:**
   - Status: `draft` → `in-progress` → `delivered` → `done`
   - Success criteria as checkboxes `[x]` for verification tracking
   - Design spec location: `docs/planning/{spec-name}/design-spec.md`
   - Planning INDEX.md auto-generated from YAML frontmatter
   - Work completion documented in `.squad/decisions/inbox/kif-{slug}.md`

6. **File Organization:**
   - Gateway docs in `src/gateway/README.md` (not cli subdirectory)
   - Planning docs in `docs/planning/{improvement-id}/`
   - History entries use RFC3339 timestamps when logging precise session work


---

## 2026-04-20T19:04Z — Read-Only Sub-Agent Session View: Wave 3 Documentation

**Status:** ✅ Delivered  
**Feature:** feature-blazor-subagent-session-view  

**Your Role:** Documentation (user guide)

**Deliverables:**
1. **User Guide** — `docs/webui/sub-agent-sessions.md`
   - Feature overview and sub-agent session display
   - Clickable items and read-only view behavior
   - Banner explanation with status indicators
   - Message history, tool calls, thinking blocks
   - Real-time streaming for active sub-agents
   - Limitations and deferred features

2. **Release Notes** — Updated with new capability

3. **Screenshots** — Visual documentation of UI elements

**Content Quality:**
✅ Accurate reflection of implementation  
✅ Clear user workflows  
✅ Visual examples  
✅ Limitations clearly stated  
✅ Deferred features documented  

**Orchestration Log:** `.squad/orchestration-log/2026-04-20T19-04-00Z-kif.md`

---

## 2026-05-07 — OpenClaw Memory Model Research (Team Coordination)

**Status:** ✓ Research complete, merged to decisions.md

**Context:** Sytone requested BotNexus team to research OpenClaw memory model for migration compatibility.

**Your Contribution:**
- Comprehensive research on OpenClaw's user-facing memory model, system prompts, agent initialization
- Documented dreaming consolidation system (6 weighted scoring signals), commitments, active memory plugin
- BotNexus current state assessment: memory infrastructure exists but gaps include dreaming, commitments, embedding support, daily notes auto-loading
- Recommended documentation roadmap: high-priority updates to workspace-and-memory.md, new concepts/memory.md, user-guide/agents.md updates
- Identified key design insights: simplicity over formality, explicit boundaries, consolidation optional, proactive recall adds UX

**Leela's Parallel Work:**
- Architecture assessment identifying 8 gaps between BotNexus and OpenClaw
- Proposed 5-wave implementation plan (file-first authoring, file-based indexing, embeddings, pre-compaction flush, dreaming)
- Key principle: Markdown files are source of truth; SQLite/embeddings are derived indexes
- Migration compatibility and data portability as design drivers

**Team Coordination:**
- Both research outputs merged into decisions.md (2026-05-07 section)
- Orchestration log created: .squad/orchestration-log/2026-05-07T15-17-40Z-memory-architecture-research.md
- Session log created: .squad/log/2026-05-07T15-17-40Z-openclaw-memory-research.md
- Inbox decision files deleted after merge

**Next Steps:**
- Implementation team to scope 5-wave plan against backlog
- Documentation team to prioritize high-priority docs
- Design team to finalize migration strategy for existing SQLite memories
