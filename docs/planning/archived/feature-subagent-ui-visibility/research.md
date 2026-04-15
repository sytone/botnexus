# Research: Sub-Agent Session Visibility in UI

## Problem Statement
When Nova (or any agent) spawns a sub-agent, the sub-agent runs in its own session. Currently, these sub-agent sessions are NOT visible in the BotNexus WebUI channels/sessions sidebar. The user has no way to:
1. See that a sub-agent is running
2. Click into its session to watch it work in real-time
3. See the sub-agent progress, tool calls, or output as it happens

## Why This Matters
- Sub-agents are a core part of BotNexus architecture - the primary agent delegates complex work to background sub-agents
- Without visibility, the user is flying blind: they do not know if the sub-agent is stuck, progressing, or finished
- The parent agent can check status via manage_subagent, but the human has no equivalent
- For debugging and trust-building, seeing what the AI is doing is essential
- Jon specifically noted: would be good to be able to click on the subagent you are running and see what it is doing

## User Experience Vision
- Sub-agent sessions should appear in the sessions/channels panel in the WebUI
- They should be visually distinguished from main sessions (e.g., nested under the parent, different icon, or labeled as Sub-agent: name)
- Clicking on a sub-agent session should open a read-only (or read-mostly) view of its conversation
- Real-time streaming: as the sub-agent works, messages/tool calls should appear live
- Status indicator: running, completed, failed
- Completed sub-agents could be collapsed/archived but still browsable

## Dependency
- bug-session-switching-ui (HIGH) - The WebUI canvas does not switch properly during active agent work. This needs to be fixed first before sub-agent session switching can work reliably.

## Industry Reference

### Claude Code (Anthropic)
- Sub-agents run in the same terminal context - user can see all output
- No separate session concept, but full transparency of what is happening

### Cursor / Windsurf
- Background agents show progress in a sidebar panel
- Can click to see details of what the agent is working on

### ChatGPT Canvas
- Artifacts and work shown in a side panel alongside the conversation
- Not exactly sub-agents, but the pattern of work happening in a visible side pane is relevant

## Implementation Considerations
1. Sub-agent sessions already exist in the session store - they just need to be surfaced in the UI
2. The session ID format includes ::subagent:: which can be used to identify and group them
3. Need WebSocket/SignalR subscription to sub-agent sessions for real-time updates
4. Parent-child relationship should be tracked (which main session spawned which sub-agent)
5. Consider: should the user be able to send messages TO a sub-agent session? (Steering is already possible via the parent agent manage_subagent tool, but direct interaction could be useful)

## Questions for the Squad
1. Are sub-agent sessions currently stored in the same sessions table? What is their schema?
2. Is there a parent_session_id or equivalent linking sub-agent sessions to their parent?
3. Can the WebUI subscribe to a sub-agent session stream via SignalR?
4. What is the session switching bug that needs fixing first? (ref: bug-session-switching-ui)

## Critical Finding: Sub-Agent Sessions Not Persisted (2026-04-10)

### Discovery
While attempting to audit what bash commands sub-agents were running, we discovered:
1. Sub-agent sessions are **entirely in-memory** - they are NOT written to the sessions or session_history tables in SQLite
2. Only 7 session IDs exist in session_history, none containing 'subagent'
3. Three sub-agents ran, completed, and returned results - but their conversations were never persisted
4. Once a sub-agent completes, its full conversation history (including all tool calls) is gone

### UI Attribution Bug
The WebUI shows sub-agent bash/tool calls as if Nova (the parent agent) is running them. This is because:
- Sub-agent tool calls may be leaking into the parent session's display
- OR the UI is showing process-level activity (bash commands on the host) without session attribution
- Either way, the user sees 'Nova is running bash' when it is actually a sub-agent

### Impact
- **No audit trail**: Cannot review what sub-agents did after the fact
- **No debugging**: If a sub-agent does something unexpected, there is no history to investigate
- **Incorrect attribution**: UI misleads user about which agent is performing actions
- **Blocks UI visibility**: feature-subagent-ui-visibility cannot work without persisted sessions

### Prerequisite for UI Feature
Sub-agent session persistence to the database is a PREREQUISITE for the UI visibility feature. The implementation order must be:
1. Persist sub-agent sessions to sessions + session_history tables (with parent_session_id and session_type = 'subagent')
2. Fix tool call attribution in UI (show which session/agent owns each tool call)
3. Then build the UI panel to browse sub-agent sessions

This changes feature-subagent-ui-visibility from a pure UI feature to a backend + UI feature.
