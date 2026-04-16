# Research: Sub-Agent Spawning

## Problem Statement

Nova (and other agents) cannot spawn sub-agents for parallel or isolated work. The `subAgents` field exists in config but has no runtime implementation. This blocks:
- Delegating research tasks to background agents
- Running long operations without consuming the main session's context
- Using cheaper/faster models for specific sub-tasks
- Parallel work execution

## Current State in BotNexus

### Config Model (already exists)
```json
{
  "agents": {
    "nova": {
      "subAgents": [],          // Empty - no sub-agents configured
      "isolationStrategy": "in-process",
      "maxConcurrentSessions": 0
    }
  }
}
```

The `subAgents` array and `isolationStrategy` field suggest the data model was designed with this in mind, but runtime support does not exist yet.

### What Nova Can Do Today
- **Cron jobs**: Can schedule isolated tasks with their own sessions, but no parent-child relationship
- **Sessions tool**: Can list/view sessions but not create new ones programmatically
- **Exec/process**: Can run shell commands in background, but not LLM-powered agents

### What is Missing
1. A **tool** Nova can call to spawn a sub-agent session (e.g., `subagents` tool)
2. **Session linkage** - parent session knows about child sessions
3. **Completion notification** - child signals parent when done (push, not polling)
4. **Context isolation** - child gets its own context window
5. **Result passing** - child returns a summary to parent
6. **Model routing** - ability to use a different/cheaper model for sub-tasks

## Industry Research

### Claude Code Sub-Agents
Source: https://code.claude.com/docs/en/sub-agents

**Architecture:**
- Each subagent runs in its own context window with custom system prompt
- Defined as markdown files (like skills) with frontmatter for config
- Claude auto-delegates based on the subagent description
- Can run in **foreground** (blocking) or **background** (async)
- Built-in subagents: Explore, Plan, general-purpose

**Key features:**
- **Model selection**: Can use cheaper models (Haiku) for specific tasks
- **Tool restrictions**: Limit which tools a subagent can use
- **Skill preloading**: Inject specific skills into a subagent
- **MCP scoping**: Give subagents access to specific MCP servers only
- **Persistent memory**: Subagents can optionally share memory
- **Hooks**: before/after subagent events for quality gates

**Subagent definition (YAML frontmatter in markdown):**
```yaml
---
name: researcher
model: haiku
description: Handles research tasks by searching and reading files
tools: [read, grep, glob, web_search, web_fetch]
---
Research the topic thoroughly and return a concise summary.
```

### Claude Code Agent Teams
Source: https://code.claude.com/docs/en/agent-teams

- Experimental feature for multi-agent coordination
- One session acts as team lead, assigns tasks, synthesizes results
- Teammates work independently in their own context windows
- Can communicate directly with each other (not just through lead)
- Uses tmux for session management
- **Key difference from subagents**: Subagents are within one session; agent teams are separate sessions

### Windsurf
- No sub-agent system - uses a single Cascade agent with planning
- Offloads parallelism to "Worktrees" (git worktrees for parallel work)
- Context managed automatically, not by delegation

### Aider
- No sub-agent system - single-agent with model switching
- `/architect` mode uses 2 models (architect + editor) but not separate agents
- No parallel execution

### OpenClaw (from prior session source analysis)
- Had sub-agent spawning via `subagents` tool
- Supported `list`, `steer`, `kill` actions
- Sub-agents had their own sessions with completion events
- Parent agent received completion notification automatically
- Used `sessions_send` for cross-session messaging

## Key Design Decisions

### 1. Subagent Definition: Config vs Runtime
- **Claude Code approach**: Define as markdown files, discovered automatically
- **OpenClaw approach**: Runtime spawning with task description
- **Recommendation**: Support both - pre-defined agents in config + runtime spawning with inline instructions

### 2. Foreground vs Background
- **Foreground**: Blocks parent until complete, result injected into context
- **Background**: Parent continues, gets notified on completion
- **Recommendation**: Both, with background as default for BotNexus (matches async messaging model)

### 3. Model Routing
- Critical for cost management - research tasks on GPT-4.1, complex reasoning on Opus
- Should inherit parent provider but allow model override
- **Recommendation**: `model` parameter on spawn, defaulting to a configurable "subagent default model"

### 4. Context Passing
- Parent should pass a task description + optional context
- Child should NOT inherit parent full conversation history
- Child should return a structured result (summary text)
- **Recommendation**: Task string + optional file paths/context, returns text summary

### 5. Tool Access
- Sub-agents should have configurable tool access
- Default: same tools as parent minus sub-agent spawning (prevent infinite recursion)
- **Recommendation**: Configurable `tools` allowlist, with recursion depth limit

### 6. Completion Notification
- BotNexus already has session-based messaging infrastructure
- Completion should be push-based (like cron completion events)
- **Recommendation**: Auto-inject completion message into parent session

## Prior Art in BotNexus

The system prompt already references sub-agent patterns:
- "spawn a sub-agent" mentioned in AGENTS.md
- "Completion is push-based: it will auto-announce when done"
- "Do not poll subagents list / sessions_list in a loop"
- Cron jobs already create isolated sessions with completion events

This suggests the architecture was designed for sub-agents but the tool has not been implemented yet.
