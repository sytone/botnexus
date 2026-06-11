# Built-in Agents

BotNexus ships with six built-in agent archetypes that are always available on every instance. These agents serve common roles and can be used as sub-agent targets without any configuration.

## Overview

Built-in agents are registered at startup before user-configured agents load. They:

- Inherit the spawning agent's model and provider (empty `ModelId` and `ApiProvider`)
- Have restricted tool sets appropriate to their role
- Can be targeted via `spawn_subagent(targetAgentId: "...")` or `agent_converse`
- Cannot be overridden by user configuration
- Appear at the bottom of agent dropdowns in the portal

## Available Archetypes

| ID | Emoji | Role | Description |
|----|-------|------|-------------|
| `researcher` | 🔍 | Research | Web search, URL fetch, and summarization. Read-only. |
| `coder` | 💻 | Code | Code writing, editing, building, and testing. Full file and shell access. |
| `planner` | 📋 | Planning | Issue decomposition, spec writing, and task breakdown. |
| `reviewer` | 🔎 | Review | Code review, PR analysis, and quality checks. Read-only shell access. |
| `writer` | ✍️ | Writing | Documentation, changelogs, summaries, and content creation. |
| `analyst` | 📊 | Analysis | Data analysis, log triage, and metrics. |

## Tool Access by Archetype

| Agent | Tools |
|-------|-------|
| `researcher` | `web_search`, `web_fetch`, `memory_search`, `memory_get`, `read`, `glob`, `grep` |
| `coder` | `read`, `write`, `edit`, `glob`, `grep`, `shell`, `exec`, `process`, `watch_file` |
| `planner` | `memory_search`, `memory_save`, `memory_get`, `web_search`, `read`, `write` |
| `reviewer` | `read`, `glob`, `grep`, `shell`, `web_fetch`, `memory_search` |
| `writer` | `read`, `write`, `edit`, `glob`, `grep`, `web_search`, `web_fetch`, `memory_search` |
| `analyst` | `read`, `glob`, `grep`, `shell`, `exec`, `web_fetch` |

## Usage Examples

### Spawning a sub-agent

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "targetAgentId": "researcher",
    "task": "Find the latest release notes for .NET 10"
  }
}
```

### Conversing with a built-in agent

```json
{
  "tool": "agent_converse",
  "parameters": {
    "agentId": "reviewer",
    "message": "Review the changes in PR #1234 for potential issues"
  }
}
```

### Using the archetype parameter

When spawning sub-agents, you can also use the `archetype` parameter to hint at behavior without targeting a specific agent:

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "archetype": "coder",
    "task": "Implement the FooBar interface"
  }
}
```

## Model Inheritance

Built-in agents have empty `ModelId` and `ApiProvider` fields. When spawned as sub-agents, they inherit the calling agent's model and provider context. This means:

- A sub-agent spawned by an agent using `claude-sonnet-4.5` will also use `claude-sonnet-4.5`
- No additional provider configuration is needed
- The cost is attributed to the spawning agent's provider account

## Related

- [Sub-Agent Spawning](/features/sub-agent-spawning) — How sub-agents work
- [Agents User Guide](/user-guide/agents) — Configuring custom agents
