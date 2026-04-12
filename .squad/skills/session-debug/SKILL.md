---
name: session-debug
description: Fast session investigation without hunting through files and databases. Use when debugging session state, resuming issues, message routing, checking what sessions exist for an agent, or reviewing gateway logs for errors.
metadata:
  domain: debugging
  confidence: high
  source: "earned — production debugging of real session issues (commits 28a0329, 5d4bf4f, 0fa175a)"
---

# Session Debug

## When to Use

- User reports a session isn't loading, resuming, or showing history
- Debugging session state (status, type, channel, participants)
- Investigating message routing issues (wrong session received messages)
- Checking what sessions exist for an agent
- Reviewing gateway logs for errors
- Any session-related bug investigation

## Quick Reference

### Storage Locations

| Component | Path | Format |
|-----------|------|--------|
| **Sessions DB** | `~/.botnexus/sessions.db` | SQLite |
| **File sessions** | `~/.botnexus/sessions/*.jsonl` + `*.meta.json` | JSONL + JSON |
| **Logs** | `~/.botnexus/logs/botnexus-*.log` | Text (Serilog, hourly rotation) |
| **Agent memory** | `~/.botnexus/agents/{name}/data/memory.sqlite` | SQLite |
| **Cron store** | `~/.botnexus/cron.sqlite` | SQLite |
| **Config** | `~/.botnexus/config.json` | JSON |

### DB Schema

```
sessions: id, agent_id, channel_type, caller_id, session_type,
          participants_json, status, metadata, created_at, updated_at

session_history: id (autoincrement), session_id, role, content,
                 timestamp, tool_name, tool_call_id, is_compaction_summary
```

### Session Status Values
- `Active` — in use
- `Suspended` — paused
- `Sealed` — completed/archived (was "Closed" pre-DDD)
- `Expired` — past retention window

### Session Type Values
- `user-agent` — normal user chat (visible in WebUI)
- `agent-sub-agent` — sub-agent worker session
- `agent-agent` — peer agent conversation
- `soul` — daily soul/heartbeat session
- `cron` — scheduled task session
- `None` — legacy session (pre-DDD, treated as user-agent by inference)

### Channel Key Aliases
`signalr` = `web chat` = `web-chat` = `webchat` (all resolve to `signalr`)

## How to Investigate

### Step 1: Use the helper scripts

**Always use the Python helper scripts** in `.squad/skills/session-debug/` instead of manual queries. They handle path resolution, DB connection, and output formatting in a single tool call.

```powershell
# Find a session by partial ID
python .squad/skills/session-debug/session-lookup.py 5b0cea38

# List all sessions for an agent
python .squad/skills/session-debug/session-lookup.py --agent nova

# Show recent sessions across all agents
python .squad/skills/session-debug/session-lookup.py --recent 10

# Show session with full history
python .squad/skills/session-debug/session-lookup.py 5b0cea38 --history

# Check session visibility (what SubscribeAll would return)
python .squad/skills/session-debug/session-lookup.py --agent nova --visible

# Search gateway logs
python .squad/skills/session-debug/log-search.py "error" --last-hours 2
python .squad/skills/session-debug/log-search.py "5b0cea38" --last-hours 4
python .squad/skills/session-debug/log-search.py "SubscribeAll" --level ERR
```

### Step 2: Common Investigations

**Session not resuming after restart:**
1. `python .squad/skills/session-debug/session-lookup.py {partial-id}` — is it in the DB?
2. Check `status` — must be `Active` (not Sealed/Expired)
3. Check `session_type` — must be `user-agent` (not cron/soul)
4. Check `channel_type` — should be `signalr` for WebUI
5. Check history count — 0 means empty session (won't show in channel history)
6. `python .squad/skills/session-debug/log-search.py "SubscribeAll"` — did the client subscribe?

**Messages going to wrong session:**
1. `python .squad/skills/session-debug/log-search.py "SendMessage"` — check routing
2. Check `contentDelta` events include `sessionId` (fix in commit 28a0329)
3. Check if `routeEvent` is dropping events (console.warn in browser)

**Session not visible in sidebar:**
1. `python .squad/skills/session-debug/session-lookup.py --agent {name} --visible`
2. Visibility requires: `session_type = user-agent`, `status = Active|Suspended|Sealed`, within retention window
3. Sealed sessions hidden when newer Active sibling exists for same channel

## Confidence

Level: `high` — Based on production debugging of real session issues (commits 28a0329, 5d4bf4f, 0fa175a).
