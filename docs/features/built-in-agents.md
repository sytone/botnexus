# Built-in Agent Archetypes

BotNexus ships with six built-in **sub-agent archetypes** that shape how a spawned sub-agent behaves. They are **not** registered as named, conversational agents: they are spawn-time role profiles resolved from an internal catalog when you call `spawn_subagent(archetype: "...")`.

## Overview

An archetype is a role profile, not an agent identity. When you spawn a sub-agent with an archetype:

- The sub-agent **clones the spawning (parent) agent's descriptor**, inheriting the parent's model and provider.
- The archetype **restricts the sub-agent's tool set** to those appropriate for the role.
- No archetype agent configuration exists, and none is required.

Because archetypes are implementation-only roles, they are **not** valid `agent_converse` targets and **cannot** be used as `spawn_subagent(targetAgentId: "...")` targets. They also cannot be created, updated, or defined in configuration under their reserved ids (`researcher`, `coder`, `planner`, `reviewer`, `writer`, `analyst`). Attempting to `agent_converse` with an archetype id is rejected deterministically with guidance to use `spawn_subagent(archetype: ...)` instead.

## Available Archetypes

| Archetype | Role | Description |
|-----------|------|-------------|
| `researcher` | Research | Web search, URL fetch, and summarization. Read-only. |
| `coder` | Code | Code writing, editing, building, and testing. Full file and shell access. |
| `planner` | Planning | Issue decomposition, spec writing, and task breakdown. |
| `reviewer` | Review | Code review, PR analysis, and quality checks. Read-only shell access. |
| `writer` | Writing | Documentation, changelogs, summaries, and content creation. |
| `analyst` | Analysis | Data analysis, log triage, and metrics. |

## Tool Access by Archetype

| Archetype | Tools |
|-----------|-------|
| `researcher` | `web_search`, `web_fetch`, `memory_search`, `memory_get`, `read`, `glob`, `grep` |
| `coder` | `read`, `write`, `edit`, `glob`, `grep`, `shell`, `exec`, `process`, `watch_file` |
| `planner` | `memory_search`, `memory_save`, `memory_get`, `web_search`, `read`, `write` |
| `reviewer` | `read`, `glob`, `grep`, `shell`, `web_fetch`, `memory_search` |
| `writer` | `read`, `write`, `edit`, `glob`, `grep`, `web_search`, `web_fetch`, `memory_search` |
| `analyst` | `read`, `glob`, `grep`, `shell`, `exec`, `web_fetch` |

## Usage

### Spawning a sub-agent with an archetype

Use the `archetype` parameter to select the role. The sub-agent inherits your model and provider:

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "archetype": "coder",
    "task": "Implement the FooBar interface"
  }
}
```

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "archetype": "researcher",
    "task": "Find the latest release notes for .NET 10"
  }
}
```

### Mirroring a real named agent (not an archetype)

`spawn_subagent(targetAgentId: "...")` and `agent_converse(agentId: "...")` only accept **genuine registered named agents** — the agents that appear in `list_agents`. Archetype ids are never valid here.

## Model Inheritance

A sub-agent spawned with an archetype inherits the spawning agent's model and provider context. This means:

- A sub-agent spawned by an agent using `claude-sonnet-4.5` will also use `claude-sonnet-4.5`.
- No additional provider configuration is needed.
- Cost is attributed to the spawning agent's provider account.

## Related

- [Sub-Agent Spawning](/features/sub-agent-spawning) - How sub-agents work
- [Agents User Guide](/user-guide/agents) - Configuring custom agents
