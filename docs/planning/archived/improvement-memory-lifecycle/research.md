# Research: Memory Persistence Lifecycle

## Problem Statement

Nova is not writing to memory files during sessions. Important context, decisions, and conversation topics are lost between sessions because there is no systematic trigger for the agent to persist information to disk. The agent relies entirely on ad-hoc "write to memory" decisions, which are easily forgotten during complex work.

## How OpenClaw Handles Memory (Source Analysis)

OpenClaw has a sophisticated multi-layer memory system built into the `memory-core` plugin. Key findings from source analysis of the installed OpenClaw binary (`Q:/.tools/.npm-global/node_modules/openclaw/`):

### Layer 1: Pre-Compaction Memory Flush (Automatic)

**Source**: `extensions/memory-core/src/flush-plan.ts` and `src/auto-reply/reply/memory-flush.ts`

When the session approaches the compaction threshold, OpenClaw runs an **automatic memory flush** BEFORE compacting:

1. **Trigger**: Token count approaches threshold (`contextWindow - reserveTokensFloor - softThresholdTokens`)
   - Default soft threshold: 4,000 tokens before compaction would fire
   - Also triggered by transcript size: force flush at 2MB transcript
2. **What happens**: A special LLM turn runs with this prompt:
   ```
   Pre-compaction memory flush.
   Store durable memories only in memory/YYYY-MM-DD.md (create memory/ if needed).
   Treat workspace bootstrap/reference files such as MEMORY.md, DREAMS.md, SOUL.md,
   TOOLS.md, and AGENTS.md as read-only during this flush; never overwrite, replace,
   or edit them.
   If memory/YYYY-MM-DD.md already exists, APPEND new content only and do not
   overwrite existing entries.
   Do NOT create timestamped variant files (e.g., YYYY-MM-DD-HHMM.md);
   always use the canonical YYYY-MM-DD.md filename.
   If nothing to store, reply with NO_REPLY.
   ```
3. **System prompt for flush turn**:
   ```
   Pre-compaction memory flush turn.
   The session is near auto-compaction; capture durable memories to disk.
   [same safety hints as above]
   You may reply, but usually NO_REPLY is correct.
   ```
4. **Safety guards**:
   - Only writes to `memory/YYYY-MM-DD.md`
   - APPEND only - never overwrites existing entries
   - Cannot modify MEMORY.md, DREAMS.md, SOUL.md, TOOLS.md, AGENTS.md
   - One flush per compaction cycle (tracked via `memoryFlushCompactionCount`)
   - Not triggered during heartbeats

### Layer 2: Memory Search (Query-time)

**Source**: `memory-search-DIQ9kV2j.js`

Semantic search across memory files using embeddings:
- **Provider**: Configurable (ollama/nomic-embed-text in Jon's config)
- **Hybrid search**: Vector (0.7 weight) + text (0.3 weight)
- **MMR**: Maximal Marginal Relevance for diversity
- **Temporal decay**: Recent memories score higher (30-day half-life)
- **Sources**: `memory/` directory files + optionally session transcripts
- **Sync triggers**:
  - On session start (`sync.onSessionStart: true`)
  - On search (`sync.onSearch: true`)
  - File watcher (`sync.watch: true`, 1.5s debounce)
  - Post-compaction force sync (`sync.sessions.postCompactionForce: true`)

### Layer 3: Dreaming (Periodic Consolidation)

**Source**: `extensions/memory-core/src/dreaming-command.ts` and `dreaming-*.js`

A multi-phase periodic process that consolidates memories:
- **Light phase**: Quick review/cleanup
- **REM phase**: Pattern recognition across recent memories
- **Deep phase**: Writes durable entries to MEMORY.md (the long-term memory file)
- **Configurable frequency**: Via cron-like cadence
- **Promotion policy**: Entries need minimum score, recall count, and unique queries before promotion to long-term memory

This is the mechanism that moves daily notes (`memory/YYYY-MM-DD.md`) into curated long-term memory (`MEMORY.md`).

### Layer 4: Short-Term Promotion

**Source**: `short-term-promotion-*.js`

Tracks which memory entries are frequently recalled (searched for) and promotes them:
- `minScore`: Minimum relevance score
- `minRecallCount`: Minimum number of times recalled
- `minUniqueQueries`: Minimum unique search queries that found it
- Entries meeting these thresholds get promoted during dreaming sweeps

### Layer 5: Memory Prompt Section (System Prompt)

**Source**: `memory-state-BWbQIcQt.js` and `pi-embedded-DWASRjxE.js`

The memory system injects a section into the system prompt via `buildMemoryPromptSection()`:
- Tells the agent about available memory tools (`memory_search`, `memory_get`)
- Provides guidance on when/how to use memory
- Conditional on available tools

## What BotNexus Has Today

BotNexus has:
- `memory_search` and `memory_get` tools (semantic search)
- Memory config with indexing, temporal decay, topK
- SQLite memory stores (inherited from OpenClaw: `nova.sqlite`)
- Workspace memory files: `MEMORY.md`, `memory/*.md`

BotNexus is MISSING:
1. **Pre-compaction memory flush** - No automatic write-to-disk before compaction
2. **Dreaming** - No periodic consolidation of daily notes into MEMORY.md
3. **Short-term promotion** - No tracking of recall frequency
4. **Memory flush prompt** - No system prompt telling the agent to persist memories
5. **Post-compaction memory sync** - Config has `postCompactionForce: true` but may not be wired up
6. **Session-end memory flush** - No trigger on `/reset`, `/new`, or session close

## What the Agent (Nova) Could Do Today (No Platform Changes)

Even without platform-level memory flush:
1. **AGENTS.md already instructs**: "Capture what matters. Decisions, context, things to remember."
2. **Heartbeat checks**: Could write to memory during heartbeats
3. **Session startup**: Reads memory files (MEMORY.md + today/yesterday daily notes)
4. **Manual writes**: Nova can write to `memory/YYYY-MM-DD.md` at any time

The gap is that there is NO trigger or reminder to actually do it during active work. The agent gets busy with tasks and forgets to persist.

## Industry Comparison

| Feature                    | OpenClaw       | Claude Code    | BotNexus (current) |
|----------------------------|----------------|----------------|---------------------|
| Pre-compaction flush       | Automatic      | N/A            | Missing             |
| Semantic memory search     | Yes (hybrid)   | Yes (built-in) | Yes                 |
| Periodic consolidation     | Dreaming       | N/A            | Missing             |
| Session-end persistence    | Via flush       | Checkpoint     | Missing             |
| Memory in system prompt    | Yes            | Yes            | Partial             |
| Recall-based promotion     | Yes            | N/A            | Missing             |
| Daily note convention      | Yes (YYYY-MM-DD.md) | N/A       | Yes (convention)    |
