# Matrix Channel

The Matrix channel extension gives each agent its **own account on a Matrix homeserver**, so a human
can DM an agent from any Matrix client (Element, FluffyChat, …) or invite it into a room, and the
agent answers as a first-class Matrix user rather than through a shared gateway bot.

## Overview

| Property | Value |
|----------|-------|
| Extension ID | `botnexus-matrix` |
| Channel key | `matrix` |
| Source | `BotNexus.Extensions.Channels.Matrix` |
| DI extension | `AddBotNexusMatrixChannel(...)` |
| Config section | `channels:matrix` |
| Protocol | Matrix Client-Server API v3 (REST + JSON, no SDK dependency) |

## Scope

This is the **first vertical slice** of the Matrix adapter. The following are implemented:

- Per-agent Matrix account configuration (homeserver, user ID, access token)
- `/sync` long-poll loop with `since`-token continuity and a bounded failure circuit breaker
- Send and receive `m.room.message` events
- Markdown → `org.matrix.custom.html` formatting
- Streaming responses via in-place `m.replace` edits
- Typing indicators while a turn is running
- Auto-join on room invite (configurable)
- Room and user allow-lists

The following are **deliberately deferred** and are not implemented here:

- **End-to-end encryption** — requires device-key management (libolm/vodozemac)
- **Federation-specific trust decisions** — remote-homeserver verification policy
- **Media** — image/file upload and download via the Matrix content repository
- **Read receipts** and Matrix **Spaces** mapping

The `IMatrixClient` seam carries only the endpoints this slice uses, so a deferred capability is a
missing interface member rather than a silent runtime no-op.

## Configuration

Bind under `channels:matrix`. Each entry under `agents` is one Matrix account owned by one agent.

```json
{
  "channels": {
    "matrix": {
      "homeserver": "https://matrix.example.com",
      "syncTimeoutMs": 30000,
      "streamingBufferMs": 750,
      "agents": {
        "farnsworth": {
          "userId": "@farnsworth:example.com",
          "accessToken": "syt_...",
          "agentId": "farnsworth",
          "autoJoin": true
        },
        "nova": {
          "userId": "@nova:example.com",
          "accessToken": "syt_...",
          "autoJoin": false
        }
      }
    }
  }
}
```

### Top-level keys

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `homeserver` | string | — | Base URL of the homeserver shared by every account. |
| `syncTimeoutMs` | integer | `30000` | `/sync` long-poll timeout. A non-positive value falls back to the default rather than busy-spinning. |
| `streamingBufferMs` | integer | `750` | Minimum interval between streaming edits. `0` means edit on every delta. |
| `agents` | map | — | Per-agent accounts, keyed by agent name. |

### Per-account keys

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `userId` | string | — | **Required.** Fully-qualified Matrix user ID, e.g. `@farnsworth:example.com`. |
| `accessToken` | string | — | **Required.** Matrix access token. Sensitive; stored and displayed masked. |
| `agentId` | string | the map key | BotNexus agent inbound messages route to. |
| `homeserver` | string | the shared value | Per-account homeserver override. |
| `autoJoin` | boolean | `true` | Whether the account accepts room invites automatically. |
| `allowedRoomIds` | string[] | empty | Room allow-list. Empty permits all joined rooms. |
| `allowedUserIds` | string[] | empty | Sender allow-list. Empty permits all senders. |

An account missing its homeserver, user ID or access token is **skipped with a warning** — one bad
entry does not prevent the other accounts from starting.

### Obtaining an access token

Create one Matrix user per agent on your homeserver, then log in once to mint a long-lived token:

```bash
curl -XPOST https://matrix.example.com/_matrix/client/v3/login \
  -d '{"type":"m.login.password","identifier":{"type":"m.id.user","user":"farnsworth"},"password":"..."}'
```

The `access_token` in the response goes into `accessToken`. The adapter sends it as a
`Authorization: Bearer` header, never as a query parameter, so it does not land in homeserver access
logs.

## How it works

- **Inbound.** Each account runs its own `/sync` long poll. `m.room.message` events in joined rooms
  are translated into an `InboundMessage` and dispatched through `IChannelDispatcher`. The account's
  own messages are suppressed (they echo back on the next sync), as are `m.replace` edits — an edit
  is not a new user turn.
- **Outbound.** `SendAsync` decodes the room from the channel address and sends an `m.room.message`
  with a plain `body` plus an HTML `formatted_body` when the Markdown actually produced markup.
- **Streaming.** The first delta sends a message; subsequent deltas edit that event in place via an
  `m.replace` relation carrying the full accumulated text under `m.new_content`. Edits are rate
  limited by `streamingBufferMs`.
- **Channel address.** The room ID and optional thread root are folded into the opaque
  `ChannelAddress` as `<roomId>` or `<roomId>/thread:<eventId>`. A Matrix room ID cannot contain a
  forward slash, so `/` is a safe delimiter for this channel.

### Capability flags

| Flag | Value | Notes |
|------|-------|-------|
| `SupportsStreaming` | `true` | Send-then-edit via `m.replace`. |
| `SupportsThinkingDisplay` | `false` | Thinking deltas are not rendered into rooms. |
| `SupportsToolDisplay` | `false` | Tool activity is not rendered into rooms. |
| `SupportsInboundImages` | `false` | Media needs the content repository — deferred. |
| `StripsRuntimeContext` | `true` | Matrix rooms are a user-visible surface. |

## Failure handling

The sync loop is bounded by the shared `ChannelLoopCircuitBreaker`. A **terminal** fault — an HTTP
401/403 from a revoked or invalid access token — parks the loop with a single error line rather than
retrying a fault that cannot clear. Transient faults (429, 5xx, transport errors) retry with bounded
exponential backoff.

The `since` token is advanced **only after** a batch has been fully processed, so a crash mid-batch
replays that batch rather than skipping the events it contained.

## Registration

```csharp
services.Configure<MatrixChannelOptions>(config.GetSection("channels:matrix"));
services.AddBotNexusMatrixChannel();
```

The default `IMatrixClientFactory` is registered with `TryAddSingleton`, so a host that needs custom
transport behaviour can register its own implementation beforehand and keep it.
