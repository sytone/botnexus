---
id: feature-context-visibility
title: "Context Window Visibility (/context command)"
type: feature
priority: medium
status: superseded
superseded_by: feature-context-diagnostics
created: 2026-04-10
updated: 2026-07-18
author: nova
tags: [context, tokens, ux, diagnostics]
depends_on: []
---

# Design Spec: Context Window Visibility (/context command)

**Type**: Feature
**Priority**: Medium (developer experience, debugging aid)
**Status**: Draft
**Author**: Nova (via Jon)

## Overview

Expose context window usage to users via a `/context` command system. Can be implemented in two layers: agent-side (Nova estimates from what she can observe) and platform-side (BotNexus provides actual token counts).

## Approach: Two-Layer Implementation

### Layer 1: Agent-Side (can ship immediately, no platform changes)

Nova recognizes `/context` in messages and runs diagnostic commands to estimate usage:
- Measures workspace files with `wc -c`
- Reads config for window size and compaction settings
- Checks session info for message count
- Applies char/4 heuristic for token estimation

**Accuracy**: ~85% (good enough for budgeting, not billing)

### Layer 2: Platform-Side (requires BotNexus changes)

BotNexus tracks actual token counts from provider responses and exposes them:
- Per-turn token usage (from Claude API `usage` field in responses)
- Cumulative session tokens
- System prompt token count (measured once at session start)
- Tool schema token count

**Accuracy**: 100% (uses actual provider data)

## Command Specification

### `/context` - Overview

Shows high-level context usage breakdown:
- Total window size and current usage (%)
- Category breakdown: system prompt, workspace files, tools, skills, conversation
- Available headroom
- Distance to compaction threshold
- Health status indicator

### `/context files` - Workspace File Detail

Shows each injected file with its token cost:
- File name, char count, estimated tokens, loaded status
- Total injected vs total available
- Optimization tips (e.g., "AGENTS.md is 2.7K tokens - consider trimming")

### `/context history` - Conversation State

Shows conversation-specific data:
- Message count, estimated history tokens
- Compaction status (compacted? summary size?)
- Preserved turns count
- Session age, last activity
- Estimated exchanges until next compaction

### `/context tools` - Tool Schema Costs

Shows tool definition overhead:
- Built-in tool count and estimated tokens
- MCP server connections and tool counts
- Loaded skill count and token cost per skill

## Platform API (Layer 2)

### New Tool: `context_info` (optional, for accurate data)

If BotNexus wants to provide this natively:

```json
{
  "name": "context_info",
  "description": "Get current context window usage information",
  "parameters": {
    "detail": {
      "type": "string",
      "enum": ["overview", "files", "history", "tools"],
      "default": "overview"
    }
  }
}
```

Returns actual token counts from the gateway's perspective.

### Response Headers (alternative approach)

Include context metadata in every LLM response:
```
X-BotNexus-Context-Tokens: 34521
X-BotNexus-Context-Window: 128000
X-BotNexus-Context-Ratio: 0.27
```

Agent can read these to provide accurate data without a separate tool.

## Implementation Phases

### Phase 1: Agent-Side Estimates (no platform changes)
- Nova implements `/context` pattern matching
- Shell commands to measure files
- Config parsing for limits
- Session API for message counts
- Formatted output with tables

### Phase 2: Platform Token Tracking
- BotNexus captures `usage` from provider responses
- Stores cumulative token counts in session metadata
- Exposes via `context_info` tool or response headers

### Phase 3: Proactive Alerts
- Nova checks context usage during heartbeats
- Warns when approaching compaction threshold
- Suggests optimization actions

## Detailed research and mockups:
See workspace://research/context-visibility-research.md for full command output mockups and token estimation methodology.
