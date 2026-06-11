# Canvas

The canvas system gives agents the ability to render rich HTML content and maintain persistent key-value state scoped to conversations. Canvas output appears in a dedicated panel in the web portal.

## Overview

Canvas provides two complementary capabilities:

1. **HTML Rendering** — Agents can push arbitrary HTML to a visual panel (dashboards, charts, interactive UIs)
2. **State Management** — Persistent key-value store scoped to each conversation, accessible from both agent-side tools and client-side JavaScript

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

Canvas HTML rendered in the iframe can communicate back to the server via `window.postMessage`. The portal's `canvasBridge.js` intercepts messages and forwards state updates through the SignalR connection:

```javascript
// Inside canvas iframe
window.parent.postMessage({
  type: 'canvas-state',
  action: 'set',
  key: 'counter',
  value: 42
}, '*');
```

## Configuration

Canvas is a built-in tool — no additional configuration is required. The canvas panel appears automatically in the web portal when an agent uses the `canvas` tool.

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
