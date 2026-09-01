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
- **Separation of Concerns**: Distinct from session data (stored in `sessions.sqlite`) and deployed extension binaries (`~/.botnexus/extensions/`). Memory is file-based plain Markdown — SQLite is used only as an optional search index, not as the source of truth for memory content

---

## Workspace Location and Structure

All agent workspaces are stored under the BotNexus home directory:

```text
~/.botnexus/agents/{agent_name}/
├── SOUL.md                    (Core personality, values, boundaries)
├── IDENTITY.md                (Role, communication style, constraints)
├── USER.md                    (User preferences and collaboration expectations)
├── AGENTS.md                  (Scaffolded once: multi-agent awareness)
├── TOOLS.md                   (Scaffolded once: external tool guidance)
├── HEARTBEAT.md               (Periodic tasks and memory consolidation cadence)
├── MEMORY.md                  (Long-term distilled memory)
└── memory/
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
- Injected in full; the prompt builder applies no per-file character cap

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
- Injected in full; the prompt builder applies no per-file character cap

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
BotNexus Team.

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
- Injected in full; the prompt builder applies no per-file character cap

### AGENTS.md — Multi-Agent Awareness (Scaffolded Once)

**Purpose**: Reference of the other agents in this world, their models, roles and providers, plus minimal memory guidance.

**Loaded Into**: System prompt (every session)

**Origin**:
- Written once by `BotNexusHome` when the workspace is scaffolded, from an embedded `Templates.AGENTS.md` resource (an empty file if the resource is absent)
- **Never regenerated.** Existing files are left untouched on every subsequent start, so hand edits persist and stale entries are the author's to correct
- Loaded verbatim into the prompt by `WorkspaceContextBuilder` alongside the other workspace files

> Repository `AGENTS.md` convention files are a different mechanism: they are discovered on demand
> through the `get_agent_files` tool rather than injected, and do not affect this workspace file.

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

## Memory Notes

- Append durable summaries to `MEMORY.md` when you learn stable, long-term facts.
- Append day-specific notes to `memory/YYYY-MM-DD.md` for active work context.
```

**Technical Details**:
- File: `{workspace}/AGENTS.md` (exists but is replaced at session start)
- Safe to edit by hand; never overwritten once present
- Injected in full; the prompt builder applies no per-file character cap

### TOOLS.md — Available Tools (Scaffolded Once)

**Purpose**: User guidance on how to use the external tools this agent reaches for.

**Loaded Into**: System prompt (every session)

**Origin**:
- Written once by `BotNexusHome` when the workspace is scaffolded, from an embedded `Templates.TOOLS.md` resource
- **Never regenerated.** It is a hand-maintained note, not a live inventory
- `TOOLS.md` does **not** control tool availability — the structured tool definitions supplied to the model are the source of truth for which tools exist. A tool named here that is not registered is simply guidance for a tool the agent does not have

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
- Safe to edit by hand; never overwritten once present
- Injected in full; the prompt builder applies no per-file character cap

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
- Injected in full; the prompt builder applies no per-file character cap
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
- Pattern: When the user says "build", run `dotnet build dirs.proj`
- Pattern: User prefers concise summaries (max 100 words) before diving into detail
- Decision: Always check build status before suggesting code changes
- Learning: User timezone is Pacific Time; avoid scheduling tasks outside 8am-6pm

## Architecture Learnings
- The core platform has 17 projects with clean dependency inversion
- Extensions are loaded dynamically from `~/.botnexus/extensions/{type}/{name}/`
- SessionManager persists conversation history to `~/.botnexus/sessions.sqlite`

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
- Loaded at session start by `WorkspaceContextBuilder.BuildSystemPromptAsync()`
- Injected in full; the prompt builder applies no per-file character cap
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

2. **Daily Notes** (`memory/YYYY-MM-DD.md`)
   - Session-specific observations and learnings
   - Plain Markdown content appended throughout the day
   - One file per day (local date)
   - Today's and yesterday's notes auto-loaded into system prompt

### Auto-Loading Strategy

At system prompt assembly time (`WorkspaceContextBuilder.BuildSystemPromptAsync()`):
- **Always included**: `MEMORY.md` (long-term memory)
- **Conditionally included**: 
  - Today's daily notes (`memory/{today}.md`)
  - Yesterday's daily notes (`memory/{yesterday}.md`)
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
    ├── 2026-04-01.md      (Plain Markdown daily notes)
    ├── 2026-04-02.md
    └── ...
