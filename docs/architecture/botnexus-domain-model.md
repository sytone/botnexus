# BotNexus Domain Model

This document defines the conceptual domain model for the BotNexus platform — every object, relationship, and interaction pattern that governs how agents exist, communicate, and grow. It is the source of truth for what the system *means*, independent of how any particular layer implements it.

If you're building or refactoring BotNexus code, this is the map. The codebase is the territory.

---

## Domain Overview

BotNexus is a multi-agent platform where intelligent agents live, think, communicate, and remember. The domain model is built around these core concepts:

| Domain Object     | Human Analogy                                                    |
|-------------------|------------------------------------------------------------------|
| **Agent**         | A person — their résumé (Descriptor) and their living self (Instance) |
| **World**         | The city or environment where the person lives and works         |
| **Soul**          | Personality, voice, and values                                   |
| **Identity**      | Name, appearance, and self-concept                               |
| **Session**       | A conversation, a task, a meeting — a bounded interaction        |
| **Existence**     | The totality of a person's experiences and history               |
| **Channel**       | A phone, an email client, a chat app — a way to reach the outside world |
| **User**          | A human interacting with an agent                                |
| **Memory**        | What a person remembers — short-term, long-term, and the ability to recall |

```
+--------------------------------------------------------------+
|                           WORLD                              |
|                         (Gateway)                            |
|                                                              |
|  +------------------------------------+   +------------+     |
|  |              AGENT                 |   |  Channels  |     |
|  |                                    |   | +--------+ |     |
|  |  +-----------+  +--------------+   |   | | Signal | |     |
|  |  | Descriptor|  |   Instance   |   |   | | Telegr | |     |
|  |  |  (config) |  |  (runtime)   |   |<--+ |  TUI   | |     |
|  |  +-----------+  +------+-------+   |   | +--------+ |     |
|  |                        |           |   +------------+     |
|  |  +------+  +--------+  |          |                       |
|  |  | Soul |  |Identity|  |          |   +------------+      |
|  |  +------+  +--------+  |          |   |   Users    |      |
|  |                        |          |<--+            |      |
|  |  +---------------------v-------+  |   +------------+      |
|  |  |         EXISTENCE           |  |                       |
|  |  |  +---------+  +--------+    |  |                       |
|  |  |  |Sessions |  | Memory |    |  |                       |
|  |  |  +---------+  +--------+    |  |                      |
|  |  +-----------------------------+  |                       |
|  +------------------------------------+                      |
|                                                              |
|  +------------------------------------+                      |
|  |         Other Agents ...           |                      |
|  +------------------------------------+                      |
|                                                              |
|  +------------------------------------+                      |
|  |            Locations               |                      |
|  |  (file system, APIs, services)     |                      |
|  +------------------------------------+                      |
+--------------------------------------------------------------+
         |                         ^
         |    cross-world comms    |
         v                         |
+--------------------------------------------------------------+
|                       ANOTHER WORLD                          |
|                     (Remote Gateway)                         |
+--------------------------------------------------------------+
```

---

## Agent

An Agent is the central domain object in BotNexus. It represents an intelligent entity that can think, communicate, remember, and act. Agents are the *who* of the system — everything else exists in service of them.

An Agent is composed of two facets:

### Descriptor (The Résumé)

The Descriptor is the static definition of an agent — who it is on paper. It captures everything you'd need to know to *configure* an agent before it ever wakes up:

