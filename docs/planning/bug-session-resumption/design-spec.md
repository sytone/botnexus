---
id: bug-session-resumption
title: "Session Resumption After Gateway Restart"
type: bug
priority: critical
status: reopened
created: 2026-04-10
updated: 2026-04-11
author: nova
previous_fix: "Implemented by squad 2026-04-10. Confirmed working briefly but regressed on next gateway restart."
tags: [session, gateway, continuity]
---
# Design Spec: Session Resumption After Gateway Restart
**Type**: Bug Fix
**Priority**: Critical (breaks continuity, core value proposition)
**Status**: Reopened (was resolved 2026-04-10, regressed on next cold start)
**Author**: Nova (via Jon)
## Overview
Fix session resumption so that when the BotNexus gateway restarts, agents resume their previous session with context intact rather than starting fresh.
## Requirements
### Must Have
1. After gateway restart, agent resumes the most recent active session for the channel
2. Conversation context is preserved (via compaction summary or recent history)
3. Agent can reference what was previously discussed without user re-explaining
4. Works for SignalR channel (primary use case)
5. **Cron and heartbeat sessions MUST be excluded from session matching.** These are ephemeral one-shot sessions and must not interfere with the main conversation session. The session lookup query must filter them out (e.g., `session_type != 'cron'` or `source = 'channel'`).
6. **Resumed session notification**: The agent MUST be informed that this is a resumed session (e.g., via a system message or metadata flag). The agent needs to know so it can behave appropriately â€” load memory, greet contextually, not re-introduce itself. Without this signal, the agent has no way to distinguish a resumed session from a fresh one.
### Should Have
7. Works for all channel types (cron sessions excluded - they are one-shot)
8. Graceful handling when previous session is too old (e.g., >24h)
### Nice to Have
9. Option to force a new session even when a resumable one exists
10. Session age limit configuration (auto-archive after N hours of inactivity)
## Proposed Implementation
### Session Matching on Reconnect
When a channel connects (e.g., SignalR client connects):

> **Key principle: Sessions are channel-scoped.** Multiple clients on the same channel (e.g., WebUI on laptop and desktop, or Telegram on phone and desktop) MUST connect to the same session. Never include `client_id` or `connection_id` in the match key. The only match key is `agent_id + channel_type`.

```
1. Look up most recent session WHERE:
   - agent_id = <connecting agent>
   - channel_type = <connecting channel type>
   - status = active (0)
   - session_type != 'cron'          -- exclude cron/heartbeat sessions
   - source = 'channel'              -- only match interactive channel sessions
   - updated_at > (now - session_ttl)
   NOTE: Do NOT include client_id, connection_id, or any client-specific
         identifier in the WHERE clause. Sessions are channel-scoped.
2. If found: RESUME that session
3. If not found: CREATE new session
```

### Context Injection on Resume
When resuming a session, the gateway should inject context into the LLM request:

**Context injection ordering** (this ordering is critical):
1. **System prompt** â€” the agent's base system prompt
2. **Workspace context files** â€” AGENTS.md, SOUL.md, USER.md, TOOLS.md, etc.
3. **Compaction summary** â€” the session's compaction summary (if available), injected as a system message summarizing prior conversation
4. **Recent messages** â€” the preserved N recent turns from the session history

The compaction summary goes AFTER the system prompt and workspace context but BEFORE the conversation history. This ensures the agent has its identity and instructions loaded first, then the "what happened before" context, then the actual recent messages to continue from.

**Option A: Compaction Summary (if available)**
- If the session has been compacted, inject the compaction summary as the first system message
- Plus the preserved N recent turns (per config)
**Option B: Recent History (if no compaction)**
- Load the last N messages from the session store
- Inject them as conversation history in the LLM request
- N should be bounded by token budget (e.g., last 10 messages or 20K tokens, whichever is smaller)
**Option C: Hybrid**
- Always inject compaction summary if available
- Plus last N messages since last compaction
- Plus post-compaction context refresh (AGENTS.md sections)
**Recommendation: Option C** - matches what OpenClaw does with `readPostCompactionContext()`
**Context Injection Ordering**: The injection order matters for LLM attention. The correct order is:
1. System prompt (agent identity, rules)
2. Workspace context files (AGENTS.md, SOUL.md, USER.md, TOOLS.md, etc.)
3. Compaction summary (if session was compacted)
4. Recent conversation messages (post-compaction or last N)
5. Current user message

