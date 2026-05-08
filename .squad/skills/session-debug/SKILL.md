---
name: session-debug
description: Fast session investigation using BotNexus Probe CLI (preferred) or Python helper scripts. Use when debugging session state, resuming issues, message routing, checking what sessions exist for an agent, reviewing gateway logs for errors, or correlating events across logs/sessions/traces.
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

### Step 1: Use BotNexus Probe (preferred)

**Prefer the Probe CLI** (`tools/BotNexus.Probe`) for all diagnostic queries. It parses logs, sessions, and OTEL traces with a single tool, outputs structured JSON for agent consumption, and supports correlation-first search across all data sources.

```powershell
# Correlate — search by ANY ID across logs, sessions, and traces (THE KEY COMMAND)
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- correlate 5b0cea38

# Search logs by session, correlation, agent, or level
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- logs --session 5b0cea38 --take 50
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- logs --level error --take 20
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- logs --correlation xyz789
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- logs --agent myagent --take 50
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- logs --search "SubscribeAll" --level error

# List and inspect sessions
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- sessions
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- session 5b0cea38 --take 20
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- session 5b0cea38 --search "error"

# List log files
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- files

# Check live Gateway status and data (requires running Gateway)
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- gateway status --gateway http://localhost:5005
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- gateway logs --gateway http://localhost:5005 --limit 50
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- gateway agents --gateway http://localhost:5005
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- gateway sessions --gateway http://localhost:5005

# Add --text for human-readable output instead of JSON
dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- logs --level error --text
```

**Probe also has a web UI** for interactive investigation. Launch with:
```powershell
.\scripts\start-probe.ps1                                         # connects to Gateway on localhost:5005
.\scripts\start-probe.ps1 -Port 5051 -OtlpPort 4318              # custom port + OTLP traces
```
Then open `http://localhost:5050` — Dashboard, Logs, Sessions, Traces, Live Activity, and Correlate pages.

### Step 2: Fallback — Python helper scripts

If Probe isn't built or you need direct DB access (e.g., querying SQLite session metadata not in JSONL files), use the Python helper scripts in `.squad/skills/session-debug/`:

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

### Step 3: Common Investigations

**Session not resuming after restart:**
1. `dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- correlate {partial-id}` — find all traces of the session
2. `python .squad/skills/session-debug/session-lookup.py {partial-id}` — check DB status directly
3. Check `status` — must be `Active` (not Sealed/Expired)
4. Check `session_type` — must be `user-agent` (not cron/soul)
5. Check `channel_type` — should be `signalr` for WebUI
6. Check history count — 0 means empty session (won't show in channel history)
7. `dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- logs --search "SubscribeAll"` — did the client subscribe?

**Messages going to wrong session:**
1. `dotnet run --project tools/BotNexus.Probe/src/BotNexus.Probe -- logs --search "SendMessage"` — check routing
2. Check `contentDelta` events include `sessionId` (fix in commit 28a0329)
3. Check if `routeEvent` is dropping events (console.warn in browser)

**Session not visible in sidebar:**
1. `python .squad/skills/session-debug/session-lookup.py --agent {name} --visible`
2. Visibility requires: `session_type = user-agent`, `status = Active|Suspended|Sealed`, within retention window
3. Sealed sessions hidden when newer Active sibling exists for same channel

## Confidence

Level: `high` — Based on production debugging of real session issues (commits 28a0329, 5d4bf4f, 0fa175a).

## History paging regression check

When UI refresh loses recent turns, verify /api/conversations/{conversationId}/history semantics first: offset=0 must return the newest page, and positive offsets must page backward from newest history.

