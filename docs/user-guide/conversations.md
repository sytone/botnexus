# Conversations

A **conversation** is the fundamental unit of context in BotNexus. It is a persistent, named thread of messages that:

- belongs to one agent
- spans any number of sessions
- can be reached from multiple channels simultaneously
- persists across gateway restarts

Understanding conversations is the key to understanding how messages flow between users, agents, and channels.

---

## Core concepts

### Conversation vs session

These are distinct things that are often confused.

| | Conversation | Session |
|---|---|---|
| **What it is** | The full history of a dialogue | The agent's active context window |
| **Scope** | Permanent (until archived) | Temporary — ends when reset or expired |
| **Contains** | All messages, all sessions | Recent messages the agent is currently aware of |
| **Survives restart** | Yes | No (new session on restart) |
| **Visible in portal** | Always | Only the current one |

A conversation can contain many sessions. When you click **New session**, the agent's context is cleared but the conversation history is not. A `─── New session started ───` divider marks the boundary in the portal.

```
Conversation: "Support thread with Jon"
├── Session 1  [2024-01-10]  ← agent remembered these messages
│   ├── user: How do I configure X?
│   └── agent: Here's how...
├── ── New session started ──
└── Session 2  [2024-01-15]  ← agent starts fresh, history still visible
    ├── user: What did we discuss about X?
    └── agent: I don't have that context — could you recap?
```

### Channel bindings

A **channel binding** connects a conversation to a specific address on a specific channel. Each binding records:

- `channelType` — e.g. `telegram`, `signalr`, `teams`
- `channelAddress` — e.g. a Telegram chat ID, a Teams channel ID
- `threadId` — optional; for forum topics, Teams threads, etc.
- `mode` — controls fan-out participation (see below)

One conversation can have many bindings. This is how a single dialogue can be visible on Telegram and in the web portal at the same time.

---

## How a message is routed

When a message arrives from any channel, the gateway resolves which conversation it belongs to:

1. **Find an existing binding** — look for a `ChannelBinding` that matches `(channelType, channelAddress, threadId)`. If found, use that conversation.
2. **Thread fallback** — if no binding found and `threadId` is non-null, create a new conversation for this thread.
3. **Default conversation** — if no binding found and `threadId` is null, use the agent's default conversation and add this channel as a new binding.

This means **the first message from a new channel auto-attaches to the agent's default conversation**. Subsequent messages from the same address always route to the same conversation.

---

## Conversations across sessions

Sessions come and go; the conversation persists. This has practical implications:

- **Portal refresh** — if the SignalR connection drops and reconnects, the portal re-loads the conversation history from the database. Nothing is lost.
- **New session** — the agent starts with no context, but history is still visible in the portal above the session divider.
- **Compaction** — `/compact` summarises the current session to reduce token usage while preserving the full history in the database.
- **Multiple sessions** — an agent can only have one active session per conversation at a time. Starting a new session seals the previous one.

---

## Conversations between channels (fan-out)

When an agent responds, the response is delivered to **all active bindings** on that conversation — not just the channel the message came from. This is called **fan-out**.

```
User sends from Telegram
        │
        ▼
  Conversation: "Jon's chat"
  ├── Binding: telegram / chat-id-123   ← inbound arrived here
  ├── Binding: signalr / browser-tab    ← fan-out delivers here too
  └── Binding: teams / channel-abc      ← and here
        │
        ▼
  Agent responds once
        │
  ┌─────┴──────────────────────────────────┐
  │                                        │
  ▼                                        ▼
Telegram reply                    Portal + Teams update
```

The originating binding is **excluded from fan-out** — it receives the direct response instead, preventing duplicates.

### Binding modes

Each binding has a `mode` that controls how it participates in fan-out:

| Mode | Inbound | Outbound fan-out | Use case |
|---|---|---|---|
| `Interactive` | ✓ | ✓ | Normal two-way channel |
| `NotifyOnly` | ✗ | ✓ | Receive responses but not send (e.g. a monitoring dashboard) |
| `Muted` | ✗ | ✗ | Silenced — no traffic in or out |

