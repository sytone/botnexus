# Per-Conversation Todo

Each conversation can carry a structured **todo list** — a multi-step plan that lives as persisted
state on the conversation rather than as free-text prose. The list survives across sessions and
compaction, and the web portal renders it live in a dedicated **Todo** panel.

## Overview

The todo primitive anchors long, multi-step work so the plan does not drift or get lost mid-run:

1. **Structured state** — the plan is a list of items, each with `text` and a `status`
   (`pending`, `in_progress`, `done`, `cancelled`), persisted on the conversation row.
2. **Live portal view** — the portal **Todo** panel shows the conversation's checklist with live
   per-item status, updating in real time as the agent advances items.

A todo item is only legitimately marked `done` when the work was actually accomplished by a tool
result — narration alone cannot flip a checkbox. The most fabrication-prone agent claim ("I launched
N sub-agents") becomes visible on screen: if the panel shows `3 / 7` done while the agent claims it
finished, the mismatch is the signal.

## Viewing the Todo Panel

- **Desktop portal**: the **Todo** tab sits alongside Conversation, Workspace, Reports, and Canvas.
  It shows the conversation's checklist with a `done / total` progress count and a per-item status
  badge. When the conversation has no plan, the panel shows an empty-state placeholder.
- The panel is **read-only** today — it reflects the agent-managed plan. User editing (checking off
  or reordering items from the panel) is a planned fast-follow.

The panel hydrates its initial state from REST when a conversation is opened and then refreshes live
via a SignalR `TodoUpdated` event whenever the `todo` tool mutates the list — no manual refresh is
required.

## Close-out Warning

When a run **fully ends** (the authoritative `RunEnded` bracket) and the plan still has
`in_progress` items, the Todo panel surfaces a non-fatal **close-out warning** — the signal that the
run stopped mid-plan. This composes with the run-active bracket: `in_progress` items are expected
*during* a run, so the warning only appears once the run is idle.

It is **warn-only** by design — a visible indicator, never a hard gate. A run is never blocked from
completing, and the warning never appears for an empty list or a list whose items are all `done` /
`cancelled`.

## Todo Tool

Agents manage the list through the built-in `todo` tool. It is a per-conversation execution checklist
for the current agent loop, not a durable task system of record. Use it for detailed sequencing,
checkpoints, retries, validation, deployment, and handoff steps so work can resume accurately after
context compaction, interruption, or session continuation.

TaskNexus owns higher-level outcomes, ownership, priority, due dates, provenance, history, and
cross-agent reporting. One TaskNexus task may map to many `todo` items. Do not substitute `todo` for
durable, assigned, cross-session, or user-visible TaskNexus work, and do not create a TaskNexus task
for every implementation step unless that step independently needs long-term ownership or tracking.

| Action   | Description                                                                          |
| -------- | ------------------------------------------------------------------------------------ |
| `write`  | Replaces the whole list with a new set of items.                                     |
| `update` | Changes one item by `id` (text and/or status), or appends it if the id is new.       |
| `list`   | Returns the current items.                                                           |
| `clear`  | Empties the list.                                                                    |

Item status is one of `pending`, `in_progress`, `done`, or `cancelled`. The list persists on the
conversation across sessions and survives compaction.

## REST API

The current todo state is available over REST for hydration and inspection:

```
GET /api/agents/{agentId}/conversations/{conversationId}/todo
```

Returns the raw todo document (JSON) with HTTP 200, or HTTP 204 when the conversation has no todo
state.

## Live Updates

When the `todo` tool mutates the list, the gateway broadcasts a `TodoUpdated` SignalR event carrying
the agent id, conversation id, and the new todo document. The portal applies it to the matching
conversation and re-renders the Todo panel. Broadcasting is best-effort — a transport failure never
fails the underlying tool call.