- **Agent ID** — unique identifier, the agent's permanent name in the system
- **Display Name** — human-readable name for UI and multi-agent contexts
- **Description** — what this agent does and what it's for
- **Model** — the default LLM model the agent thinks with
- **API Provider** — which inference backend powers this agent
- **System Prompt** — the foundational instructions that shape the agent's behavior
- **Tool Access** — which tools the agent can use
- **Allowed Models** — which models the agent is permitted to switch to
- **Sub-Agent Access** — which other agents this agent can call upon
- **World Configuration** — how the agent is isolated and where it runs (see [World](#world))
- **Session Limits** — maximum concurrent sessions
- **Memory Configuration** — whether memory is enabled and how it behaves
- **Session Access Level** — what sessions the agent can see beyond its own
- **Extension Configuration** — settings for any extensions the agent uses
- **Metadata** — extensible bag for tags, ownership, and other properties

Think of the Descriptor as a job posting: it defines the role, the tools, the access, and the expectations. It exists whether or not the agent is running.

### Instance (The Living Agent)

The Instance is the embodiment of the agent being alive — the runtime manifestation that can interact, think, and introspect. When a Descriptor is activated for a specific session, an Instance is born.

- **Instance ID** — unique composite of agent + session, identifying this specific incarnation
- **Agent ID** — which Descriptor this instance was created from
- **Session ID** — which session this instance is bound to
- **Status** — the current runtime state: Starting, Idle, Running, Stopping, Stopped, or Faulted
- **Created At** — when this instance came alive
- **Last Active At** — when it last processed something
- **World Strategy** — which isolation environment this instance runs within

An agent can have many simultaneous Instances — one for each active session. This is like a person being in multiple conversations at once: same identity, same personality, different contexts.

### The Relationship Between Descriptor and Instance

The Descriptor is the blueprint; the Instance is the building. You can read a Descriptor and understand what an agent *would* do. You can observe an Instance and see what an agent *is* doing. Both are facets of the same Agent concept, split for practical reasons: configuration doesn't change at runtime, and runtime state doesn't belong in configuration.

---

## World

A World is the environment in which an agent exists — its place of being. Every agent lives in a World, and the World shapes what the agent can see, touch, and reach.

In concrete terms, a World is the Gateway that hosts the agent. But the concept is richer than "which server am I on" — it encompasses the agent's entire environmental context.

### Properties of a World

- **Gateway** — the BotNexus Gateway process that manages this World
- **Agents** — the set of agents that live in this World (a World may host one agent or many)
- **Locations** — the resources accessible from this World (see below)
- **Boundaries** — what the World allows and restricts

### Locations

A Location is any resource an agent can access from its World. Locations are a first-class concept because an agent's capabilities are defined not just by its tools, but by what those tools can reach:

- **File System Paths** — workspace directories, shared drives, mounted volumes
- **APIs and Services** — REST endpoints, MCP servers, databases, cloud services
- **Remote Nodes** — satellite processes or services that extend the World's reach (e.g., a coding agent running in a container, a remote tool host)

Some Locations are inherent to the World (the local file system, the Gateway's own APIs). Others are explicitly granted — an agent might have access to a specific SharePoint site, a particular Azure subscription, or a set of GitHub repositories. The Descriptor's tool access and extension configuration determine which Locations an agent can reach.

### Shared vs. Isolated Worlds

A World may host a single agent in splendid isolation, or it may be a shared environment where multiple agents coexist:

- **Isolated World** — one agent, one Gateway. The agent has full run of the environment. Common for personal assistants or security-sensitive workloads.
- **Shared World** — multiple agents on the same Gateway. Agents share the infrastructure but have separate workspaces, sessions, and memory. Communication between co-located agents is local and fast.

### Agent Execution Strategies

How an agent runs within its World is determined by its execution strategy (configured via the Descriptor). This controls the isolation boundary between the agent's code and the Gateway process:

- **In-Process** — the agent runs directly inside the Gateway process. Fastest, no isolation. The default for simple deployments.
- **Sandbox** — the agent runs in a restricted process with limited permissions. A middle ground between speed and safety.
- **Container** — the agent runs in a Docker container. Strong isolation, suitable for untrusted or resource-intensive agents.
- **Remote** — the agent delegates to a remote service. The Instance is a proxy; the real work happens elsewhere.

Satellite nodes — remote processes that extend a World's capabilities without being full Worlds themselves — are considered part of the World they serve. A coding agent container spawned by a Gateway is a satellite of that Gateway's World, not a separate World.

### Cross-World Access

An agent's World is its home, but some work requires reaching beyond it. If an agent needs to communicate with agents on a different Gateway, or access resources in another World, it needs explicit permission. Cross-world access is never implicit — it is configured, auditable, and has different session semantics than local communication (see [Cross-World Conversations](#cross-world-conversations)).

---

## Soul

The Soul is defined in a `SOUL.md` file and is where the agent's voice, personality, and stance live. BotNexus injects it into the system prompt for every session, giving it real weight over how the agent communicates. If an agent sounds bland, overly corporate, or lacks character, `SOUL.md` is the file to fix.

### What Belongs in SOUL.md

`SOUL.md` should contain the attributes that change how the agent *feels* to talk to:

- **Tone** — formal, casual, dry, warm, direct
- **Opinions** — strong takes rather than hedging with "it depends"
- **Brevity** — how concise or verbose the agent should be by default
- **Humor** — whether wit is welcome and to what degree
- **Boundaries** — what the agent will push back on or refuse
- **Bluntness** — default level of directness

### What Does NOT Belong in SOUL.md

- Life stories or elaborate backstories
- Changelogs or version history
- Security policies or compliance rules
- Long walls of vague vibes with no behavioral effect

Short beats long. Sharp beats vague. Good rules sound like "have a take", "skip filler", "call out bad ideas early". Bad rules sound like "maintain professionalism at all times" or "ensure a positive and supportive experience" — that's how you get mush.

### Relationship to Other Files

`SOUL.md` is purely for voice, stance, and style. Operating rules, tool permissions, and behavioral logic belong in the Agent Descriptor or `AGENTS.md`. Personality is not permission to be sloppy — if an agent works in shared channels, public replies, or customer surfaces, the tone in `SOUL.md` still needs to fit the room.

### Soul Session

Separate from the `SOUL.md` file, each agent also has a **Soul Session** — a special internal session that is only accessible to the agent itself. It is the agent's private inner space for reflection, learning, and growth. The user cannot see or interact with this session.

A new Soul Session is created daily and automatically sealed at the end of each day. This gives the agent a daily reflection and learning cycle. The Soul Session is also where the **Heartbeat** runs — driven by the `HEARTBEAT.md` file, the agent follows its heartbeat instructions to interact with itself, perform maintenance, and proactively reach out to users and other agents in other sessions as needed.

The Soul Session is not created via a Channel — it is an internal trigger, and like all internally triggered sessions, it is non-interactive.

---

## Identity

Identity is the agent's self-concept — the answer to "who are you?" It is defined in an `IDENTITY.md` file at the workspace root and established during the agent's **bootstrap ritual** (its first-run setup). Unlike the Soul, which governs *how* the agent communicates, Identity governs *what* the agent is and how it presents itself to the world.

### IDENTITY.md Fields

- **Name** — the agent's chosen name, used in channel messages and multi-agent routing
- **Archetype** — what kind of entity the agent considers itself (AI assistant, familiar, digital colleague, something weirder). This anchors the agent's self-model and influences how it frames its own capabilities and limitations.
- **Vibe** — a short description of the agent's general impression (sharp, warm, chaotic, calm). Overlaps with Soul but serves as a quick identity summary rather than detailed behavioral rules.
  - "Vibe is the elevator pitch; Soul is the full playbook."
- **Emoji** — a signature emoji used as a visual shorthand across channels and UI surfaces
- **Avatar** — a visual representation of the agent (workspace-relative path, URL, or data URI)

### How Identity Differs from Soul

| Aspect   | Identity (`IDENTITY.md`)                  | Soul (`SOUL.md`)                             |
|----------|-------------------------------------------|----------------------------------------------|
| Purpose  | Who the agent *is*                        | How the agent *communicates*                 |
| Content  | Name, archetype, emoji, avatar            | Tone, opinions, brevity, humor, boundaries   |
| Analogy  | A person's name and appearance            | A person's personality and mannerisms        |
| Scope    | Public-facing metadata + self-concept     | Behavioral rules for conversation            |

### Bootstrap Ritual

Identity is established when an agent first comes online. During the bootstrap ritual, the agent is guided through defining its own identity — picking a name, choosing an emoji, deciding what kind of entity it is. This is a one-time event; after the ritual completes, the `IDENTITY.md` file persists and is loaded into the system prompt for every session.

The identity fields also map to the agent's runtime configuration, making them available for multi-agent routing, channel message formatting, and UI rendering. In multi-agent environments, each agent's identity ensures users and other agents can distinguish between them across shared channels.

Identity and Soul will evolve over time after the initial bootstrap.

---

## Session

A Session is a bounded window of interaction. It is the container for a conversation, a task, a piece of focused work, or an internal process. Every time an agent does anything — responds to a user, runs a cron job, reflects in its Soul Session, talks to another agent — it happens within a Session.

### Session Properties

- **Session ID** — unique identifier for this session
- **Agent ID** — which agent this session belongs to
- **Channel Type** — the external channel that created this session (null for internally triggered sessions)
- **Caller ID** — the identifier of the external caller within the channel (user ID, chat ID, etc.)
- **Participants** — who is in this session (one or more users, one or more agents)
- **IsInteractive** — whether external participants can inject messages into the session
- **Status** — the session's lifecycle state (see below)
- **History** — the ordered conversation entries that make up this session's record
- **Created At / Updated At** — temporal bookmarks
- **Metadata** — extensible properties for session-specific context

### Session Lifecycle

Sessions move through a clear lifecycle:

- **Active** — the session is live and available for new messages. The agent can process interactions and the session accumulates history.
- **Suspended** — the session is temporarily paused but may be resumed. This is the state for sessions that aren't currently in use but haven't been concluded — like a conversation you'll come back to after lunch.
- **Sealed** — the session is explicitly closed and no more interactions should be added. This is the terminal state. Once sealed, the agent knows to stop contributing to this session and can begin processing its contents for memory and learning. The term "sealed" is deliberate — it implies the session's record is now complete and preserved, like sealing an envelope.

When the Gateway starts up, any Active or Suspended sessions should be resumed so the agent is ready to respond. The Gateway should never resume a Sealed session.

### Session Types

Sessions are distinguished by who created them and who participates:

| Type                     | Created By       | Participants            | Interactive | Description                                    |
|--------------------------|------------------|-------------------------|-------------|------------------------------------------------|
| **User–Agent**           | Channel message  | User + Agent            | Yes         | A human talking to an agent                    |
| **Agent–Self**           | Agent (internal) | Agent only              | No          | An agent multitasking — doing focused work privately |
| **Agent–Sub-Agent**      | Agent (spawn)    | Parent Agent + Worker   | No          | An agent using disposable workers              |
| **Agent–Agent**          | Agent (call)     | Two registered agents   | No          | Two agents having a conversation               |
| **Soul Session**         | Internal trigger | Agent only              | No          | Daily reflection, heartbeat, introspection     |
| **Cron Session**         | Scheduler        | Agent only              | No          | Scheduled task execution                       |

All internally triggered sessions (Soul, Cron, Agent–Self, Agent–Sub-Agent, Agent–Agent) are non-interactive — users cannot inject messages into them. The WebUI may allow users to *inspect* these sessions and read what happened, but never to participate.

### Session Sealing

The `/new` command is the primary user-facing way to seal a session and start a fresh one. Internally, sessions may also be sealed when:

- A cron job completes its work
- An agent-to-agent conversation achieves its objective
- A sub-agent finishes and reports back
- A Soul Session's daily cycle ends
- A session expires due to retention policy

---

## Existence

The Existence is the totality of an agent's experience — the collection of every Session it has ever participated in. Just as a human's life is the sum of their conversations, work, reflections, and encounters, an agent's Existence is the sum of its Sessions.

The Existence serves several purposes:

- **Continuity** — the agent can look back at past sessions for context it needs now
- **Learning** — by reviewing past interactions, the agent can identify patterns, mistakes, and growth
- **Memory Source** — Memory (both Long-Term and Daily) is always a distillation of what happened in Sessions. The Existence is the raw material; Memory is the refined product.

An agent can pull information from its Existence at varying levels of granularity. It might scan Memory for a quick answer, or dive back into a specific past session for the full detail of a conversation. This layered access — Memory for breadth, Existence for depth — gives agents a powerful system for remembering and learning over time.

Every session type becomes part of the agent's Existence — user conversations, cron jobs, soul reflections, sub-agent delegations, and agent-to-agent conversations. They all contribute to the agent's accumulated experience.

---

## Agent Communication

Agents communicate through four distinct patterns, each with different session semantics, participant models, and use cases. Understanding these patterns is essential — they define how work gets distributed, how information flows between agents, and how the system scales.

### Agent to Self (Multitasking)

An agent spins up a private session to focus on a piece of work while maintaining its main conversation. This is the equivalent of a human multitasking — you're in a meeting, but you're also drafting that email in the background.

- **Session type**: Agent–Self
- **Participants**: The agent alone
- **Interactive**: No
- **Lifecycle**: Created when the agent decides to do background work; sealed when the work is complete
- **Existence**: Becomes part of the agent's Existence like any other session

The agent creates these sessions through its own initiative. There is no external caller, no channel, and no user involvement. The agent defines the work, executes it, and consumes the result.

### Agent to Generic Sub-Agent (Disposable Workers)

An agent spawns stateless workers that have no soul, no history, and no identity of their own. They are completely disposable — created for a task, given a prompt by the parent, and discarded when done. Think of them as personal assistants to the agent: "go research this", "go write this code", "go summarize that document".

- **Session type**: Agent–Sub-Agent
- **Participants**: Parent agent + spawned worker
- **Interactive**: No
- **Lifecycle**: Created by the parent agent via a spawn request; sealed when the worker completes, times out, or is killed
- **Identity**: The worker runs using the parent's Descriptor (same agent ID, same tools) but with the parent's custom prompt as its instructions. It has no soul, no workspace files, no memory.
- **Existence**: The worker's session becomes part of the parent agent's Existence. The worker itself has no Existence — it's ephemeral.
- **Completion**: When the worker finishes, a completion summary is delivered back to the parent's session as a follow-up message

Sub-agents are the primary mechanism for delegation. An agent that receives a complex request can break it into pieces, spawn workers for each piece, and synthesize the results. Workers can be limited by turn count, timeout, tool access, and model selection.

### Agent to Agent (Conversation)

An agent calls upon another *registered* agent — one with its own Descriptor, Soul, Identity, and Existence. This is fundamentally different from spawning a disposable worker. This is two colleagues having a conversation.

- **Session type**: Agent–Agent (a **Conversation**)
- **Participants**: The initiating agent (acting as the "user" role) and the target agent (acting as the "agent" role) — exactly like a human interacting with an agent
- **Interactive**: No — users cannot inject into agent-to-agent conversations
- **Lifecycle**: A new session is created for each conversation. Once the objective is met, the session is sealed. If the agents need to talk again later, a *new* session is created — just as humans have separate conversations over time rather than one infinitely long one.
- **Existence**: The conversation session becomes part of *both* agents' Existence. Each agent can look back on what was discussed, what was agreed, what was learned.
- **Cycle Detection**: The system prevents recursive call chains (Agent A calls Agent B calls Agent A) and enforces maximum call-chain depth

This pattern enables specialization. An orchestrator agent can call a research agent for deep dives, a code agent for implementation, a compliance agent for reviews — each bringing their own expertise, their own soul, their own perspective. The conversation is richer because both participants are fully realized agents.

### Cross-World Conversations

When agents on different Gateways need to communicate, the pattern extends across World boundaries. The mechanics change, but the concept remains the same — two agents having a conversation.

- **Session creation**: The initiating agent creates a conversation session on *their* side. The target agent's World creates a session on *their* side to track their participation.
- **Session ownership**: Sessions are NOT shared across Worlds. Each agent has access only to their own session as a participant. This is a fundamental boundary — Worlds do not share session storage.
- **Existence**: Both sessions become part of their respective agent's Existence. Agent A's Existence contains their side of the conversation; Agent B's Existence contains theirs.
- **Interactive**: No — cross-world conversations follow the same non-interactive rule as local agent-to-agent conversations
- **Permission**: Cross-world communication requires explicit configuration. An agent cannot reach into another World uninvited.

Cross-world conversations are the mechanism for federation — multiple BotNexus deployments cooperating without merging into a single system. Each World maintains sovereignty over its agents, sessions, and data.

### Communication Pattern Summary

```
+-------------------------------------------------------------------+
|                   Agent Communication Patterns                    |
+-------------------------------------------------------------------+
|                                                                   |
|  1. Agent to Self             2. Agent to Sub-Agent               |
|                                                                   |
|  +----------+                 +----------+     +---------+        |
|  |  Agent   |-----------+    |  Agent   |---->| Worker  |         |
|  |  (main)  |  private  |    | (parent) |     |(no soul)|         |
|  +----------+  session  |    +----------+     +---------+         |
|                +--------v+        ^  completion  |                |
|                | focused |        +--------------+                |
|                |  work   |                                        |
|                +---------+                                        |
|                                                                   |
|  3. Agent to Agent            4. Cross-World                      |
|                                                                   |
|  +----------+                 +----------+     +----------+       |
|  | Agent A  |<--------------->| Agent A  |     | Agent B  |       |
|  | ("user") |  conversation   | World 1  |<--->| World 2  |       |
|  +----------+                 +----------+     +----------+       |
|  +----------+                  session A != session B             |
|  | Agent B  |                  each agent owns their side         |
|  |("agent") |                                                     |
|  +----------+                                                     |
|                                                                   |
+-------------------------------------------------------------------+
```--

## Channel

A Channel is an external communication method — the way an agent talks to and hears from the outside world. Channels are the bridges between agents and their users.

### Examples

- **SignalR** — the built-in real-time web channel
- **Telegram** — messaging via Telegram Bot API
- **TUI** — terminal-based interactive interface
- **Discord**, **Slack**, **Signal**, **Email**, **SMS** — future channel adapters

### Channel Capabilities

Channels are not all equal. Each Channel declares what it can do:

- **Streaming** — can it render incremental content as the agent thinks, or only complete messages?
- **Steering** — can the user inject mid-response to redirect the agent?
- **Follow-up** — can the agent append to an existing response?
- **Thinking Display** — can the channel render the agent's reasoning process?
- **Tool Display** — can the channel show tool call activity?

These capabilities influence how the agent's output is presented but do not change the agent's behavior or the session model.

### Channels vs. Internal Triggers

Channels are distinct from internal triggers. **Cron jobs, Soul Sessions, and Heartbeats are NOT channels** — they are internal processes that create sessions without any external communication.

Think of it like a human: when you think to yourself or a reminder fires in your head, you're not *communicating* — you're processing internally. Other people might observe the resulting actions, but there was no conversation. The same applies to agents: a cron job may trigger work that produces visible output, but the trigger itself is not a channel.

Sessions created by internal triggers are non-interactive. The WebUI allows users to inspect these sessions and see the interactions that took place, but they cannot participate in them.

---

## User

A User is a human who interacts with an Agent. Users are the *people* in the system — the ones who ask questions, give instructions, review output, and ultimately judge whether the agent is being helpful.

### User Properties

- **Identity** — who the user is (name, email, external identifier)
- **Channel Presence** — which channels the user communicates through
- **Sessions** — the set of sessions the user participates in (across agents and channels)

### User Interaction Model

A User interacts with agents through Channels. Each channel-agent combination creates a Session where the user and agent converse. A user can:

- Have multiple sessions with the same agent (different conversations, different topics)
- Have sessions with different agents (talking to a personal assistant, then a coding agent)
- See non-interactive sessions in the WebUI (inspecting cron results, soul session activity) without being able to participate in them

Users are the interactive participants. When a session is marked as interactive, it means a user can contribute to it. When a session is non-interactive, the user is an observer at most.

---

## Memory

Memory is how an agent persists knowledge across sessions. Without Memory, every session would be a blank slate — the agent would have no context, no history, no learning. Memory is what gives agents continuity.

### Memory Types

- **Long-Term Memory** — the agent's accumulated knowledge over its entire existence. This is the distilled, curated collection of everything the agent has learned — facts, preferences, lessons, relationships, patterns. Long-term memory is stored in `MEMORY.md` and is the agent's most important file. It's what a human would call "what I know."

- **Daily Memory** — a day's worth of raw notes and observations, stored in `memory/YYYY-MM-DD.md` files. Daily memory captures what happened today — sessions, decisions, corrections, discoveries. At the end of each day (or periodically), the agent reviews its daily notes and distills the important bits into long-term memory. Daily memory is the journal; long-term memory is the wisdom.

### Semantic Memory

Beyond file-based memory, BotNexus supports semantic memory — an indexed, searchable store that the agent can query by meaning rather than file path. Semantic memory enables the agent to recall relevant information even when it doesn't remember exactly where it wrote something down. Configuration includes:

- **Indexing** — whether memory content is automatically indexed
- **Search** — how many results to return, relevance tuning
- **Temporal Decay** — recent memories score higher than old ones, with a configurable half-life

### Memory and Existence

Memory is always a summation of what happened in Sessions — it's the refined extract of the agent's Existence. An agent can operate at three levels of recall:

1. **Memory** (fast, summarized) — "I know Jon prefers short responses"
2. **Semantic Search** (broad, associative) — "What do I know about calendar management?"
3. **Existence** (deep, detailed) — "What exactly was said in that session last Tuesday?"

This layered approach — Memory for breadth, Existence for depth — mirrors how humans remember: we have general knowledge, we can search our recollections, and if we really need the details, we can go back to our notes.

### Memory Maintenance

Memory is updated and managed primarily during Soul Sessions. The agent's daily reflection cycle is when it reviews what happened, decides what matters, and updates its long-term memory accordingly. This is not a passive process — the agent actively curates its own memory, just as a person would review their journal and update their mental model of the world.

---

## Domain Relationships

The following captures how the domain objects relate to one another:

```
World (Gateway)
 ├── hosts one or more Agents
 ├── provides Locations (file system, APIs, services)
 ├── manages Channels (external communication adapters)
 └── enforces boundaries (cross-world access requires permission)

Agent
 ├── defined by a Descriptor (static configuration)
 ├── manifested as one or more Instances (runtime, per-session)
 ├── has a Soul (voice, personality — SOUL.md)
 ├── has an Identity (self-concept — IDENTITY.md)
 ├── has an Existence (all sessions, ever)
 ├── has Memory (long-term + daily)
 ├── lives in a World
 └── communicates via Channels (external) or internal triggers

Session
 ├── belongs to an Agent (bound via Agent ID)
 ├── may be associated with a Channel (user-facing) or not (internal)
 ├── has Participants (users, agents, or both)
 ├── has a lifecycle: Active → Suspended → Sealed
 ├── is interactive or non-interactive (explicit property)
 ├── accumulates History (conversation entries)
 └── becomes part of the Agent's Existence when created

Channel
 ├── connects Users to Agents
 ├── creates interactive Sessions
 ├── declares capabilities (streaming, steering, follow-up, etc.)
 └── is distinct from internal triggers (cron, soul, heartbeat)

User
 ├── interacts through Channels
 ├── participates in interactive Sessions
 └── can inspect (but not participate in) non-interactive Sessions

Memory
 ├── is a distillation of the Agent's Existence
 ├── has Long-Term (MEMORY.md) and Daily (memory/*.md) layers
 ├── supports semantic search with temporal decay
 └── is maintained during Soul Sessions
```

---

## Glossary

| Term                  | Definition                                                                                     |
|-----------------------|------------------------------------------------------------------------------------------------|
| **Agent**             | An intelligent entity composed of a Descriptor (config) and Instance(s) (runtime)              |
| **Descriptor**        | The static configuration of an agent — its identity, model, tools, and access                  |
| **Instance**          | A live runtime manifestation of an agent, bound to a specific session                          |
| **World**             | The environment (Gateway) in which agents exist, including accessible locations                 |
| **Location**          | A resource accessible from a World — file paths, APIs, remote services                         |
| **Soul**              | An agent's voice, personality, and behavioral stance (SOUL.md)                                 |
| **Soul Session**      | A daily private session for agent reflection, heartbeat, and self-maintenance                  |
| **Identity**          | An agent's self-concept — name, archetype, emoji, avatar (IDENTITY.md)                         |
| **Bootstrap Ritual**  | The one-time first-run process where an agent establishes its identity                         |
| **Session**           | A bounded window of interaction between participants                                           |
| **Sealed**            | A session's terminal state — closed, preserved, no further interaction                         |
| **Interactive**       | A session where external participants (users) can inject messages                              |
| **Non-Interactive**   | A session where only the agent (or agents) can contribute                                      |
| **Existence**         | The totality of an agent's sessions — its complete experiential history                        |
| **Channel**           | An external communication adapter (SignalR, Telegram, TUI, etc.)                               |
| **Internal Trigger**  | A non-channel event that creates a session (cron, heartbeat, soul cycle)                       |
| **Conversation**      | An agent-to-agent session where the initiator acts as "user" and the target acts as "agent"    |
| **Sub-Agent**         | A stateless, soulless worker spawned by a parent agent for a specific task                     |
| **Cross-World**       | Communication between agents on different Gateways, requiring explicit permission              |
| **Satellite**         | A remote process that extends a World's capabilities without being a separate World            |
| **Memory**            | The agent's persistent knowledge — long-term (curated) and daily (raw)                         |
| **Semantic Memory**   | Indexed, searchable memory that supports recall by meaning with temporal decay                  |
| **Heartbeat**         | A periodic internal trigger that drives proactive agent behavior via HEARTBEAT.md              |
