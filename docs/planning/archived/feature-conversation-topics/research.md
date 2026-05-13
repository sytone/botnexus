---
id: feature-conversation-topics
title: "Research: Conversation Topics / Omnichannel Continuity"
type: feature
priority: high
status: draft
created: 2026-04-27
author: rusty
related:
  - archived/feature-multi-session-connection/architecture-proposal.md
  - archived/feature-session-visibility/design-spec.md
---

# Research: Conversation Topics / Omnichannel Continuity

## Problem Statement

BotNexus currently treats the session as both:
1. the runtime unit of agent execution, and
2. the user-facing conversation container.

That coupling breaks omnichannel continuity.

Today, a user can talk to the same agent from Telegram, iMessage, and the web portal, but those interactions are represented as separate sessions rather than a shared conversation. That means:
- context continuity across channels is weak or absent,
- the portal cannot cleanly show "the conversation" an agent is currently in,
- channels cannot be attached to a durable user-facing conversation concept,
- session operations like compact/reset are applied to the only visible unit, even though they are really runtime operations.

The desired model is closer to how a human works:
- one agent can have one or more user-visible conversations,
- a conversation can continue across channels without losing context,
- the portal can deliberately create multiple independent conversations,
- only the portal needs explicit multi-conversation UX,
- external channels like Telegram/iMessage usually map to one active conversation unless deliberately re-bound.

## Current State

### Existing BotNexus model

The current domain `Session` contains:
- `SessionId`
- `AgentId`
- `ChannelType`
- `SessionType`
- `Status`
- `Participants`
- `Metadata`
- `History`

This makes the session carry both runtime lifecycle and user-visible history.

The SignalR hub already supports a multi-session subscription model (`SubscribeAll`) so the portal can see multiple sessions at once, but the visible unit is still the session.

Relevant existing concepts:
- **Session** = execution + persistence + history unit
- **ChannelType / ChannelKey** = source transport (`signalr`, `telegram`, etc.)
- **Session visibility rules** = decide which sessions appear in the portal
- **Channel-scoped history** = combine multiple sessions for a channel/agent pair

### What works today

- Agents have memory and workspace files.
- Skills can be loaded on demand.
- Sessions persist history.
- The portal can list multiple sessions.
- Channels can send messages into an agent.

### What does not work

- A Telegram conversation cannot naturally continue in iMessage or the portal as the *same* user conversation.
- Portal multi-session support is based on raw sessions, not a higher-level conversation object.
- Channel routing is session-centric, not conversation-centric.
- There is no durable default conversation per agent.
- There is no clear concept for "this set of channels is attached to this conversation."

## Industry Research

### Omnichannel support systems

Mainstream support platforms distinguish between:
- **channel** (transport),
- **conversation/case/thread** (user-facing unit), and
- **internal processing state** (agent assignment, handoff, work item state).

Salesforce describes omnichannel service as continuity across channels rather than isolated channel silos. Their framing is that a customer can start in one channel and continue in another without losing context because conversation history is unified across channels.

Microsoft Dynamics 365 Omnichannel similarly uses the conversation as an analyzable top-level unit, with channels acting as facets of the customer interaction rather than the primary container.

### Key pattern from industry

A stable user-facing container sits **above** transport and below account/customer identity:

```text
User / Customer
  -> Conversation / Thread / Case
      -> Channel bindings / touchpoints
      -> Runtime work items / sessions / assignments
```

BotNexus currently jumps from:

```text
User
  -> Session
      -> Channel
```

That is too flat for omnichannel continuity.

## BotNexus-Specific Findings

### Prior platform direction already points this way

The archived multi-session work introduced:
- subscription to multiple sessions,
- client-side switching between sessions,
- visibility filtering.

That solved UI switching problems, but it did **not** solve the core modeling problem: session is still the wrong top-level user concept.

### Sessions are still valuable

The current session model should not be removed. It is still the right place for:
- execution lifetime,
- compaction,
- abort/reset,
- replay buffering,
- message history segments,
- sub-agent and system lifecycle integration.

What is missing is a parent container.

### A new container is needed

A new domain object is needed above session. Working name from Jon: **topic**.

Alternative names:
- `Conversation`
- `Thread` (not chosen)
- `Conversation`
- `WorkspaceConversation`
- `Dialogue`

Initial recommendation: **Conversation** in specs/code until naming is finalized.

Reason:
- `Session` is already overloaded.
- `Conversation` alone may collide with existing transport/runtime vocabulary.
- `Conversation` suggests user intent grouping and supports multiple runtime sessions beneath it.

## Proposed Conceptual Model

```text
Agent
  -> Conversation (user-visible)
      -> ChannelBinding(s)
      -> Session(s) (runtime/history segments)
```

### Conversation responsibilities

- User-facing conversation container
- Stable identity across channels
- Default conversation for an agent
- Holds display name/title/created metadata
- Owns channel bindings
- Owns one or more sessions over time
- Defines which session is currently active for message continuation

### Session responsibilities

- Execution/runtime unit
- History segment within a topic
- Can be compacted/reset/archived
- Can roll over without losing topic continuity

### ChannelBinding responsibilities

- Maps an external channel identity to a topic
- Defines how inbound user messages are routed
- Defines which outbound agent messages are fanned out to which channels

## Design Principles Emerging from Research

1. **Portal shows conversations, not raw sessions, by default.**
2. **Every agent should have a default topic available when it comes online.**
3. **Portal can deliberately create additional topics for the same agent.**
4. **External channels should bind to a topic, not directly to whichever session happens to exist.**
5. **Agent responses can fan out to all subscribed/bound channels for a topic.**
6. **User messages should not be mirrored to other channels automatically.**
7. **Sessions remain internal/runtime segments under a topic.**
8. **Compaction/reset applies to a session, but is initiated from a topic context.**
9. **Only the portal needs rich multi-topic UX at first.**

## Open Questions

1. **Naming** — should the new container be `Conversation`, `Conversation`, or `Thread` (not chosen)?
2. **Binding granularity** — is channel binding per provider (`telegram`) or per channel identity (`telegram chat 1234567890`)?
3. **Fan-out policy** — should all agent replies go to all bound channels by default, or should bindings support notification-only vs interactive modes?
4. **Topic creation policy** — should every agent always have exactly one default topic at first boot, or only once first contacted?
5. **Portal semantics** — when the user clicks "new conversation", does that create a new topic immediately or lazily on first message?
6. **History model** — should topic history be materialized, or assembled from ordered session segments?
7. **Permissions** — if BotNexus grows to multi-user access, are topics scoped by agent+user or globally per agent?

## Recommendation

Create a new feature spec for **Conversation Topics** that:
- introduces a parent container above session,
- routes channels to topics,
- makes the portal topic-first,
- preserves sessions as runtime/history segments,
- provides an incremental migration path so existing session infrastructure keeps working.

## Sources

- Salesforce: *Omnichannel Customer Service: Benefits, How It Works & Examples* — continuity across channels, unified conversation history
- Microsoft Learn: *Conversation dashboard in Omnichannel historical analytics* — conversations as top-level analyzable units across channels
- BotNexus archived planning:
  - `docs/planning/archived/feature-multi-session-connection/architecture-proposal.md`
  - `docs/planning/archived/feature-session-visibility/design-spec.md`
