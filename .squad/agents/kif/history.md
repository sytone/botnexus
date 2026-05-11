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
