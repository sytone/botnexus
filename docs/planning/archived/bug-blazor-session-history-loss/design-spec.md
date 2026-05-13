---
id: bug-blazor-session-history-loss
title: "Blazor UI Loses Session History for Agent/Channel Combo"
type: bug
priority: medium
status: done
created: 2026-07-17
updated: 2026-07-17
author: nova
tags: [blazor, webui, sessions, ux]
---

# Design Spec: Blazor UI Loses Session History for Agent/Channel Combo

**Type**: Bug
**Priority**: Medium (continuity loss — functional but disruptive)
**Status**: Draft
**Author**: Nova (via Jon)

## Overview

When switching to the Blazor Web UI channel, prior session history from the same agent (e.g., nova on SignalR) is not carried over or accessible. The user sees a blank conversation with no history from previous sessions. Sessions appear to be tied to the channel type rather than the logical agent+user pairing.

## Current Behavior

1. User interacts with agent `nova` via SignalR channel — sessions accumulate normally
2. User opens Blazor Web UI for the same agent `nova`
3. Blazor UI shows a blank conversation — no prior history visible
4. Previous SignalR sessions are not listed or navigable from the Blazor UI
5. A new session is created, scoped to the Blazor channel

## Expected Behavior

1. When opening the Blazor UI for an agent the user has previously interacted with, prior conversation history should be visible or at least navigable in the sidebar
2. Session listing should show all sessions for the agent+user pair, regardless of originating channel
3. The most recent session should load by default, even if it originated on a different channel
4. Channel of origin may be shown as metadata but should not partition session visibility

## Impact

- **Conversational continuity lost** — user must re-establish context manually
- **Memory/context fragmentation** — agent may have relevant history in prior sessions that neither party can reference
- **UX confusion** — user expects a unified view of their interaction history with an agent

## Root Cause Analysis

Sessions are likely keyed or filtered by channel type during retrieval. Possible locations:

1. **Session query/filter** — the Blazor UI or its backing API filters sessions by channel, excluding non-Blazor sessions
2. **Session creation** — new sessions are created per-channel rather than resuming the most recent session for the agent+user pair
3. **Session visibility rules** — `feature-session-visibility` may have channel-scoped rules that inadvertently hide cross-channel sessions

## Proposed Fix

### Option A: Unify Session Listing by Agent+User

Remove or relax the channel filter in session queries so the sidebar shows all sessions for the agent+user pair.

- Session list endpoint returns sessions regardless of originating channel
- Blazor UI renders them with an optional channel badge (e.g., `SignalR`, `Blazor`)
- Default session selection picks the most recent, regardless of channel

### Option B: Cross-Channel Session Resume

When a Blazor session starts for an agent+user pair that already has sessions on another channel:

1. Detect existing sessions for the same agent+user
2. Offer to continue the most recent session rather than creating a new one
3. If a new session is created, still list prior sessions in the sidebar

### Recommendation: Option A

Unified listing is simpler, non-breaking, and matches user expectations. Option B adds resume semantics that can layer on later.

## Testing Plan

1. Create sessions with an agent via SignalR — verify they appear in Blazor UI sidebar
2. Open Blazor UI for an agent with no Blazor sessions — verify prior sessions from other channels are listed
3. Switch between channels — verify session list is consistent
4. Verify session messages render correctly regardless of originating channel
5. Verify new Blazor sessions also appear when viewed from other channels

## Scope

- **Backend**: Session query API — remove/relax channel filter
- **Frontend**: Blazor sidebar — ensure cross-channel sessions render (optional channel badge)
- **No agent/LLM changes required**

## Related

- `feature-session-visibility` — visibility rules may need channel-awareness update
- `feature-blazor-webui` — original Blazor delivery
- `feature-infinite-scrollback` — history pagination applies to cross-channel sessions too
