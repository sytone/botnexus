---
id: bug-steering-message-visibility
title: "Steering Messages Not Visible in Conversation Flow"
type: bug
priority: medium
status: draft
created: 2026-04-10
updated: 2026-04-10
author: nova
tags: [webui, signalr, ux]
---

# Design Spec: Steering Message Visibility in Conversation

**Type**: Bug
**Priority**: Medium (UX issue — functional but confusing)
**Status**: Draft
**Author**: Nova (via Jon)

## Overview

Steering messages (sent while the agent is mid-response) should appear inline in the conversation timeline, not just as a transient indicator above the input bar. Currently the message is delivered to the agent but never rendered in the chat UI.

## Requirements

### Must Have
1. Steering messages appear in the conversation timeline at the point they were injected
2. Messages persist visually after the agent processes them (not just transient)
3. Correct chronological ordering relative to agent responses and tool calls

### Should Have
4. Visual distinction from normal messages (e.g., "sent while responding" label, subtle styling)
5. Smooth transition: queued indicator above bar -> message appears in conversation

### Nice to Have
6. Animation/transition when the message "lands" in the conversation
7. Indicator showing whether the agent has acknowledged the steering message

## Proposed Fix

### Client-Side (SignalR WebUI)

The fix is likely in the message rendering pipeline:

```
Current flow:
  User sends message -> show above bar as "queued" -> deliver to agent -> done (message disappears)

Fixed flow:
  User sends message -> show above bar as "queued" -> deliver to agent -> append to conversation timeline -> remove queued indicator
```

### Implementation Options

**Option A: Optimistic Render + Confirm**
1. When user sends a steering message, immediately append it to the conversation view
2. Mark it as "pending" (lighter styling)
3. When server confirms delivery, update styling to "delivered"
4. This matches how most chat apps work (WhatsApp, iMessage, etc.)

**Option B: Server Echo**
1. Client sends steering message to server
2. Server injects into session AND echoes back to client as a rendered message event
3. Client renders it like any other user message
4. Simpler client logic, but requires server change

**Option C: Session State Reconciliation**
1. After steering message is sent, client re-fetches recent session messages
2. Renders any messages that are in session state but not in the current view
3. Works but could cause flicker/reordering

**Recommendation: Option A** — it's the standard pattern, fastest UX, minimal server changes.

### Message Styling

Steering messages could have a subtle visual distinction:

```
┌─────────────────────────────────────┐
│ 💬 Jon (sent while responding)      │
│ Also look at how Windsurf does it   │
└─────────────────────────────────────┘
```

Or simply render as a normal message — most chat apps don't distinguish between messages sent during vs between responses.

## Testing Plan

1. Send a message while agent is executing tools — verify it appears in conversation
2. Send multiple steering messages in sequence — verify all appear in order
3. Scroll up then send steering — verify scroll position behaves reasonably
4. Refresh the page — verify steering messages persist in conversation history
5. Check session API — verify steering messages are in the session message list

## Scope

- **Frontend only** if using Option A
- **Frontend + backend** if using Option B
- No changes needed to agent/LLM layer (steering delivery already works)

## Additional Requirement: Agent Attribution in Status Messages

### Must Have (added 2026-04-10)
6. Steering and queued message indicators must show the correct executing agent identity
7. Sub-agent tool calls must NOT be displayed as parent agent activity
8. Sub-agent name (from spawn parameters) should be visible in the status indicator
