# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform built in C#/.NET
- **Stack:** C# (.NET latest), modular class libraries, dynamic extension loading, GitHub Pages
- **Created:** 2026-04-01

## Core Context

**Phases 1-12 Complete.** Kif owns developer experience, training materials, documentation.

**Key Delivered Documentation:**
- Phase 11 Wave 1: XML docs, module READMEs
- Phase 12 Wave 1: WebSocket channel docs
- Sub-agent spawning: 470-line feature doc
- DDD patterns: 20.5KB developer reference, type catalog (15+ value objects, 6 smart enums)
- Wave 4 DDD architecture: SessionParticipant, SubAgentArchetype, TriggerType patterns
- Extension-Contributed Commands: API reference (GET /api/commands, POST /api/commands/execute)
- OpenClaw Memory Alignment: Design questions resolution, spec updates

**Documentation Standards Applied:**
- Matched existing endpoint section format for new API docs
- Followed response JSON style conventions
- Consistent heading/code/config example formatting across all docs

## Learnings

- API endpoint documentation should include: purpose, request/response schema, multiple example scenarios, error handling semantics, and implementation notes.
- Command documentation: distinguish client-side vs backend execution, document collision handling, sub-command parsing.
- DDD documentation tracks phase/wave breakdown for traceability.
- Design question resolutions must be recorded with full decision context and implications for downstream waves.
- Prompt template documentation (PR #242): Storage locations are dual (config.json primary, ~/.botnexus/prompts/ file-based); parameter resolution uses {{name}} placeholders with 3-tier priority (caller → param defaults → simple defaults); required/optional status determined by presence of parameter metadata or placeholder in template body. Document both simple configs (for common use) and advanced parameter structures (for required validation).
- CLI subcommand documentation: Include options table, arguments table, multiple realistic examples showing common workflows, edge cases (e.g., required params missing), and integration points (e.g., cron jobs, custom gateways).
- Configuration guide sections benefit from JSON schema examples + human-friendly descriptions + workflow examples (e.g., file-based → config-based → CLI usage).

## Session: Prompt Template Documentation (2026-05-14)

**Outcome:** PR #242 documentation complete across CLI and configuration guides.

**Delivered:**
- `docs/cli-reference.md`: Added `## prompt` overview + 3 subcommand sections (list, render, run) with examples
- `docs/configuration.md`: Added "Prompt Templates" section, parameter resolution algorithm, 4 worked examples
- Decision merged to `decisions.md` (2026-07-29 entry)

**Key Decisions:**
- Dual storage documentation (config.json + files) — merge behavior explained
- Parameter substitution `{{name}}` syntax (case-sensitive, no nested placeholders)
- Cron integration example links existing scheduling documentation
- Documentation scope: basic + advanced patterns complete; marketplace/UI gallery deferred

**Commits:** 5e6deb76, 0380cce7 (pushed to PR #242)

