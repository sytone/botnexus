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
| **Survives restart** | Yes | Yes — session is resumed if still Active |
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
- `channelAddress` — the channel's native routing key. For channels with sub-addresses (Telegram forum topics, future Teams/Slack threads), the adapter encodes the topic into the address itself (e.g. `12345/topic:67`) — the core sees one opaque identifier.
- `mode` — controls fan-out participation (see below)

One conversation can have many bindings. This is how a single dialogue can be visible on Telegram and in the web portal at the same time.

---

## How a message is routed

When a message arrives from any channel, the gateway resolves which conversation it belongs to:

1. **Find an existing binding** — look for a `ChannelBinding` that matches `(channelType, channelAddress)`. If found, use that conversation.
2. **Default conversation** — if no binding is found, use the agent's default conversation and add this channel as a new binding.

This means **the first message from a new channel auto-attaches to the agent's default conversation**. Subsequent messages from the same address always route to the same conversation. Channels that disambiguate sub-addresses (e.g. Telegram forum topics) do so by encoding the sub-address into `channelAddress` itself — the routing rule is uniform across all channels.

---

## Conversations across sessions

Sessions come and go; the conversation persists. This has practical implications:

- **Portal refresh** — if the SignalR connection drops and reconnects, the portal re-loads the conversation history from the database. Nothing is lost.
- **New session** — the agent starts with no context, but history is still visible in the portal above the session divider.
- **Gateway restart** — the session is persisted in the database. When the next message arrives the router reloads the conversation, finds the `ActiveSessionId`, and resumes it. The agent picks up where it left off.
- **Session creation** — a new session is only created when: the conversation has no active session yet, the previous session was explicitly reset by the user, or the session was sealed/expired by compaction or a timeout.
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
Agent: my-agent          Agent: assistant
│                     │
Conversation A        Conversation B
(User ↔ my-agent)         (Jon ↔ assistant)
```

These are independent. A message sent to my-agent does not reach assistant.

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
Set `allowedUserIds` and `allowedChatIds` in the Telegram config to ensure only your ID can create bindings. See [Telegram — Security](./channels/telegram#security).
:::

### Multiple conversations with one agent

An agent can have multiple conversations — one per channel address, or one per forum topic. Each has its own history and its own session. Switching between them in the portal switches the active context.

---

## Managing conversations with the `conversation` tool

Agents that are granted the built-in **`conversation`** tool can inspect and manage conversation context programmatically — get, list, create, annotate, and archive persistent conversations. This is how an agent starts a new conversation, posts an update into an existing one, or renames/annotates a thread from inside its own loop.

### Actions

| Action | What it does |
|---|---|
| `get` | Fetch a conversation's details (title, purpose, status). |
| `list` | List conversations, optionally filtered by `status` (`active` or `archived`). |
| `new` | Create a new conversation, optionally seeded with an initial message. |
| `message` | Post a message into an existing conversation. |
| `set_title` | Rename a conversation's display title. |
| `set_purpose` | Set a conversation's purpose annotation. |
| `set` | Update title/purpose in one call. |
| `archive` | Archive a conversation (read-only; drops out of fan-out). |

### Cross-agent access

By default the tool operates on the calling agent's own conversations. The `get`, `list`, and `new` actions accept a `targetAgentId` to reach another agent's conversations, subject to the gateway's `conversationAccess` policy:

| `conversationAccess.level` | Reach |
|---|---|
| `own` | Only the calling agent's conversations. |
| `allowlist` | The calling agent's plus the agents named in `conversationAccess.allowedAgents`. |
| `all` | Any agent's conversations. |

See [Configuration — Gateway settings](./configuration#gateway-settings) for the `conversationAccess` keys.

### Which role an agent's post is recorded under (`speak_as`)

When an agent posts via the `message` or `new` action, the message is stamped into conversation history under a **role**. The rule (the *Hybrid* rule, priority order):

1. If the call sets **`speak_as`** (`"assistant"` or `"user"`), that role is honoured verbatim.
2. Otherwise, an **agent** sender defaults to **`assistant`** — the agent speaking as itself.
3. Otherwise, a **human user** sender stays **`user`** (unchanged from prior behaviour).

So an agent posting to a conversation records as **`assistant`** by default — its message appears as the agent's own turn, not as a fabricated user prompt. Set `speak_as: "user"` only for a genuine *on-behalf-of-a-user* kickoff (e.g. seeding a conversation as if the user had asked). Only `assistant` and `user` are accepted — the tool cannot record a message under a system, tool, or notification role.

---

## Threading

Some channels support native threads or topics (Telegram forum groups, Teams channels, Slack threads). BotNexus maps each thread to its own conversation; the originating channel adapter encodes the native thread identifier into the `channelAddress` so the core can treat all addresses uniformly:

```
Telegram group: chat-id-100
├── General topic     → Conversation A  (channelAddress: "-100")
├── Topic: Help       → Conversation B  (channelAddress: "-100/topic:42")
└── Topic: Dev        → Conversation C  (channelAddress: "-100/topic:99")
```

A message posted in the "Help" topic only routes to Conversation B. The other topics are independent. The composite-address format is adapter-private — different channels (Teams, Slack) may pick different encodings; the gateway never parses these strings.

The `ThreadingMode` on a binding controls outbound formatting:

| Mode | Behaviour |
|---|---|
| `Single` | One conversation per channel address (DMs, SMS, plus native topics encoded in the address) |
| `Prefix` | Prepends the conversation name to messages (SMS fallback) |

---

## Conversation lifecycle

```
Created ──► Active ──► Archived
                         (read-only)
