# Research: Context Window Visibility (/context command)

## Problem Statement

Users have no way to see what is consuming the agent's context window - how much is system prompt, conversation history, tools, skills, etc. This makes it hard to understand why compaction happens, optimize context usage, or debug context-related issues.

## Detailed research available at:
workspace://research/context-visibility-research.md

## Summary of Findings

### Context Composition (for Nova on claude-opus-4.6, 128K window)

| Component              | Est. Tokens | % of Window |
|------------------------|-------------|-------------|
| System prompt header   | ~150        | 0.1%        |
| Tool definitions (17)  | ~5,000      | 3.9%        |
| Skills list (12)       | ~1,500      | 1.2%        |
| AGENTS.md              | 2,709       | 2.1%        |
| SOUL.md                | 1,014       | 0.8%        |
| USER.md                | 660         | 0.5%        |
| IDENTITY.md            | 229         | 0.2%        |
| TOOLS.md               | 1,162       | 0.9%        |
| MEMORY.md (main only)  | 2,226       | 1.7%        |
| **Fixed overhead**     | **~14,650** | **~11.4%**  |

Leaving ~113K tokens for conversation + loaded skills + tool results.

### Industry Comparison

| Tool        | Context Visibility       | Token Count | File Breakdown | Compaction Info |
|-------------|--------------------------|:-----------:|:--------------:|:---------------:|
| Claude Code | `/context` colored grid  | via `/cost` | No             | `/compact`      |
| Aider       | `/tokens` command        | Exact       | `/ls` + `/add` | N/A             |
| Windsurf    | Automatic/hidden         | No          | No             | Automatic       |
| Cursor      | UI percentage bar        | Approx      | No             | Automatic       |
| OpenClaw    | None (internal only)     | Per-message | No             | Auto+safeguard  |

**Gap**: Nobody offers a hierarchical drill-down (overview > files > history > tools).

### Key API: Anthropic Token Counting
- `POST /v1/messages/count-tokens` - exact token count before sending
- Could be used for precise measurement if BotNexus proxies through it
