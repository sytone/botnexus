# Canvas

The canvas system gives agents the ability to render rich HTML content and maintain persistent key-value state scoped to conversations. Canvas output appears in a dedicated panel in the web portal.

## Overview

Canvas provides two complementary capabilities:

1. **HTML Rendering** — Agents can push arbitrary HTML to a visual panel (dashboards, charts, interactive UIs)
2. **State Management** — Persistent key-value store scoped to each conversation, accessible from both agent-side tools and client-side JavaScript

## Viewing the Canvas

How you open the canvas depends on the portal layout:

- **Desktop portal**: the canvas is a dedicated panel that is always visible alongside the conversation. When the agent has not published any HTML yet, the panel shows an empty-state placeholder.
- **Mobile portal**: open the overflow menu (the **⋯** button in the top bar) and choose **Canvas**. The action is always available so you can open the canvas at any time — it shows the same empty-state placeholder until the agent publishes HTML. A small dot next to **Canvas** indicates that the current conversation already has canvas content.

## Canvas Tool

Agents interact with canvas through the built-in `canvas` tool.

### Actions

| Action | Description |
|--------|-------------|
| `render` | Replace the canvas panel HTML content |
| `clear` | Clear all canvas panel content |
| `set_state` | Store a key-value pair in conversation state |
| `get_state` | Retrieve state (single key or all keys) |
| `clear_state` | Remove a state key, or clear all state |

### Rendering HTML

```json
{
  "action": "render",
  "html": "<h1>Dashboard</h1><p>Active agents: 3</p>"
}
```

The HTML is rendered inside a sandboxed iframe in the web portal. Use `action: "clear"` to reset the panel.

### State Management

State is persisted in the conversation store (SQLite) and survives session restarts.

**Set a value:**
```json
{
  "action": "set_state",
  "key": "theme",
  "value": "dark"
}
```

**Get a single key:**
```json
{
  "action": "get_state",
  "key": "theme"
}
```

**Get all state:**
```json
{
  "action": "get_state"
}
```

**Clear a single key:**
```json
{
  "action": "clear_state",
  "key": "theme"
}
```

**Clear all state:**
```json
{
  "action": "clear_state"
}
```

## REST API

Canvas state is also accessible via the conversations REST API:

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/conversations/{id}/canvas-state` | Get all canvas state for a conversation |
| `GET` | `/api/conversations/{id}/canvas-state/{key}` | Get a specific state key |
| `POST` | `/api/conversations/{id}/canvas-state/{key}` | Set a state key (body is the JSON value) |
| `DELETE` | `/api/conversations/{id}/canvas-state/{key}` | Delete a specific state key |

## SignalR Notifications

When canvas state changes (via tool or REST API), a `CanvasStateChanged` event is broadcast to connected clients over SignalR. This enables real-time reactive UIs:

```typescript
// Client-side SignalR handler
connection.on("CanvasStateChanged", (conversationId, key, value) => {
  // Update local state reactively
});
```

## postMessage Bridge

Canvas HTML rendered in the iframe gets a `window.canvasState` SDK injected synchronously before any
user script runs. It has six verbs — five state verbs plus `submitToAgent`:

| Verb | Signature | Description |
|------|-----------|-------------|
| `get` | `get(key)` | Read one state key |
| `set` | `set(key, value)` | Write one state key |
| `delete` | `delete(key)` | Remove one state key |
| `getAll` | `getAll()` | Read the whole state object |
| `clear` | `clear()` | Remove all state keys |
| `submitToAgent` | `submitToAgent({ prompt, instructions })` | Hand control back to the agent |

All six return Promises.

### `submitToAgent` (issue #2449)

The five state verbs are write-only from the agent's point of view: a user can fill in a canvas form
and nothing tells the agent about it. `submitToAgent` closes that loop. It injects a **user message**
into the conversation that owns the canvas, using prompt text the agent chose when it rendered the
canvas. The agent then reads the submitted values out of canvas state as normal.

```html
<button id="submit">Submit review</button>
<script>
  document.getElementById('submit').addEventListener('click', async () => {
    await canvasState.set('rating', document.getElementById('rating').value);
    await canvasState.set('comments', document.getElementById('comments').value);

    await canvasState.submitToAgent({
      prompt: 'The user has completed the review form.',
      instructions: 'Read canvas state keys "rating" and "comments", then summarise the review.'
    });
  });
