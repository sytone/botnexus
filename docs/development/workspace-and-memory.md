# Agent Workspace and Memory Model

**Version:** 1.0  
**Last Updated:** 2026-04-02  
**Lead Architect:** Leela

---

## Table of Contents

1. [Agent Workspace Overview](#agent-workspace-overview)
2. [Workspace Location and Structure](#workspace-location-and-structure)
3. [Workspace Files Reference](#workspace-files-reference)
4. [Memory Model](#memory-model)
5. [Memory Tools](#memory-tools)
6. [Memory Consolidation](#memory-consolidation)
7. [Configuration Reference](#configuration-reference)
8. [Context Builder](#context-builder)
9. [Examples](#examples)
10. [First-Run Behavior](#first-run-behavior)

---

## Agent Workspace Overview

An **agent workspace** is a persistent file system directory where each BotNexus agent stores its identity, personality, values, user preferences, and memory. It separates durable agent state (who am I, what do I remember) from transient session data (conversation history, logs).

### Design Rationale

- **Autonomy**: Each agent has its own workspace, independent of other agents and deployments
- **Persistence**: Workspace survives agent restarts and redeployments
- **Accessibility**: Files are plain Markdown — human-readable and editable
- **Composability**: Workspace files form the foundation of the system prompt assembled per session
- **Separation of Concerns**: Distinct from `~/.botnexus/workspace/sessions/` (transient conversation history) and `~/.botnexus/extensions/` (deployed binaries)

---

## Workspace Location and Structure

All agent workspaces are stored under the BotNexus home directory:

```text
~/.botnexus/agents/{agent_name}/
├── SOUL.md                    (Core personality, values, boundaries)
├── IDENTITY.md                (Role, communication style, constraints)
├── USER.md                    (User preferences and collaboration expectations)
├── AGENTS.md                  (Auto-generated: multi-agent awareness)
├── TOOLS.md                   (Auto-generated: available tools)
├── HEARTBEAT.md               (Periodic tasks and memory consolidation cadence)
├── MEMORY.md                  (Long-term distilled memory)
└── memory/
    └── daily/
        ├── 2026-04-01.md
        ├── 2026-04-02.md
        └── ... (one per day)
```

### Path Resolution

- **Base Home**: `~/.botnexus/` (or `$BOTNEXUS_HOME` if set)
- **Agents Directory**: `~/.botnexus/agents/`
- **Agent Workspace**: `~/.botnexus/agents/{agent_name}/`

The `~/.botnexus/` prefix is expanded by `BotNexusHome.ResolvePath()` to the user's profile directory:
- **Windows**: `C:\Users\{user}\.botnexus\`
- **macOS/Linux**: `/home/{user}/.botnexus/`

---

## Workspace Files Reference

### SOUL.md — Core Personality and Values

**Purpose**: Define the agent's fundamental personality, core values, ethical boundaries, and operating principles.

**Loaded Into**: System prompt (every session)

**Content Guidelines**:
- Concise personality description (1-3 sentences)
- Core values (list 3-5 key values)
- Hard boundaries (what the agent will never do)
- Guiding principles for decision-making

**Example**:
```markdown
# Soul

## Personality
You are a helpful, pragmatic software architect with strong opinions on SOLID principles and test-driven development. You communicate directly and without unnecessary jargon.

## Core Values
- Clarity over cleverness
- Empirical evidence over theory
- User needs over personal preference
- Technical debt visibility

## Boundaries
- Never suggest architectural changes without understanding the problem first
- Never recommend approaches that compromise long-term maintainability for short-term speed
- Never ignore security implications

## Principles
- Lead by example with well-documented code
- Prefer proven patterns over novel solutions
- Prioritize team understanding over individual brilliance
```

**Technical Details**:
- File: `{workspace}/SOUL.md`
- Encoding: UTF-8 (no BOM)
- Max chars included in system prompt: per `MaxContextFileChars` config (default: 8000)

### IDENTITY.md — Role and Operating Constraints

**Purpose**: Describe the agent's professional role, communication style, and operating constraints.

**Loaded Into**: System prompt (every session)

**Content Guidelines**:
- Role title and primary responsibility
- Communication style and tone
- Operating hours/timezone
- Key skills and expertise areas
- Known limitations or constraints

**Example**:
```markdown
# Identity

## Role
Lead/Architect for BotNexus platform. You own architectural decisions, code review for platform changes, and mentorship of team members.

## Communication Style
- Concise and direct
- Provide evidence and examples
- Ask clarifying questions before committing
- Document decisions and trade-offs

## Expertise
- .NET / C# architecture
- Distributed systems
- SOLID design patterns
- Test-driven development

## Constraints
- Monday-Friday, 8am-6pm Pacific Time
- Focus on core platform (avoid distraction by adjacent projects)
- Escalate security concerns immediately
```

**Technical Details**:
- File: `{workspace}/IDENTITY.md`
- Encoding: UTF-8 (no BOM)
- Max chars included in system prompt: per `MaxContextFileChars` config (default: 8000)

### USER.md — User Preferences and Collaboration Expectations

**Purpose**: Capture the human user's preferences, priorities, and collaboration expectations.

**Loaded Into**: System prompt (every session)

**Content Guidelines**:
- User name and role
- Working preferences (async/real-time, documentation style)
- Decision-making priorities
- Escalation paths
- Known timezone or availability

**Example**:
```markdown
# User

## About
Jon Bullen, Product Owner and Technical Lead for BotNexus.

## Working Preferences
- Prefer written communication and documentation over sync meetings
- Async-first: decisions documented in GitHub issues/PRs
- Appreciate concise summaries with links to detailed work
- Timezone: Pacific Time (PT)

## Priorities
1. User experience and developer ergonomics
2. Security and data privacy
3. Operational simplicity
4. Performance optimization (only when justified)

## Escalation Path
- Architecture decisions: documented in decisions.md
- Security concerns: immediate email + PR
- Blocker issues: Slack #botnexus-blockers

## Communication Preferences
- Bold claims with evidence
- Propose alternatives for significant changes
- Explain trade-offs explicitly
```

**Technical Details**:
- File: `{workspace}/USER.md`
- Encoding: UTF-8 (no BOM)
- Max chars included in system prompt: per `MaxContextFileChars` config (default: 8000)

### AGENTS.md — Multi-Agent Awareness (Auto-Generated)

**Purpose**: Automatically generated reference of all configured agents, their models, roles, and providers.

**Loaded Into**: System prompt (every session)

**Auto-Generation**:
- Generated by `AgentContextBuilder.GenerateAgentsMarkdown()` during system prompt assembly
- Reads from `BotNexusConfig.Agents` (both default agent and named agents)
- Includes model, role (from SystemPrompt/SystemPromptFile), and provider if configured
- Regenerated every session — always current

**Example Output**:
```markdown
## AGENTS.md

Configured agents:

### default
- Model: gpt-4-turbo
- Role: default agent

### leela
- Model: gpt-4-turbo
- Role: Lead/Architect for BotNexus platform. You own architectural decisions...
- Provider: copilot

### farnsworth
- Model: claude-3-opus
- Role: from /extensions/loader/spec.md
```

**Technical Details**:
- File: `{workspace}/AGENTS.md` (exists but is replaced at session start)
- Not manually edited (regenerated each session)
- Max chars included in system prompt: per `MaxContextFileChars` config (default: 8000)

### TOOLS.md — Available Tools (Auto-Generated)

**Purpose**: Automatically generated reference of all tools available to the agent.

**Loaded Into**: System prompt (every session)

**Auto-Generation**:
- Generated by `AgentContextBuilder.GenerateToolsMarkdown()` during system prompt assembly
- Reads from `ToolRegistry.GetDefinitions()` (all registered tools)
- Sorted alphabetically by tool name
- Regenerated every session — always current

**Example Output**:
```markdown
## TOOLS.md

- exec: Execute a shell command on the local machine
- fetch: Retrieve content from a URL
- memory_get: Read long-term memory or a specific daily notes file
- memory_save: Save information to long-term memory or today's daily notes
- memory_search: Search across long-term memory and daily notes
- web_search: Search the web using a search engine
```

**Technical Details**:
- File: `{workspace}/TOOLS.md` (exists but is replaced at session start)
- Not manually edited (regenerated each session)
- Max chars included in system prompt: per `MaxContextFileChars` config (default: 8000)

### HEARTBEAT.md — Periodic Tasks and Memory Consolidation

**Purpose**: Define periodic instructions for the agent (memory consolidation cadence, integrity checks, recurring cleanup tasks).

**Loaded Into**: System prompt (every session) — for reference by memory consolidation logic

**Content Guidelines**:
- Memory consolidation interval (hours)
- Consolidation trigger conditions
- Integrity checks to run periodically
- Cleanup tasks or archival rules
- Health check procedures

**Example**:
```markdown
# Heartbeat

## Memory Consolidation
- **Interval**: Every 24 hours
- **Trigger**: After each daily notes session
- **Action**: Distill daily notes into MEMORY.md under appropriate section
- **Criteria**: Only include learnings relevant to long-term patterns

## Integrity Checks
- Validate MEMORY.md structure every 7 days
- Ensure daily notes follow timestamp format [HH:mm]

## Cleanup
- Archive daily notes older than 90 days to memory/archive/
- Remove duplicate entries when consolidating
```

**Technical Details**:
- File: `{workspace}/HEARTBEAT.md`
- Encoding: UTF-8 (no BOM)
- Max chars included in system prompt: per `MaxContextFileChars` config (default: 8000)
- Loaded into system prompt for reference by the cron-based memory consolidation system (see [Cron and Scheduling Guide](../cron-and-scheduling.md))

### MEMORY.md — Long-Term Distilled Memory

**Purpose**: Capture durable, reusable learnings distilled from daily notes and sessions.

**Loaded Into**: System prompt (every session)

**Content Guidelines**:
- Concise summaries of learned patterns
- Key decisions and their rationale
- User preferences and working patterns
- System architecture learnings
- Recurring issues and solutions
- Cross-session knowledge that improves agent behavior

**Structure**:
Recommended sections (not enforced):
```markdown
# Memory

## Notes
- Pattern: When the user says "build", run `dotnet build BotNexus.slnx`
- Pattern: User prefers concise summaries (max 100 words) before diving into detail
- Decision: Always check build status before suggesting code changes
- Learning: User timezone is Pacific Time; avoid scheduling tasks outside 8am-6pm

## Architecture Learnings
- The core platform has 17 projects with clean dependency inversion
- Extensions are loaded dynamically from `~/.botnexus/extensions/{type}/{name}/`
- SessionManager persists conversation history to JSONL under `~/.botnexus/workspace/sessions/`

## User Preferences
- Async-first communication
- Evidence-based recommendations
- Document trade-offs explicitly
- Bold claims with supporting examples

## Tools Integration
- memory_save: Used to append daily learnings
- memory_get: Used to retrieve specific memories
- memory_search: Used to find patterns across notes
```

**Technical Details**:
- File: `{workspace}/MEMORY.md`
- Encoding: UTF-8 (no BOM)
- Loaded at session start by `AgentContextBuilder.BuildSystemPromptAsync()`
- Max chars included in system prompt: per `MaxContextFileChars` config (default: 8000)
- Manually edited by agent (via `memory_save` tool) or by human
- Not auto-generated (preserved across sessions)

---

## Memory Model

### Two-Layer Memory Architecture

BotNexus implements a two-layer memory model:

1. **Long-Term Memory** (`MEMORY.md`)
   - Distilled, curated learnings
   - Manually edited or consolidated from daily notes
   - Persists indefinitely
   - Always loaded into system prompt

2. **Daily Notes** (`memory/daily/YYYY-MM-DD.md`)
   - Session-specific observations and learnings
   - Timestamped entries appended throughout the day
   - One file per day (UTC date)
   - Today's and yesterday's notes auto-loaded into system prompt

### Auto-Loading Strategy

At system prompt assembly time (`AgentContextBuilder.BuildSystemPromptAsync()`):
- **Always included**: `MEMORY.md` (long-term memory)
- **Conditionally included**: 
  - Today's daily notes (`memory/daily/{today}.md`)
  - Yesterday's daily notes (`memory/daily/{yesterday}.md`)
- **Not included**: Older daily notes (available via `memory_search` tool)

**Rationale**:
- Recent context (today + yesterday) is most relevant
- Limits system prompt size while maintaining recent continuity
- `memory_search` tool provides access to full history when needed

### Memory Storage

Memory files are stored as plain Markdown under the agent workspace:

```text
~/.botnexus/agents/{agent_name}/
├── MEMORY.md                  (Long-term distilled memory)
└── memory/
    └── daily/
        ├── 2026-04-01.md      (Timestamped daily notes)
        ├── 2026-04-02.md
        └── ...
```

**File Format**:
- Markdown (.md) for readability and formatting
- UTF-8 encoding (no BOM)
- Timestamped entries in daily files: `[HH:mm] entry text`
- Sections in MEMORY.md organized by topic

### Backward Compatibility

The `MemoryStore` supports reading from legacy memory formats:
- Checks new path first: `~/.botnexus/agents/{agent_name}/memory/`
- Falls back to legacy path: original configured base path
- Supports both `.md` and `.txt` extensions

---

## Memory Tools

Three tools enable agent interaction with memory:

### memory_search — Find Knowledge Across Memory

Searches across long-term memory and daily notes for relevant information.

**Signature**:
```text
memory_search(
  query: string,                    # Search query string (required)
  max_results: integer = 10         # Maximum number of results (optional)
)
```

**Search Strategy**:
- Keyword-based search (grep-style, case-insensitive)
- Searches MEMORY.md and all daily notes
- Ranks results by recency (today first, then yesterday, then older daily notes, then long-term memory)
- Returns up to `max_results` matches with context (2 lines before and after)
- Each result shows file name, line number, and context

**Example Usage**:
```text
memory_search("Pacific Time", max_results=5)

Found 2 result(s) for 'Pacific Time':

[1] MEMORY.md (match line 12)
  10: ## User Preferences
  11: - Async-first communication
  12: - User timezone is Pacific Time; avoid scheduling outside 8am-6pm
  13: - Evidence-based recommendations

[2] memory/daily/2026-04-02.md (match line 3)
   1: [10:15] Met with user about Q2 planning
   2: [10:30] Discussed timezone constraints
   3: [10:35] User confirmed Pacific Time availability
```

**Implementation**: `MemorySearchTool.cs`
- Reads all searchable keys from memory store
- Filters to MEMORY and daily/* keys only
- Iterates through files in recency order
- Returns context around matching lines

### memory_save — Persist Learnings to Memory

Saves information to long-term memory or today's daily notes.

**Signature**:
```text
memory_save(
  content: string,                  # Memory content to save (required)
  target: string = "daily"          # "memory" (long-term) or "daily" (today's notes)
)
```

**Targets**:

1. **target="daily"** (default)
   - Appends to `memory/daily/{today}.md`
   - Timestamped entry: `[HH:mm] {content}`
   - Creates file if missing
   - Uses local timezone for timestamp

2. **target="memory"**
   - Appends to `MEMORY.md` under `## Notes` section
   - Creates section if missing
   - Format: `- {content}`
   - Overwrites existing MEMORY.md (append mode)

**Example Usage**:
```text
memory_save("Pattern: User prefers concise summaries before detail", target="memory")
→ "Saved to MEMORY.md under '## Notes'."

memory_save("Investigated workspace and memory architecture - complex, well-designed")
→ "Saved to memory/daily/2026-04-02.md."
```

**Implementation**: `MemorySaveTool.cs`
- Handles markdown section management for long-term memory
- Intelligently appends under `## Notes` section
- Creates timestamped daily entries
- Both operations use UTF-8 file I/O

### memory_get — Read Specific Memory Files

Reads long-term memory or a specific daily notes file, with optional line range selection.

**Signature**:
```text
memory_get(
  file: string = "memory",          # "memory" (long-term) or date like "YYYY-MM-DD"
  lines: string = null              # Optional line range like "10-20"
)
```

**File Targets**:
- `file="memory"` → reads `MEMORY.md`
- `file="2026-04-02"` → reads `memory/daily/2026-04-02.md`
- `file=""` (empty or omitted) → defaults to `memory` (MEMORY.md)

**Line Range**:
- Format: `"{start}-{end}"` (1-indexed)
- Example: `lines="5-10"` returns lines 5-10 inclusive
- If omitted, returns full file
- Validates bounds and returns empty if out of range

**Example Usage**:
```text
memory_get(file="memory")
# MEMORY.md

## Notes
- Pattern: User prefers concise summaries
- Decision: Always check build status first

## Architecture Learnings
[full content...]

---

memory_get(file="2026-04-01", lines="1-5")
# memory/daily/2026-04-01.md (lines 1-5)

   1: [08:15] Started architecture review
   2: [09:30] Analyzed 17 projects, clean dependency inversion
   3: [10:45] Identified 3 critical gaps
```

**Implementation**: `MemoryGetTool.cs`
- Resolves file target and validates date format
- Reads full file content
- Parses line range and validates bounds
- Returns numbered output for easy reference

---

## Memory Consolidation

Memory consolidation is the process of distilling daily notes into long-term memory (MEMORY.md).

### Consolidation Trigger

- **Interval**: Configurable via `MemoryConsolidationIntervalHours` (default: 24)
- **Mechanism**: Cron service — runs as a `maintenance` job with `consolidate-memory` action (see [Cron and Scheduling Guide](../cron-and-scheduling.md))
- **Manual**: Via `memory_save(content, target="memory")`

### Consolidation Process (Current Manual Approach)

Currently, consolidation is manual:

1. Agent reviews daily notes via `memory_get(file="{yesterday}")` or `memory_search()`
2. Agent identifies patterns and learnings from daily notes
3. Agent saves learnings to long-term memory: `memory_save(learning, target="memory")`
4. Long-term memory accumulates under `## Notes` section

**Example Consolidation**:
```text
Daily Notes (2026-04-02.md):
[09:15] User prioritizes concise summaries, max 100 words
[10:30] Architecture has 17 projects with clean inversion
[14:45] Build command: dotnet build BotNexus.slnx
[16:00] Confirmed user timezone is Pacific Time

Consolidation → memory_save(
  "Pattern: User prefers concise summaries (max 100 words) before detail",
  target="memory"
)
```

### Future Consolidation (Planned)

Phase 3 of the workspace/memory initiative includes:

- **IMemoryConsolidator** interface for pluggable consolidation strategies
- **LLM-based consolidation**: Call a model to distill daily notes
- **Cron-based trigger**: Consolidation runs on schedule via the centralized cron service (`consolidate-memory` maintenance action)
- **Configurable model**: `ConsolidationModel` config for consolidation LLM (can differ from agent's primary model)

---

## Configuration Reference

Agent workspaces are configured via `AgentConfig` in the BotNexus configuration:

### Configuration Keys

```json
{
  "BotNexus": {
    "Agents": {
      "Model": "gpt-4-turbo",               // Default LLM model
      "ContextWindowTokens": 8000,          // Total context window (tokens)
      "Named": {
        "leela": {
          "Model": "gpt-4-turbo",           // Agent-specific model (overrides default)
          "Workspace": "~/.botnexus/agents/leela/",  // Agent workspace path
          "EnableMemory": true,             // Enable memory for this agent
          "MaxContextFileChars": 8000,      // Max chars per workspace file in system prompt
          "MaxTokens": 2000,                // Max tokens per response
          "Temperature": 0.7,               // LLM temperature
          "MaxToolIterations": 40,          // Max tool calls per execution
          "ConsolidationModel": "gpt-3.5-turbo", // Model for memory consolidation
          "MemoryConsolidationIntervalHours": 24,  // Consolidation interval
          "AutoLoadMemory": true            // Auto-load today+yesterday in system prompt
        }
      }
    }
  }
}
```

### Key Configuration Properties

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `Workspace` | string | `~/.botnexus/agents/{name}/` | Override default workspace path |
| `EnableMemory` | bool | `true` | Enable memory system for this agent |
| `MaxContextFileChars` | int | 8000 | Max characters per workspace file included in system prompt |
| `AutoLoadMemory` | bool | `true` | Auto-load today's and yesterday's daily notes into system prompt |
| `ConsolidationModel` | string | (none) | LLM model for memory consolidation (can differ from primary model) |
| `MemoryConsolidationIntervalHours` | int | 24 | Hours between consolidation runs |

### Environment Variables

```bash
# Override home directory
export BOTNEXUS_HOME=/custom/path/.botnexus
```

---

## Context Builder

The `AgentContextBuilder` assembles the full system prompt for an agent at session start.

### System Prompt Assembly Order

The system prompt is built in this order (separated by `\n\n---\n\n`):

1. **Identity Block** (auto-generated)
   ```
   ## Identity
   - Agent: {agentName}
   - Platform: {osDescription}
   - Workspace: {workspacePath}
   - Time (UTC): {utcNow}
   
   ### Guidelines
   [standard guidelines]
   ```

2. **SOUL.md** (if exists and non-empty)
   ```
   ## SOUL.md
   [file content, truncated to MaxContextFileChars]
   ```

3. **IDENTITY.md** (if exists and non-empty)
   ```
   ## IDENTITY.md
   [file content, truncated to MaxContextFileChars]
   ```

4. **USER.md** (if exists and non-empty)
   ```
   ## USER.md
   [file content, truncated to MaxContextFileChars]
   ```

5. **AGENTS.md** (auto-generated)
   ```
   ## AGENTS.md
   [auto-generated list of all agents and their roles]
   ```

6. **TOOLS.md** (auto-generated)
   ```
   ## TOOLS.md
   [auto-generated list of available tools]
   ```

7. **Long-Term Memory** (if MEMORY.md exists)
   ```
   ## MEMORY.md
   [file content, truncated to MaxContextFileChars]
   ```

8. **Today's Daily Notes** (if exists)
   ```
   ## memory/daily/{today}.md
   [file content, truncated to MaxContextFileChars]
   ```

9. **Yesterday's Daily Notes** (if exists)
   ```
   ## memory/daily/{yesterday}.md
   [file content, truncated to MaxContextFileChars]
   ```

### Truncation

If any file exceeds `MaxContextFileChars`:
- Content is truncated to `MaxContextFileChars` characters
- Suffix `[truncated]` is appended to signal truncation
- Allows agents to process large memory files without exceeding token budget

### Auto-Generation

- **AGENTS.md**: Regenerated every session from `BotNexusConfig.Agents`
- **TOOLS.md**: Regenerated every session from `ToolRegistry.GetDefinitions()`
- Both always reflect current configuration and registered tools

### Implementation

- File: `AgentContextBuilder.cs`
- Method: `BuildSystemPromptAsync(agentName, cancellationToken)`
- Also provides: `BuildMessagesAsync(agentName, history, currentMessage, channel, chatId, cancellationToken)`
  - Builds system prompt + trimmed history + runtime context
  - Trims history to fit context window budget
  - Respects `ContextWindowTokens` configuration

---

## Examples

### Example 1: Minimal Workspace

A new agent with minimal configuration:

**Files**:
```text
~/.botnexus/agents/newagent/
├── SOUL.md
├── IDENTITY.md
├── USER.md
├── AGENTS.md
├── TOOLS.md
├── HEARTBEAT.md
├── MEMORY.md
└── memory/
    └── daily/
```

**SOUL.md**:
```markdown
# Soul

You are a helpful assistant focused on solving problems.
```

**IDENTITY.md**:
```markdown
# Identity

You are a general-purpose assistant.
```

**USER.md**:
```markdown
# User

Standard user with no special preferences.
```

---

### Example 2: Architect Workspace

A specialized architect agent with rich personality:

**SOUL.md**:
```markdown
# Soul

## Personality
You are a pragmatic software architect with strong opinions on SOLID principles and clean architecture. You communicate directly and without unnecessary jargon.

## Core Values
- Clarity over cleverness
- Empirical evidence over theory
- User needs over personal preference
- Long-term maintainability over short-term speed

## Boundaries
- Never suggest changes without understanding the problem first
- Never recommend approaches that compromise future flexibility
- Never ignore security implications

## Principles
- Lead by example with well-documented code
- Prefer proven patterns over novel solutions
- Prioritize team understanding over individual brilliance
```

**IDENTITY.md**:
```markdown
# Identity

## Role
Lead/Architect for the engineering team. You own architectural decisions, code reviews, and mentorship.

## Communication Style
- Concise and direct
- Provide evidence and examples
- Ask clarifying questions before committing
- Document decisions and trade-offs

## Expertise
- .NET / C# architecture
- Distributed systems
- SOLID design patterns
- Test-driven development
- Continuous integration and deployment
```

**MEMORY.md**:
```markdown
# Memory

## Patterns
- Team prefers async communication over sync meetings
- Code review focuses on architecture and maintainability first
- User timezone is Pacific Time; avoid scheduling outside 8am-6pm
- Build command: `dotnet build BotNexus.slnx`
- Test command: `dotnet test BotNexus.slnx`

## Architecture Learnings
- Core platform has 17 projects with clean dependency inversion
- Extensions are loaded dynamically from `~/.botnexus/extensions/{type}/{name}/`
- SessionManager persists conversation history as JSONL
- Agent loop supports max 40 tool iterations per execution

## Tools Integration
- ToolRegistry.Register() to add tools
- ToolBase abstract class for tool implementations
- Tool definitions auto-generated in TOOLS.md each session

## Decision Patterns
- Always validate architectural changes with automated tests
- Document rationale in decisions.md for significant changes
- Escalate security concerns immediately
- Propose multiple alternatives for large decisions
```

---

### Example 3: Session System Prompt (Generated)

System prompt assembled at session start for "leela" agent:

```text
## Identity

- Agent: leela
- Platform: Windows 10.0 (Build 22621)
- Workspace: C:\Users\jonbullen\.botnexus\agents\leela
- Time (UTC): 2026-04-02T15:30:45.1234567+00:00

### Guidelines
- Follow workspace and memory instructions as source of truth.
- Prefer concise, actionable responses.
- Use tools deliberately and safely.

---

## SOUL.md

## Personality
You are a pragmatic software architect with strong opinions on SOLID principles...

---

## IDENTITY.md

## Role
Lead/Architect for the engineering team. You own architectural decisions...

---

## USER.md

## About
Jon Bullen, Product Owner and Technical Lead for BotNexus.

## Working Preferences
- Prefer written communication and documentation over sync meetings
- Async-first: decisions documented in GitHub issues/PRs
- Appreciate concise summaries with links to detailed work

---

## AGENTS.md

Configured agents:

### default
- Model: gpt-4-turbo
- Role: default agent

### leela
- Model: gpt-4-turbo
- Role: Lead/Architect for the engineering team. You own architectural decisions...

---

## TOOLS.md

- exec: Execute a shell command on the local machine
- fetch: Retrieve content from a URL
- memory_get: Read long-term memory or a specific daily notes file
- memory_save: Save information to long-term memory or today's daily notes
- memory_search: Search across long-term memory and daily notes
- web_search: Search the web using a search engine

---

## MEMORY.md

## Patterns
- Team prefers async communication over sync meetings
- Code review focuses on architecture and maintainability first
- User timezone is Pacific Time; avoid scheduling outside 8am-6pm

---

## memory/daily/2026-04-02.md

[09:15] Reviewed workspace and memory architecture
[10:30] Documented system prompt assembly process
[14:00] Discussed memory consolidation strategy
```

---

## First-Run Behavior

When an agent workspace doesn't exist yet, the system initializes it automatically.

### Initialization Flow

1. **Workspace Detection**
   - `AgentContextBuilder.BuildSystemPromptAsync()` is called
   - Calls `IAgentWorkspace.InitializeAsync()` first

2. **Initialization Steps** (in `AgentWorkspace.InitializeAsync()`)
   - Create agent workspace directory: `~/.botnexus/agents/{agentName}/`
   - Create memory directories:
     - `~/.botnexus/agents/{agentName}/memory/`
     - `~/.botnexus/agents/{agentName}/memory/daily/`
   - Create bootstrap files if missing:
     - `SOUL.md` (with placeholder comment)
     - `IDENTITY.md` (with placeholder comment)
     - `USER.md` (with placeholder comment)
     - `MEMORY.md` (with placeholder comment)
     - `HEARTBEAT.md` (with placeholder comment)

3. **File Content**
   Bootstrap files are created with placeholder templates:

   **SOUL.md**:
   ```markdown
   # Soul

   <!-- Define this agent's core personality, values, and boundaries.
        This file is loaded into every session as part of the system prompt. -->

   ```

   **IDENTITY.md**:
   ```markdown
   # Identity

   <!-- Describe this agent's role, communication style, and operating constraints.
        This file is loaded into every session as part of the system prompt. -->

   ```

   **USER.md**:
   ```markdown
   # User

   <!-- Capture user-specific preferences, priorities, and collaboration expectations.
        This file is loaded into every session as part of the system prompt. -->

   ```

4. **Idempotency**
   - Files are only created if missing
   - If workspace already exists, initialization is skipped
   - Safe to call multiple times

5. **Human Editing**
   - After initialization, human edits SOUL.md, IDENTITY.md, USER.md
   - Next session automatically includes updated content in system prompt
   - No restart required — content loaded per session

### Example First-Run

**Command**:
```bash
# User creates agent "mybot" and runs it for first time
dotnet run -- --agent mybot "Hello, who are you?"
```

**Initialization**:
```text
Creating workspace: ~/.botnexus/agents/mybot/
Creating memory directories...
Creating SOUL.md (template)
Creating IDENTITY.md (template)
Creating USER.md (template)
Creating MEMORY.md (template)
Creating HEARTBEAT.md (template)

Workspace initialized. Edit the following files before next session:
- ~/.botnexus/agents/mybot/SOUL.md
- ~/.botnexus/agents/mybot/IDENTITY.md
- ~/.botnexus/agents/mybot/USER.md
```

**Human Action**:
```bash
# User edits workspace files
cd ~/.botnexus/agents/mybot
# Edit SOUL.md, IDENTITY.md, USER.md with custom content
```

**Second Run**:
```bash
# User runs agent again
dotnet run -- --agent mybot "What's in SOUL.md?"

# Response includes personalized content from edited files
```

---

## Related Documentation

- [Architecture Overview](../architecture/overview.md) — System architecture and design patterns
- [Configuration Guide](../configuration.md) — Full configuration reference
- [Extension Development Guide](../extension-development.md) — Building extensions

---

## Implementation References

- **AgentWorkspace**: `src/BotNexus.Agent/AgentWorkspace.cs`
- **AgentContextBuilder**: `src/BotNexus.Agent/AgentContextBuilder.cs`
- **MemoryStore**: `src/BotNexus.Agent/MemoryStore.cs`
- **Memory Tools**: `src/BotNexus.Agent/Tools/MemorySearchTool.cs`, `MemorySaveTool.cs`, `MemoryGetTool.cs`
- **Configuration**: `src/BotNexus.Core/Configuration/AgentConfig.cs`, `BotNexusHome.cs`
- **Abstractions**: `src/BotNexus.Core/Abstractions/IAgentWorkspace.cs`, `IContextBuilder.cs`, `IMemoryStore.cs`