Stale bindings (e.g. closed browser tabs) should be `Muted` to avoid delivering to dead connections.

---

## Conversations between agents

Each conversation belongs to exactly **one agent**. Agents do not share conversations.

If you want two agents to collaborate, you use one of:

- **Sub-agents** — one agent spawns another as a tool call. The sub-agent's output feeds back into the parent's session.
- **Separate conversations** — the user opens a new conversation with a different agent. There is no automatic cross-agent memory sharing.
- **Shared memory** — agents can read from shared memory files/databases if configured, but this is outside the conversation model.

```
Agent: larry          Agent: assistant
│                     │
Conversation A        Conversation B
(Jon ↔ larry)         (Jon ↔ assistant)
```

These are independent. A message sent to larry does not reach assistant.

---

## Conversations between users and agents

### Default conversation

Every agent has one **default conversation**. This is:
- Created automatically the first time any channel connects to the agent
- The landing point for any channel address that hasn't been seen before
- Marked `IsDefault: true` in the database

For a personal bot (one user, one agent), you will typically have exactly one default conversation and all your channels are bound to it.

### Multiple users

If multiple users message the same Telegram bot, each user's chat ID is a different `channelAddress`. The router will:
- Give the first user the default conversation
- Create new conversations for subsequent users (if no existing binding matches)

::: tip For a personal bot
Set `allowedUserIds` and `allowedChatIds` in the Telegram config to ensure only your ID can create bindings. See [Telegram — Security](./telegram#security).
:::

### Multiple conversations with one agent

An agent can have multiple conversations — one per channel address, or one per forum topic. Each has its own history and its own session. Switching between them in the portal switches the active context.

---

## Threading

Some channels support native threads or topics (Telegram forum groups, Teams channels, Slack threads). BotNexus maps each thread to its own conversation with a `threadId` binding:

```
Telegram group: chat-id-100
├── General topic     → Conversation A  (threadId: null)
├── Topic: Help       → Conversation B  (threadId: "42")
└── Topic: Dev        → Conversation C  (threadId: "99")
```

A message posted in the "Help" topic only routes to Conversation B. The other topics are independent.

The `ThreadingMode` on a binding controls this:

| Mode | Behaviour |
|---|---|
| `Single` | One conversation per channel address (DMs, SMS) |
| `NativeThread` | Maps to a native thread or topic |
| `Prefix` | Prepends the conversation name to messages (SMS fallback) |

---

## Conversation lifecycle

```
Created ──► Active ──► Archived
                         (read-only)
```

- **Active** — accepts new sessions and messages
- **Archived** — read-only history, no new sessions

Archiving is currently manual. Archived conversations do not appear in fan-out and cannot receive new messages.

---

## What persists and what doesn't

| Data | Persists | Where |
|---|---|---|
| Conversation history | Yes | SQLite (`~/.botnexus/sessions.sqlite`) |
| Channel bindings | Yes | SQLite |
| Session context (agent memory) | No | In-process only |
| Streaming state | No | In-process only |
| SignalR connection | No | Reconnects on page load |
| Unread counts | No | In-process only |

After a gateway restart, conversations and their history are fully restored. The agent starts a new session with no prior context unless memory/RAG is configured.

---

## Summary

```
User (Telegram) ──► Channel Binding ─┐
                                      ├──► Conversation ──► Session ──► Agent
User (Portal)   ──► Channel Binding ─┘         │
                                               fan-out
                                          ┌────┴────┐
                                    Telegram    Portal
```

- **Conversation** = persistent history, lives forever
- **Session** = agent's current context window, temporary
- **Channel binding** = how a channel address connects to a conversation
- **Fan-out** = one response delivered to all active bindings
- **Agent** = owns one or more conversations; does not share them