### Multi-Client Synchronization

When multiple clients are connected to the same channel simultaneously (e.g., WebUI open on both laptop and desktop), the system must keep them in sync:

1. **Message fan-out**: When a new message is added to a session (whether from user or agent), it must be broadcast to ALL connected clients for that channel. Every client sees every message in real time.
2. **Late-join catch-up**: A client that connects after a conversation is already in progress must load the conversation history so it appears identical to what other clients see. This is the "walk into the room and read the scrollback" pattern.
3. **No split-brain**: There is exactly ONE session per `agent_id + channel_type`. Two clients on the same channel must never end up in different sessions.

This is analogous to how Telegram and Discord work â€” open the app on any device and you see the same chat with the same history.

**Implementation consideration**: The gateway needs a pub/sub or broadcast mechanism per session. When a message is added to a session, all connected clients for that `agent_id + channel_type` pair get notified (e.g., via SignalR group, WebSocket broadcast, or equivalent). The session ID can serve as the pub/sub topic/group name.

### Session State Machine
```
Created -> Active -> Compacted -> Active -> ... -> Archived
                                                      |
                          Gateway Restart ------------>|
                                                      v
                                                   Resumed (-> Active)
```
### New Session Store Fields (if needed)
```sql
ALTER TABLE sessions ADD COLUMN compaction_summary TEXT;
ALTER TABLE sessions ADD COLUMN last_compaction_at TEXT;
ALTER TABLE sessions ADD COLUMN resume_count INTEGER DEFAULT 0;
-- resume_count: telemetry only. Tracks how many times this session has been
-- resumed across gateway restarts. No behavioral logic is attached to this
-- value â€” it exists purely for diagnostics and observability (e.g., "this
-- session has been resumed 14 times, maybe we should investigate why the
-- gateway is restarting so often").
-- Note: resume_count is telemetry only - no behavioral logic is attached.
-- Used for observability and debugging session stability.
```
## API Changes
### Session Lookup Endpoint (internal)
```
GET /api/sessions/resume?agentId=nova&channelType=signalr
```
Returns the session to resume (if any) with its compaction summary and recent messages.
## Edge Cases
1. **Multiple active sessions**: Pick the most recently updated one
2. **Session too old**: Configurable TTL (default: 24h). Beyond that, start fresh
3. **Corrupted session**: If history cant be loaded, start fresh with a note
4. **Mid-compaction restart**: If gateway restarted during compaction, session may be in inconsistent state. Fall back to pre-compaction history
5. **Channel switching**: If user was on SignalR, disconnected, came back on a different channel - should still find the session (match on agent_id primarily)
6. **Cron session interference**: If a cron job runs while the main session is active, it creates its own session in the same store. The resume logic must not pick up the cron session as the "most recent" session for the channel. This is why the WHERE clause must include `session_type != 'cron'` (or `source = 'channel'`). Without this filter, a heartbeat that ran 2 minutes ago would be matched over the main conversation session that was updated an hour ago.
## Configuration
```json
{
  "gateway": {
    "sessions": {
      "resumeEnabled": true,
      "resumeTtlHours": 24,
      "resumeMaxHistoryTokens": 20000,
      "resumeMaxMessages": 20
    }
  }
}
```
## Testing Plan
1. **Happy path**: Start session, have conversation, restart gateway, verify resumption
2. **With compaction**: Converse until compaction triggers, restart, verify summary loads
3. **Old session**: Set TTL low, wait, verify new session created
4. **Multiple sessions**: Have 2+ sessions, verify most recent is resumed
5. **Clean restart**: No prior sessions, verify fresh session created normally
6. **Cron during active session**: Start a main conversation session. Trigger a cron job (which creates its own session). Restart the gateway. Verify that the main conversation session is resumed â€” NOT the cron session. This validates that `session_type != 'cron'` filtering works correctly.
7. **Multi-client sync**: Open the WebUI in two different browsers (or browser tabs). Send a message from one. Verify the message and the agent's response appear on both clients in real time. Then close one browser, continue the conversation on the other, and reopen the first browser â€” verify it catches up with the full conversation history including the messages it missed.
## Success Criteria
- After `botnexus restart`, agent can reference conversation from before the restart
- No user action required ("it just works")
- Agent acknowledges resumed context naturally (not "I have no memory of this")
