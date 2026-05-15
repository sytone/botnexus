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

## Session: Prompt Template Sample Files (2026-05-15)

**Outcome:** Sample prompt files added to repo for user reference; documentation updated to guide discovery.

**Delivered:**
- `prompts/sample-simple-greeting.prompt.json`: Minimal template with single parameter
- `prompts/sample-advanced-status-report.prompt.json`: Advanced template with parameter metadata, defaults, required flags
- `docs/configuration.md`: Added reference to sample templates in Storage Locations section
- `docs/cli-reference.md`: Added "Getting Started" note pointing to sample files

**Key Decisions:**
- Sample files live at repo root `/prompts/` to mirror `~/.botnexus/prompts/` user directory structure
- Naming convention `sample-*.prompt.json` clearly identifies files as templates users can copy and customize
- Simple example shows minimal viable template (name, prompt, description); advanced example showcases full feature set (parameters metadata with descriptions, required flags, defaults)
- Documentation links users to samples from both CLI and configuration guides for discoverability

**Commits:** c5063654 (pushed to PR #242)


## Recent Work

**2026-05-14 — PR 243 Service Bus Channel Docs Correction**
- Updated `docs/user-guide/channels/service-bus.md` and `service-bus-envelope.md`
- Referred to `~/.botnexus/config.json` instead of YAML configuration examples
- Removed manual NuGet package installation and `add-package` instructions
- Removed C# code registration guidance (builder.WithChannel pattern)
- Clarified BotNexus CLI-based deployment and configuration flow
- Validated: no stale YAML/NuGet/C# registration references remain
- Key learning: Service Bus extension documentation should describe CLI deployment and `~/.botnexus/config.json`, not manual package installation or C# registration code
