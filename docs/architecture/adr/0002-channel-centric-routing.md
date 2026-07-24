# 0002. Route messages through channels (channel-centric routing)

- **Status:** Accepted
- **Date:** 2026-07-24
- **Deciders:** BotNexus Team

## Context and Problem Statement

BotNexus serves several front-ends — a Blazor Server WebUI over SignalR, Telegram, a TUI,
Azure Service Bus, Agent365 — plus non-interactive entry points such as cron triggers and
inbound webhooks. If each front-end called agents directly, session resolution, history
rehydration, and prompt assembly would be duplicated and inconsistent across surfaces.
How should inbound input reach an agent?

## Decision Drivers

- **Uniformity** — every surface should get the same session, history, and prompt
  semantics.
- **Isolation** — each (agent, channel) pair needs its own persistent conversation.
- **Extensibility** — new channels should plug in without touching agent code.
- **Testability** — the citizen → conversation → session model must be verifiable without
  any real channel (see the scenario suite).

## Considered Options

- **Channel-centric routing** — all input flows through a channel abstraction into a
  central dispatcher that resolves session and runs the agent loop.
- **Direct agent invocation per front-end** — each UI wires straight to Agent Core.

## Decision

We will route **all** messages through **channels**, never through direct agent calls.
Channel adapters (`src/extensions/BotNexus.Extensions.Channels.*`) hand input to the
Gateway, where **Dispatching** resolves the session (one per agent+channel pair via
`BotNexus.Gateway.Sessions`), rehydrates conversation history
(`BotNexus.Gateway.Conversations`), and runs the agent loop through the prompt pipeline.
Cron and webhooks feed the same dispatch path rather than bypassing it. This seam is
protected by architecture fitness functions: scenario tests and the harness must not
reference any channel extension, and `VirtualChannelAdapter` implements the same
`IChannelAdapter` contract the real channels do.

## Consequences

**Positive**

- Consistent session/history/prompt behaviour across every surface and trigger type.
- New channels are additive extensions; agent and dispatch code stays untouched.
- The channel-agnostic scenario suite can exercise the full model through a virtual
  adapter, with fitness functions preventing channel leakage.

**Negative / costs**

- An extra indirection layer for the simplest one-off flows.
- Channel adapters must faithfully implement the `IChannelAdapter` contract; a
  non-conforming adapter is caught by tests rather than at compile time only.

## References

- `src/extensions/BotNexus.Extensions.Channels.*`
- `src/gateway/BotNexus.Gateway.{Dispatching,Conversations,Sessions}`
- `tests/scenarios/` and `tests/architecture/BotNexus.Architecture.Tests/ScenarioSuiteArchitectureTests.cs`
- [arc42-lite overview](../README.md) — §4 Solution Strategy, §6 Runtime View
- [Runtime view](../runtime-view.md) — Scenario 1
- Issue [#220](https://github.com/Sytone/botnexus/issues/220)
