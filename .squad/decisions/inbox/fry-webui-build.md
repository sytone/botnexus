# Decision: WebUI Rebuild for New Gateway API

**Date:** 2026-04-03  
**Author:** Fry (Web Dev)  
**Status:** Implemented  

## Context

The original WebUI (now in `archive/src/BotNexus.WebUI/`) was built for the old Gateway architecture with different API endpoints (`/api/channels`, activity streams, extension panels). The new Gateway has a simplified API surface focused on agent-based chat with WebSocket streaming.

## Decision

Rebuilt `src/BotNexus.WebUI/` from scratch targeting the new Gateway API:

### API Surface
- **WebSocket**: `/ws?agent={agentId}&session={sessionId}` — streaming with `message_start`, `content_delta`, `tool_start/end`, `message_end`
- **REST**: `GET /api/agents`, `GET /api/sessions`, `GET /api/sessions/{id}`, `POST /api/chat` (non-streaming fallback)

### Architecture Choices
1. **Pure HTML/CSS/JS** — no build tools, consistent with project convention
2. **IIFE pattern** in `app.js` — all state encapsulated, no global leaks
3. **WebSocket-first** with REST fallback — best UX for streaming, graceful degradation
4. **Exponential backoff reconnection** — prevents server hammering on disconnect
5. **Ping keepalive** (30s interval) — maintains connection through proxies/load balancers
6. **MSBuild copy targets** — wwwroot files copied to Gateway.Api output, no manual steps

### Removed from Archive
- Extensions panel (no longer in API)
- Channels list (no longer in API)
- Activity monitor (no longer in API)
- Agent form modal (simplified — agents are read-only from the UI)
- Command palette (simplify for now)

### Retained from Archive
- Dark theme CSS variables and visual design language
- Sidebar + main area layout pattern
- Tool call modal for inspecting tool details
- Tool visibility toggle
- Markdown rendering via marked.js + DOMPurify
- Thinking indicator during agent processing

## Impact
- Gateway.Api.csproj gains a ProjectReference to BotNexus.WebUI and MSBuild copy targets
- No existing source code modified (only .csproj addition)
- Build verified: 0 errors, all tests passing
