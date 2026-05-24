# Telegram Channel

BotNexus supports Telegram as a first-class channel via the Telegram Bot API. The bot uses long polling by default — no public URL or webhook required.

## Quick setup

### 1. Create a bot

Open Telegram and message **@BotFather**. Run `/newbot`, follow the prompts, and save the token it gives you (e.g. `1234567890:AAFxxxxxxxxxx...`).

### 2. Find your Telegram user ID

Message **@userinfobot** on Telegram — it replies with your numeric user ID (e.g. `1234567890`).

### 3. Configure the bot

Add a `channels.telegram` section to `~/.botnexus/config.json`:

```json
{
  "channels": {
    "telegram": {
      "botToken": "YOUR_BOT_TOKEN",
      "agentId": "my-agent",
      "allowedChatIds": [1234567890],
      "allowedUserIds": [1234567890],
      "pollingTimeoutSeconds": 30
    }
  }
}
```

### 4. Restart the gateway

```bash
botnexus gateway restart
```

DM your bot and it will respond via the configured agent.

---

## Configuration reference

### Single bot

```json
{
  "channels": {
    "telegram": {
      "botToken": "string (required)",
      "agentId": "string",
      "allowedChatIds": [],
      "allowedUserIds": [],
      "pollingTimeoutSeconds": 30,
      "streamingBufferMs": 500,
      "maxMessageLength": 4000,
      "processEditedMessages": false,
      "webhookUrl": "string (optional)"
    }
  }
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `botToken` | string | required | Bot token from @BotFather |
| `agentId` | string | `null` | Routes all messages to this agent. Falls back to gateway default agent if unset. |
| `allowedChatIds` | number[] | `[]` | Chat IDs allowed to interact. Empty = allow all chats. |
| `allowedUserIds` | number[] | `[]` | Sender user IDs allowed to interact. Empty = allow any sender. |
| `pollingTimeoutSeconds` | int | `30` | Long poll timeout in seconds. |
| `streamingBufferMs` | int | `500` | Flush interval for streaming deltas. |
| `maxMessageLength` | int | `4000` | Characters before splitting long responses. |
| `processEditedMessages` | bool | `false` | When `true`, edited messages are processed as new messages. |
| `webhookUrl` | string | `null` | Set to enable webhook mode instead of polling (requires a public HTTPS URL). |

### Multiple bots

Each key under `bots` is a logical bot name. Each bot maps to its own token and agent.

```json
{
  "channels": {
    "telegram": {
      "bots": {
        "personal-bot": {
          "botToken": "BOT_TOKEN_1",
          "agentId": "my-agent",
          "allowedChatIds": [1234567890],
          "allowedUserIds": [1234567890]
        },
        "helper-bot": {
          "botToken": "BOT_TOKEN_2",
          "agentId": "assistant-agent",
          "allowedChatIds": [1234567890],
          "allowedUserIds": [1234567890]
        }
      }
    }
  }
}
```

When `bots` is populated it takes precedence over the top-level single-bot fields.

---

## Security

::: warning Personal bots should always set allowedChatIds and allowedUserIds
An empty `allowedChatIds` or `allowedUserIds` list allows **anyone** who knows your bot token to interact with your agent. For a personal bot, always restrict both.
:::

### Understanding the two allow-lists

| Field | Filters on | Use for |
|---|---|---|
| `allowedChatIds` | `message.chat.id` | Restrict which conversations (DMs, groups) can use the bot |
| `allowedUserIds` | `message.from.id` | Restrict which Telegram users can send messages |

In a **DM**, `chat.id == from.id` (your user ID), so either field alone is sufficient.

In a **group**, `chat.id` is the group's ID (a large negative number) and `from.id` is the individual sender. Setting `allowedChatIds` to a group ID allows **all group members** to use the bot. Set `allowedUserIds` to restrict to specific people within the group.

**Recommended for a personal bot:**

```json
"allowedChatIds": [YOUR_USER_ID],
"allowedUserIds": [YOUR_USER_ID]
```

### What is validated

| Check | Description |
|---|---|
| `chat.id` in `allowedChatIds` | Inbound message is from an allowed conversation |
| `from.id` in `allowedUserIds` | Inbound message is from an allowed sender |
| `from` is non-null | Messages with no sender (channel posts) are rejected |
| Edited messages | Ignored by default (`processEditedMessages: false`) |

### Protecting your bot token

- Never commit your `config.json` to source control
- Use environment variable references: `"botToken": "${TELEGRAM_BOT_TOKEN}"`
- Regenerate the token in @BotFather if it is ever exposed (`/revoke`)

---

## Conversation model

Each Telegram chat maps to its own **conversation** in BotNexus. Conversations are keyed by `(telegram, channelAddress)`, where the adapter encodes forum-topic IDs into the address itself (e.g. `-1001234567890/topic:67` for topic 67 inside that supergroup).

- **DM** — one conversation per user (address = chat ID, no topic suffix)
- **Group chat** — one conversation per group
- **Forum topic** — one conversation per topic (address = `<chatId>/topic:<topicId>`)

Conversations persist across gateway restarts. History is stored in `~/.botnexus/sessions.sqlite`.

If the SignalR portal is also connected, messages from Telegram appear there too (fan-out). Messages sent from the portal fan out to Telegram. Streaming replies follow the same routing rule and now land in the originating forum topic (previous behaviour incorrectly routed streamed replies to the root chat).

---

## Polling vs webhook

| | Long polling (default) | Webhook |
|---|---|---|
| Public URL required | No | Yes (HTTPS + valid cert) |
| Works behind NAT/VPN | Yes | Only with reverse proxy |
| Latency | ~0.5s | ~0ms |
| Setup complexity | None | Requires ingress config |

Long polling is the right default for self-hosted or home-lab setups. Set `webhookUrl` only if you already have a stable public HTTPS endpoint.

---

## Troubleshooting

**Bot not responding**

Check gateway logs:
```bash
tail -f ~/.botnexus/logs/botnexus-$(date +%Y%m%d%H).log | grep -i telegram
```

Common causes:
- `botToken` is wrong — check with `curl "https://api.telegram.org/bot<token>/getMe"`
- Your user ID is not in `allowedChatIds` or `allowedUserIds`
- Gateway isn't running — check `botnexus gateway status`

**Messages not appearing in the portal**

The SignalR connection may have dropped. Refresh the portal page — history loads from the database.

**Bot responding to wrong thread in a group**

Forum-topic routing is keyed off the composite channel address (`<chatId>/topic:<topicId>`) that the adapter writes on inbound. Ensure you are on the latest release; the topic field is now preserved end-to-end including for streaming replies.