```

**File Format**:
- Markdown (.md) for readability and formatting
- UTF-8 encoding (no BOM)
- Plain Markdown content appended to daily files
- Sections in MEMORY.md organized by topic

### Memory provenance

Every row in the SQLite memory store carries **provenance** - a record of *whose words* the
content is - alongside `source_type`, which records *what kind of write* produced it. The two
answer different questions and are deliberately separate fields.

| Provenance | Meaning |
| --- | --- |
| `agent` | The agent's own reasoning, summaries or deliberate `memory_save` notes. |
| `user` | First-party owner input, including indexed conversation turns. |
| `tool` | Derived from a tool result the agent executed. |
| `external-untrusted` | Ingested from a third party the agent does not control - an issue body, a comment, an inbound message from an unverified sender, fetched web content. |
| `unknown` | Provenance was never recorded, or the stored value was not recognised. |

Why it exists: untrusted third-party text is inert display-only input on the turn it arrives,
but once summarised into a memory row it would otherwise read back on a later session as
first-party agent knowledge with its origin erased - a prompt-injection laundering path through
the memory store.

**Fail-safe rules:**

- `unknown` is the default for any absent, blank or unrecognised value, and it is **not**
  first-party. Being trusted requires having been explicitly stamped at write time.
- Provenance is normalised on write as well as on read, so a value outside the vocabulary above
  cannot be persisted and later mistaken for a new trust level.
- Recall surfaces it: `memory_search` and `memory_get` render a `Provenance:` line, and each
  daily note injected into the system prompt is prefixed with a
  `> [memory provenance: <value>]` banner. The banner is unconditional, so its absence can never
  be read as "verified first-party".

**Schema:** `provenance`, `origin_conversation_id` and `origin_session_id` are additive nullable
`TEXT` columns. A store file created before they existed is upgraded lazily at open via
`ALTER TABLE ... ADD COLUMN` and is never rejected; its pre-existing rows keep `NULL`, which
reads back as `unknown`. There is deliberately no backfill to a trusted value - `NULL` is the
honest record that provenance was never captured.

> Recording provenance is separate from acting on it. What consumes it - trust tiers at rank,
> injection and promotion time - is described in [Memory trust tiers](#memory-trust-tiers) below.

### Memory trust tiers

Provenance records *where content came from*. A **trust tier** is what the retrieval pipeline
*does about it*. The tier is a pure function of the provenance, derived at read time by
`MemoryTrust.Derive` and never stored.

| Provenance | Trust tier | Rank weight | Always-on injection | Auto-promotion |
| --- | --- | --- | --- | --- |
| `user` | `trusted` | full | yes | eligible |
| `agent`, `tool` | `derived` | full | yes | eligible |
| `unknown` | `untrusted` | reduced | no | refused |
| `external-untrusted` | `quarantined` | reduced further | no | refused |

**Derived, never persisted.** There is no trust column. A stored tier could drift from the
provenance it was computed from - through a partial migration, a direct SQL edit, or a write path
that forgot to recompute it - and a drifted trust value fails *open*, presenting untrusted content
as first-party. Recomputing on read makes that class of bug unrepresentable.

**Weighted at rank, filtered at injection.** These look inconsistent and are not. A search result
is *pulled* by an explicit query and rendered with its provenance and trust lines, so the caller
can see what it is looking at; down-weighting keeps it discoverable and explainable. Always-on
context is *pushed* into the system prompt every turn with no query and no opportunity to decline,
which makes it indistinguishable from the agent's own standing instructions - the exact position an
attacker wants their text to occupy. So ranking discounts, and injection excludes.

Nothing is ever dropped *before* ranking. A pre-rank filter would make a store holding only
untrusted material indistinguishable from an empty store, and those two situations call for
opposite responses.

**Exclusion is disclosed.** When the injection gate withholds notes it appends a
`> [memory injection: N note(s) withheld ...]` line naming the count and pointing at
`memory_search`, so the agent can still retrieve the content deliberately instead of concluding the
note was never written. When `memory_search` is *not* available to the agent for the turn the
disclosure is still emitted but stops naming the tool - see [Capability scoping](#capability-scoping)
below.

### Capability scoping

Provenance answers *may this content be pushed*. It does not answer *is this agent configured to
have memory at all*. Those are two orthogonal axes, and always-on injection is gated on both.

Before any daily note is read off disk, the prompt builder resolves the turn's effective tool policy
for the agent - the descriptor's `toolIds` allowlist (including an archetype-derived one for a
spawned sub-agent) unioned with the effective deny-list, with runtime-pinned tools exempt. If no
memory recall tool (`memory_search`, `memory_get`) survives that resolution:

- **No memory content is injected at all**, regardless of provenance. An agent spawned without
  memory tools was scoped that way deliberately; injecting its notes anyway pushes private content
  across an agent boundary that was drawn on purpose.
- **No disclosure is emitted either.** A disclosure is a recovery affordance, and there is nothing
  here for this agent to recover with.

When memory *is* available but `memory_search` specifically is not, notes are injected normally and
any exclusion disclosure drops the retrieval instruction: naming an unregistered tool induces a
guaranteed-failing call and a `Tool 'memory_search' is not registered` error that misdirects
diagnosis.

The capability signal is **resolved in the gateway layer and passed to the memory provider as a
plain boolean** on `AgentMemoryPromptRequest`. The memory assembly deliberately takes no dependency
on the tool-policy provider; the layer entitled to know about security resolves the question and
hands over the answer, so the injection gate stays a pure function.

The signal defaults to *available*, so a composition that supplies no policy provider behaves
exactly as it did before this gate existed. Fail-open is deliberate here: this is a scoping control,
not the trust boundary, and a missing registration must not silently blind every agent's memory.

**Mixtures take the least-trusted contributor.** A summary distilled from several rows is stamped
with the worst provenance that went into it, never the most common one, and the contributing set is
recorded on the extracted item. Majority-voting a mixture would erase the single hostile
contributor that is the entire reason to be looking. An item with no recorded contributors resolves
to `unknown`, not to a first-party value.

**Promotion is an authority transfer, not a copy.** Agents reading a shared store never saw the
originating turn and cannot judge the content's origin for themselves, so `SharedMemoryPromoter`
refuses non-first-party items outright. A promoted row keeps its contributing provenance and is
never re-stamped as first-party on ingest.

**Legacy rows stay reachable.** `NULL`-provenance rows resolve to `unknown`/`untrusted`:
down-weighted, excluded from always-on injection, ineligible for promotion - but fully searchable
and fully readable. There is no backfill to a trusted value, and no tier is weighted to zero,
because a zero weight is a silent pre-rank drop by another name.

**Surfacing.** `memory_search` and `memory_get` render a `Trust:` line alongside the existing
`Provenance:` line. Provenance says where the content came from; the tier says how retrieval
treated it. A reader auditing a surprising result needs both.

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
  topK: integer = 10,               # Maximum number of results (optional)
  minScore: number                  # Optional relevance floor; see below
)
```

