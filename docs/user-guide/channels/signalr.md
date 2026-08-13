# SignalR Channel

The SignalR channel provides real-time bidirectional communication between agents and the BotNexus web portal (Blazor WebAssembly client). It is the primary channel for interactive browser-based agent conversations.

## Overview

The SignalR channel is built into the gateway and does not require external service setup. It uses ASP.NET Core SignalR (which negotiates WebSocket transport automatically) for low-latency streaming.

## Features

- Real-time message streaming (token-by-token)
- Tool call progress indicators
- Conversation switching
- Agent selection
- Sub-agent session visibility
- Steering queue display
- Canvas rendering
- File upload support

## Configuration

The SignalR channel is enabled by default when the gateway starts. The Blazor portal connects automatically at the configured gateway URL.

```json
{
  "gateway": {
    "listenUrl": "http://localhost:5005"
  }
}
```

No additional channel-specific configuration is needed. The SignalR hub is exposed at `/hub/gateway`.

## Architecture

```text
Browser (Blazor WASM)
    ↕ SignalR (WebSocket transport)
Gateway (/hub/gateway)
    ↕
SignalRChannelAdapter → GatewayHost → Agent Session
```

### Key Components

| Component | Role |
|-----------|------|
| `GatewayHub` | ASP.NET Core SignalR hub handling client connections |
| `SignalRChannelAdapter` | Implements `IChannelAdapter` for SignalR message routing |
| `SignalRAgentChangeNotifier` | Pushes agent state changes to connected clients |
| `SignalRConversationChangeNotifier` | Pushes conversation updates to clients |
| `SignalRCanvasNotifier` | Delivers canvas HTML renders to the portal |
| `SteeringSignalRBridge` | Routes steering queue events to the portal |
| `SubAgentSignalRBridge` | Exposes sub-agent session events to the portal |

## Portal Features

The Blazor portal communicates exclusively via the SignalR channel:

- **Chat panel**: Send messages, view streaming responses, tool call summaries
- **Agent switcher**: Change active agent mid-conversation
- **Conversation list**: Create, switch, and manage conversations
- **Steering queue**: View pending steering entries per conversation
- **Debug panel**: Inspect session state and history
- **Canvas tab**: View agent-rendered HTML content
- **PWA support**: Offline caching, installable as a desktop/mobile app

### Top bar layout

The active agent's identity (name and status) and the SignalR connection status indicator
live in the portal **top bar** rather than inside the agent panel. This keeps the
connection state visible from every page and frees vertical space in the sidebar.

![Compact density at 1920x1080](/images/portal-density/after-compact-desktop-1920.png)

Long labels truncate to a single line with an ellipsis rather than wrapping or growing the
row, and control characters in a name or description are normalised to single spaces:

![A 300-character label truncating on one line](/images/portal-density/adversarial-long300-desktop-1920.png)

The left sidebar behaves as follows:

- **Tools** subnav is **collapsed by default**; expand it to list the active agent's
  tools. When an agent has no tools configured the subnav simply stays empty — no
  placeholder "No tools configured" row is rendered.
- **My Sections** is collapsible, so the sections list can be folded away when not in use.

### Interface density

The portal ships a **density preference** that switches every chrome spacing token at
once. Change it in **Settings → Interface density**; the new value applies immediately and
is persisted per browser (localStorage, via `IPortalPreferencesService`) — it is not synced
to the server or shared between devices.

![Comfortable density at 1920x1080](/images/portal-density/after-comfortable-desktop-1920.png)

| Preset | Value | Use it when |
|--------|-------|-------------|
| **Compact** (default) | `compact` | You want maximum content on screen — tighter top bar, tab strip, sidebar rows and group headers, smaller chrome text. |
| **Comfortable** | `comfortable` | You want roomier hit targets and larger chrome text, e.g. on a large display or for accessibility. |

The preset is emitted as a `data-density="compact"` / `data-density="comfortable"`
attribute on the app shell, which selects a block of `--density-*` CSS custom properties in
`app.css`. Those tokens drive:

- chrome row padding (`--density-row-pad-y`, `--density-row-pad-x`)
- inline gap between chrome items (`--density-gap`)
- minimum height of interactive rows and bars (`--density-control-h`, `--density-bar-h`)
- chrome font sizes (`--density-font-sm`, `--density-font-xs`)
- sidebar nav item padding and sub-item indent (`--density-nav-pad-y`,
  `--density-nav-pad-x`, `--density-subnav-indent`)
- conversation/section group header padding (`--density-group-pad-y`)

Because everything reads from the same token set, a theme can override the density values
in one place, and any unrecognised or hand-edited stored value is normalised back to
`compact` rather than emitting an unknown `data-density` value.

## Comparison with Other Channels

| Feature | SignalR | Telegram | Service Bus |
|---------|--------|----------|-------------|
| Transport | SignalR (WebSocket) | HTTPS polling | AMQP |
| Latency | Very low | Medium | Low |
| Streaming | Token-by-token | Message-level | Message-level |
| Rich UI | Full (Blazor) | Limited (Telegram formatting) | None (headless) |
| Auth | Cookie/Token | Bot token | Connection string |
| Multi-user | Yes (per-connection) | Yes (per-chat) | Yes (per-subscription) |

## Related

- [WebUI Connection](/development/webui-connection) — Developer docs on the SignalR connection lifecycle
- [SignalR Hub Contract](/signalr-hub-contract) — Hub method and event reference
- [Telegram Channel](/user-guide/channels/telegram) — Alternative channel
- [Service Bus Channel](/user-guide/channels/service-bus) — Alternative channel


## Chat attachments

The desktop portal accepts up to 8 draft attachments through the file picker, plus images pasted into the message box. Individual files and the combined draft are limited to 7 MB so base64 encoding remains within the gateway's default 10 MB SignalR frame limit. Text files are decoded into textual content parts; images and other files retain their MIME type, filename, and bytes as binary content parts.