```

- **Active** — accepts new sessions and messages
- **Archived** — read-only history, no new sessions

Archiving is manual. Archived conversations do not appear in fan-out and cannot receive new messages.

- **Desktop portal** — hover a conversation in the sidebar and click the archive (✕) button.
- **Mobile portal** — open the overflow menu (the **⋯** button in the top bar) and choose **Archive conversation**, then confirm.

For a virtual cron conversation the action is labelled **Close conversation** instead — closing hides the row from the list, and it reopens automatically when the cron job next fires.

---

## What persists and what doesn't

| Data | Persists | Where |
|---|---|---|
| Conversation history | Yes | SQLite (`~/.botnexus/sessions.sqlite`) |
| Channel bindings | Yes | SQLite |
| Session context (agent memory) | Yes — if session is still Active | SQLite (`~/.botnexus/sessions.sqlite`) |
| Streaming state | No | In-process only |
| SignalR connection | No | Reconnects on page load |
| Unread counts | No | In-process only |

After a gateway restart, conversations, history, and active sessions are fully restored from the database. The agent resumes the existing session — no context is lost unless the session was sealed or the user explicitly started a new one.

---

## Steering a running agent (portal)

While an agent is working — anywhere in its loop, including the gaps between tool
calls — the portal chat composer shows four controls instead of **Send**. They
differ by *when* your message takes effect relative to the running loop:

| Control | When it takes effect | Use it to… |
|---|---|---|
| **🔀 Steer** | At the **next turn boundary** — after the current message stream or tool batch finishes. Does not interrupt the in-flight step. | Add guidance the agent should pick up on its next LLM call without throwing away current work. |
| **Redirect** | **Immediately** — aborts the current step and steers with your message right away. | Change course now when the agent is heading the wrong way and you don't want it to finish the current step. |
| **➕ Follow Up** | **After the whole loop completes** — queued and delivered once the agent has finished all its turns, tool calls, and any continuations. | Line up the next task as a follow-up without interrupting the current one. |
| **⏹ Stop** | **Immediately** — aborts the entire loop. | Halt the agent completely. |

Steer and Follow Up messages you queue appear in the conversation's queue panel
with a **Steer** or **Follow Up** badge until they are consumed.

The controls stay visible for the **entire** run, not just while text is
streaming or a single tool is executing — so you can steer or queue at any point
between steps, including between two sequential tool calls.

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
- **Session** = agent's current context window; persists across restarts, ends only when explicitly reset or expired
- **Channel binding** = how a channel address connects to a conversation
- **Fan-out** = one response delivered to all active bindings
- **Agent** = owns one or more conversations; does not share them