**Result format**: each result renders its rank *and* the numeric fused relevance score that
produced that rank, e.g. `Score: 0.7412 (rank #1)`. Passing `minScore` excludes results below the
floor, returning an empty set rather than a ranked page of near-misses when nothing qualifies. The
score is a provider-specific magnitude, not a 0-1 probability — see
[Hybrid memory retrieval](../features/hybrid-memory-retrieval.md) for how it is computed and how to
calibrate a floor.

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

[2] memory/2026-04-02.md (match line 3)
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

Appends markdown notes to an agent's memory workspace files.

**Signature**:
```text
memory_save(
  content: string,                  # Memory content to append (required)
  file_path: string = null          # Relative path under memory root (optional)
)
```

**Behavior**:

1. **Without `file_path`** (legacy / default)
   - Appends to today's daily note: `memory/{today}.md`
   - Creates file if missing
   - Content is appended as-is (plain Markdown, newline-terminated)

2. **With `file_path`**
   - Appends to the specified file relative to the memory root
   - Path must be relative (absolute paths are rejected)
   - Leading `memory/` prefix is stripped automatically
   - Path traversal outside the memory root is blocked

**Example Usage**:
```text
memory_save(content="User prefers concise summaries before detail")
→ "Appended memory note to memory/2026-04-02.md."

memory_save(content="Architecture has 17 projects", file_path="architecture.md")
→ "Appended memory note to memory/architecture.md."

memory_save(content="Stable long-term preference", file_path="../MEMORY.md")
→ Error: "file_path must remain within the memory root."
```

**Implementation**: `MemorySaveTool.cs`
- Resolves memory root from workspace path (default: `memory/` subdirectory)
- Supports per-agent memory path override via `MemoryAgentConfig.Path`
- Ensures target directory exists before writing
- Normalizes content with trailing newline

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
- `file="2026-04-02"` → reads `memory/2026-04-02.md`
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
# memory/2026-04-01.md (lines 1-5)

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
- **Manual**: Not yet available — Wave 5 consolidation will provide a dedicated mechanism

