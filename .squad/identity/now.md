---
updated_at: 2026-04-05T00:00:00Z
focus_area: CodingAgent — pi-mono Coding Agent Port
active_issues: []
status: planning
---

# What We're Focused On

**BotNexus.CodingAgent** — porting `@mariozechner/pi-coding-agent` to C#/.NET.

The coding agent is a standalone CLI built on `BotNexus.AgentCore` + `BotNexus.Providers.Core`. It provides built-in coding tools (read, write, edit, shell, glob), session management, extension loading, and an interactive REPL for developer workflows.

## Current Status

**Multi-sprint plan PROPOSED.** Awaiting approval to begin Sprint 1.

| Sprint | Focus | Owner | Status |
|--------|-------|-------|--------|
| Sprint 1 | Project scaffold + built-in tools | Farnsworth | ⏳ Pending |
| Sprint 2 | Agent factory + session runtime | Bender | ⏳ Pending |
| Sprint 3 | CLI + extension system | Bender | ⏳ Pending |
| Sprint 4 | Tests + documentation | Hermes + Kif | ⏳ Pending |

### Key Constraints
- Project at `src/coding-agent/BotNexus.CodingAgent/`
- References ONLY `BotNexus.AgentCore` and `BotNexus.Providers.Core`
- No references to archived projects or legacy Agent
- Extensible architecture — minimal core + extension loading

### What's Done (Prior)
- ✓ BotNexus.AgentCore — full pi-agent-core port (Agent, IAgentTool, events, loop)
- ✓ BotNexus.Providers.Core — LlmClient, model registry, streaming
- ✓ Provider abstraction layer — Copilot, OpenAI, Anthropic normalized

## Plan Document

`.squad/decisions/inbox/leela-coding-agent-plan.md` — 4 sprints, 35 work items, 3-4 week estimate.

## Team

Leela (Lead), Farnsworth (Platform Dev), Bender (Runtime Dev), Hermes (Tester), Kif (Documentation)
