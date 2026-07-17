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
    "urls": "http://localhost:5000"
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