### Consolidation Process

Consolidation is a **future capability** (Wave 5). During normal turns, `MEMORY.md` is read-only — only daily notes under `memory/` are writable via `memory_save`.

When consolidation is implemented, the planned flow is:

1. A dedicated consolidation agent reviews daily notes via `memory_get(file="{yesterday}")` or `memory_search()`
2. The consolidation agent identifies patterns and durable learnings
3. The consolidation agent writes updated content to `MEMORY.md` (using a privileged write path not available during normal turns)
4. The scaffolded `AGENTS.md` template reminds agents to use `MEMORY.md` for stable facts and `memory/YYYY-MM-DD.md` for active work context

**Example of what consolidation will produce**:
```text
Daily Notes (memory/2026-04-02.md):
User prioritizes concise summaries, max 100 words
Architecture has 17 projects with clean inversion
Build command: dotnet build dirs.proj
Confirmed user timezone is Pacific Time

Consolidation → MEMORY.md updated by consolidation agent:
- Pattern: User prefers concise summaries (max 100 words) before detail
- User timezone is Pacific Time
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
  "agents": {
    "leela": {
      "model": "gpt-4-turbo",
      "memory": {
        "enabled": true,
        "path": "memory",
        "indexing": "auto",
        "promptInjection": "full",
        "search": {
          "defaultTopK": 10,
          "maxTopK": 100
        }
      }
    }
  }
}
```

### Key Configuration Properties

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `memory.enabled` | bool | `false` | Enable the memory system for this agent |
| `memory.path` | string | `memory` | Memory root directory, relative to the agent workspace. Daily notes (`YYYY-MM-DD.md`) are written here |
| `memory.indexing` | string | `auto` | Memory indexing mode |
| `memory.promptInjection` | string | `full` | How memory is injected into the system prompt: `full`, `summary`, or `none` |
| `memory.search.defaultTopK` | int | `10` | Default number of `memory_search` results |
| `memory.search.maxTopK` | int | `100` | Upper bound for the `memory_search` `topK` argument; larger caller values are clamped |
| `memory.search.maxLimit` | int | `100` | Upper bound for the `memory_get` session-listing `limit` argument |

> The agent workspace path is not a per-agent config key - it is derived as
> `~/.botnexus/agents/{id}/workspace/`. There is likewise no `maxContextFileChars`,
> `autoLoadMemory`, `consolidationModel` or `memoryConsolidationIntervalHours` key; none of these bind
> to anything.

### Environment Variables

```bash
# Override home directory
export BOTNEXUS_HOME=/custom/path/.botnexus
```

---

## Context Builder

