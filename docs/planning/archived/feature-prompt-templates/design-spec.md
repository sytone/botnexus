---
id: feature-prompt-templates
title: "Feature: Prompt Templates"
type: feature
priority: medium
status: planning
created: 2026-07-26
---

# Feature: Prompt Templates

**Status:** planning
**Priority:** medium
**Created:** 2026-07-26

## Problem

No way to save and reuse pre-canned prompts for agents. Cron jobs require manually writing full prompt text each time. Users can't browse a library of parameterized templates.

## Vision

A prompt template system where:
- Agents can have saved prompt templates (per-agent or shared)
- Templates support parameters (variables substituted at invocation)
- Cron jobs can reference a template by name instead of inline prompt text
- Users can browse and invoke templates via `/prompts` command or web UI
- Templates stored as files in agent workspace (e.g., `prompts/` folder) or in a shared location

## Relationship to ask_user

`ask_user` becomes the execution primitive — when a template has parameters, the agent uses `ask_user` to collect them at runtime. Saved prompts + `ask_user` together enable interactive, reusable workflows.

## Design Considerations

- **Storage:** YAML/JSON files in `prompts/` folder per agent? Shared prompts at platform level?
- **Invocation:** CLI (`botnexus prompt run <name>`), cron (`promptRef` field), chat (`/prompt <name>`)
- **Parameters:** Mustache-style `{{param}}` with optional defaults and descriptions
- **Discovery:** `prompts/list` tool for agents, `/prompts` command for users, web UI browser
- **MCP alignment:** Consider aligning with MCP `prompts/list` + `prompts/get` protocol

## Open Questions

1. Should prompts be per-agent, shared, or both?
2. What format — YAML frontmatter + body, or pure JSON?
3. How do prompts interact with skills (skills are knowledge, prompts are invocations)?
4. Should the web UI have a prompt gallery/picker?

## Next Steps

- Finish `ask_user` tool (Phase 1-2) as the execution primitive
- Design the template format and storage convention
- Build the prompt resolution layer for cron jobs