</script>
```

**Usage rules.** `submitToAgent` is **user-initiated only**. Wire it to a button click or an
explicit user action. Do **not** call it from a timer or interval, from a render or reactive path,
or automatically on load. There is deliberately no throttle, no min-interval and no in-flight
tracking: this is a documented instruction, not enforced machinery, and an agent that ignores it
costs the user turns.

The `prompt` carries **instructions, not payload**. Canvas data belongs in canvas state - write it
with `set()` and tell the agent which keys to read back with `get_state`. Nothing inspects the
prompt's contents; this rule is enforced by documentation.

**Security model.** This verb lets iframe-hosted content create a conversation turn, so it is
deliberately narrow:

- **Conversation scoping.** The target conversation is derived from the canvas panel's own binding.
  There is no conversation-id parameter; a conversation id present in the postMessage payload is
  ignored, and the derived id is re-checked against the hosting agent's own conversation set. A
  canvas can target only the conversation hosting it - never another conversation, never a session,
  never another agent.
- **Role integrity.** The message is recorded as a genuine **user** turn. There is no role parameter
  to forge. Control characters in the prompt are collapsed to spaces so the text cannot fabricate
  extra transcript lines or trailer-shaped suffixes. Canvas-submitted text is user-origin content
  and is never treated as trusted instruction.
- **Provenance.** The submission is routed through a dedicated `SubmitCanvasPrompt` hub verb, and
  the **server** stamps `MessageKind.CanvasSubmission` on the resulting turn from the verb that was
  invoked. This reuses the issue #2300 provenance vocabulary at the message level - the same way a
  cron run is identified by `ConversationSource.Cron` - so the transcript can answer "why does this
  message exist?" with a typed field. Provenance is deliberately **not** a marker in the message
  text: any message can contain any literal, so a text marker proves nothing and is unverifiable.
- **Bounds.** `prompt` is capped at 2000 characters and `instructions` at 1000. These are
  **arbitrary guardrails, not a contract** - sensible values that comfortably fit instruction text
  and would only be hit by someone inlining a data dump. Longer values are rejected rather than
  truncated.
- **Mid-turn behaviour (degraded, pending #2438).** If the conversation already has an active agent
  turn, the submission is **rejected** (the Promise rejects with `Agent is already running; try
  again when the current turn finishes.`) rather than queued. Inbound messages arriving mid-run are
  currently dropped by the gateway (issue #2388), and the follow-up queue that would defer the click
  (issue #2438) does not exist yet - so queueing today would silently lose the user's click. An
  explicit rejection lets the canvas show a retry affordance instead. Once #2438 lands this path
  should enqueue rather than refuse, and the behaviour improves without a canvas-side change.

Rejections surface as a rejected Promise, so wrap the call:

```javascript
try {
  await canvasState.submitToAgent({ prompt: 'Form complete.' });
} catch (err) {
  document.getElementById('status').textContent = err.message;
}
```

## Configuration

Canvas is a built-in tool — no additional configuration is required. The canvas panel appears automatically in the web portal when an agent uses the `canvas` tool.

### Canvas deep links (`gateway.publicBaseUrl`)

A successful `render` returns a `canvasUrl` deep link so the agent can tell the user where to look:

```
https://portal.example.com/agent/{agentId}/conversation/{conversationId}?tab=canvas
```

The `?tab=canvas` query selects the Canvas pane. Both ids are URL-encoded. The tool guidance instructs
the agent to include this link in its reply — and to still carry the substance of the answer in the
reply, because on Signal or Telegram the canvas is not visible in-line and the link is the only way
to reach it.

**The external base URL comes from one place: `gateway.publicBaseUrl`.**

```bash
botnexus config set gateway.publicBaseUrl https://portal.example.com
```

It is deliberately separate from `gateway.listenUrl`: the gateway commonly binds a wildcard or
loopback address while users reach the portal through a tunnel or reverse proxy on a different host.

Resolution order, and what happens when nothing resolves:

| `gateway.publicBaseUrl` | `gateway.listenUrl` | Result |
| --- | --- | --- |
| set | any | link built from `publicBaseUrl` |
| unset | concrete host (`http://localhost:5005`) | link built from `listenUrl` |
| unset | wildcard (`http://+:5005`, `http://0.0.0.0:5005`, `http://[::]:5005`) | **no link**, reason stated |
| unset | unset | **no link**, reason stated |

When no link can be built the render still succeeds and the result says why, naming
`gateway.publicBaseUrl`. No partial or guessed URL is ever emitted — a link pointing at the wrong
host is worse than no link. A link is returned only for `render`; `clear`, `set_state`, `get_state`
and `clear_state` never carry one.

### State Persistence

Canvas state is stored in the conversation store:
- **SQLite**: `canvas_state` side-table with composite primary key `(conversation_id, key)`
- **In-Memory**: `ConcurrentDictionary` per conversation (development/testing)
- **File**: Persisted as `CanvasState` property in conversation JSON files

## Use Cases

- **Dashboards** — Render live metrics, charts, and status panels
- **Interactive forms** — Collect structured input from users via HTML forms
- **Configuration UIs** — Visual editors that persist settings via state
- **Progress tracking** — Multi-step workflows with visual progress indicators
- **Agent memory** — Agents can read state from previous sessions to maintain context

## See Also

- [Extensions](../user-guide/extensions.md) — how extensions integrate with the platform
- [SignalR Hub Contract](../signalr-hub-contract.md) — real-time event protocol
- [Sub-Agent Spawning](sub-agent-spawning.md) — sub-agents can inherit parent canvas context