The `WorkspaceContextBuilder` assembles the full system prompt for an agent at session start.

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
   [file content]
   ```

3. **IDENTITY.md** (if exists and non-empty)
   ```
   ## IDENTITY.md
   [file content]
   ```

4. **USER.md** (if exists and non-empty)
   ```
   ## USER.md
   [file content]
   ```

5. **AGENTS.md** (scaffolded once)
   ```
   ## AGENTS.md
   [list of the other agents and their roles]
   ```

6. **TOOLS.md** (scaffolded once)
   ```
   ## TOOLS.md
   [guidance on the external tools this agent uses]
   ```

7. **Long-Term Memory** (if MEMORY.md exists)
   ```
   ## MEMORY.md
   [file content]
   ```

8. **Today's Daily Notes** (if exists)
   ```
   ## memory/{today}.md
   [file content]
   ```

9. **Yesterday's Daily Notes** (if exists)
   ```
   ## memory/{yesterday}.md
   [file content]
   ```

### Owner-private files in shared conversations

`MEMORY.md`, `USER.md` and the daily notes under the memory root are **owner-private**: `MEMORY.md`
is the agent's consolidated long-term memory and `USER.md` is the owner's personal profile (name,
timezone, working preferences). In a conversation the owner does not exclusively occupy, injecting
them discloses that content to participants with no claim on it (issue #2846).

The prompt builder therefore takes a **conversation scope**:

| Scope | Meaning | Effect |
|---|---|---|
| `Private` | The conversation is the owning agent's alone - or the caller has no conversation at all (a descriptor-only prompt build). This is the default for every caller that does not state otherwise. | All workspace files are injected. |
| `Shared` | The conversation carries participants beyond the owning agent and one human counterpart. | `MEMORY.md`, `USER.md` and every file under the memory root are withheld from the assembled prompt. |

Two properties matter in practice:

- **The caller's scope is a floor, not the last word.** A conversation whose persisted participant
  set already contains non-owner participants is treated as shared even when the caller passed
  `Private`, so call sites that have not been threaded yet are still protected. Escalation only -
  a caller that already knows the conversation is shared is never downgraded by a participant list
  that has not been backfilled.
- **An empty participant list stays private.** That is the shape of every conversation row written
  before participants were tracked, so the default path is unchanged.

"Non-owner" means any agent participant other than the conversation's owning agent, or more than one
distinct human participant. A single human plus the owning agent is the one-to-one channel the
private file set was designed for.

Extensions can contribute context files through the `BeforeContextFilesBuildEvent` hook, which fires
after the workspace files are loaded but **before** the owner-private exclusion and before assembly.
That ordering is deliberate: a hook cannot reintroduce `MEMORY.md` or `USER.md` into a shared
conversation, and private content is never materialised into prompt text in the first place.
(`BeforePromptBuildEvent` operates on the already-assembled string and is the wrong place to enforce
a content boundary.)

### Truncation

There is none. `WorkspaceContextBuilder` reads each context file with `ReadAllTextAsync` and injects
it whole (trimmed of surrounding whitespace); no per-file character cap is applied and no
`[truncated]` suffix is ever emitted. Keeping a workspace file inside the model's context budget is
the author's responsibility - a very large `MEMORY.md` consumes the budget it asks for. Session-level
compaction, not prompt-time truncation, is what bounds a long-running conversation.

### Workspace file scaffolding

- **AGENTS.md**: Scaffolded once from an embedded template; never regenerated
- **TOOLS.md**: Scaffolded once from an embedded template; never regenerated
- Neither tracks live configuration or the registered tool set - both are hand-maintained after the
  first write

### Implementation

- File: `WorkspaceContextBuilder.cs`
- Method: `BuildSystemPromptAsync(descriptor, executionContext, effectiveSettings, scope, cancellationToken)`
- The `scope` parameter states whether the target conversation is owner-private or shared; see
  [Owner-private files in shared conversations](#owner-private-files-in-shared-conversations)
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
- Build command: `dotnet build dirs.proj`
- Test command: use the remote repository validation gate

## Architecture Learnings
- Core platform has 17 projects with clean dependency inversion
- Extensions are loaded dynamically from `~/.botnexus/extensions/{type}/{name}/`
- SessionManager persists conversation history as JSONL
- Agent loop supports max 40 tool iterations per execution

## Tools Integration
- ToolRegistry.Register() to add tools
- ToolBase abstract class for tool implementations
- Tool definitions are supplied to the model as structured definitions, not via TOOLS.md

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
- Workspace: C:\Users\username\.botnexus\agents\leela
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
BotNexus Team.

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

## memory/2026-04-02.md

Reviewed workspace and memory architecture
Documented system prompt assembly process
Discussed memory consolidation strategy
```

---

## First-Run Behavior

When an agent workspace doesn't exist yet, the system initializes it automatically.

### Initialization Flow

1. **Workspace Detection**
   - `WorkspaceContextBuilder.BuildSystemPromptAsync()` is called
   - Calls `IAgentWorkspace.InitializeAsync()` first

2. **Initialization Steps** (in `AgentWorkspace.InitializeAsync()`)
   - Create agent workspace directory: `~/.botnexus/agents/{agentName}/`
   - Create memory directory:
     - `~/.botnexus/agents/{agentName}/memory/`
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

- **AgentWorkspace**: `src/gateway/BotNexus.Gateway.Contracts/Agents/AgentWorkspace.cs`
- **WorkspaceContextBuilder**: `src/gateway/BotNexus.Gateway/Agents/WorkspaceContextBuilder.cs`
- **Memory store**: `src/gateway/BotNexus.Memory/SqliteMemoryStore.cs` (the concrete store; there is no `MemoryStore.cs`)
- **Memory Tools**: `src/gateway/BotNexus.Memory/Tools/MemorySearchTool.cs`, `MemorySaveTool.cs`, `MemoryGetTool.cs`
- **Configuration**: `src/gateway/BotNexus.Gateway.Configuration/BotNexusHome.cs`, `AgentConfigurationHostedService.cs`
- **Abstractions**: `src/gateway/BotNexus.Gateway.Contracts/Agents/IContextBuilder.cs`, `src/gateway/BotNexus.Memory/IMemoryStore.cs`
