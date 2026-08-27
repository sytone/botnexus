# Inbound Delivery Modes

How the gateway decides whether an inbound message **steers** a turn already running, **interrupts**
it, or **queues** for a turn of its own — and where the two bounded queues on that path sit.

Issue: [#3028](https://github.com/Sytone/botnexus/issues/3028).

## The problem this replaces

Before #3028 the gateway implemented two delivery semantics through two unrelated mechanisms, and the
choice between them was made by each *caller*:

- `IInboundMessageOrchestrator` queued FIFO per isolation unit. It had no concept of an active turn
  and no path to steering at all.
- `Agent.Steer` / `PendingMessageQueue` injected into a running turn, reachable only through
  `GatewayHub` and `ChatController`.

The desktop portal picked between them in a Razor component by reading client-side stream state and
calling a different hub method. Every other surface — the CLI, webhooks, satellites, and the
`POST /api/agents/{agentId}/conversations/{conversationId}/messages` endpoint — got queue-only
semantics by construction, whether or not that was what the caller wanted, and nothing said so.

## The seam

A caller now states **intent**; the gateway resolves it to a **mechanism**.

| Type | Role |
| --- | --- |
| `InboundDeliveryMode` | The caller's intent, carried on `InboundMessage.RoutingHints.DeliveryMode`. |
| `IInboundDeliveryResolver` | Decides the mechanism. Side-effect free. |
| `InboundDeliveryDecision` | The decision: requested mode, resolved mode, and whether a turn was active. |
| `IInboundSteerDeliverer` | Performs the injection when the decision is steer or interrupt. |

`DefaultInboundMessageOrchestrator` consults the resolver before it touches the queue. When the
decision is a live-turn mechanism it hands the message to the deliverer; otherwise the message takes
the ordinary FIFO path, byte-for-byte as before.

## Modes

| Mode | Meaning | With a turn running | With no turn running |
| --- | --- | --- | --- |
| `Auto` *(default)* | "Do the documented thing." | **Queues** | Queues |
| `Queue` | Always take a turn of my own. | Queues | Queues |
| `Steer` | Inject into the running turn. | Steers | Queues (falls back) |
| `Interrupt` | Abort the run and redirect it. | Interrupts | Queues (falls back) |

### Why `Auto` queues

Steering injects into a turn already in flight, which has ordering and context-window consequences a
queued message does not. Every pre-#3028 caller reached the orchestrator with no expressed intent, so
making `Auto` steer would retroactively change the meaning of the webhook path, every channel adapter
and the conversation-messages endpoint at once. **Steering is opt-in.**

### Steer and interrupt are requests, not guarantees

A steer only has meaning against a turn in flight. Injecting into an idle agent's pending queue
produces a message nothing ever drains, because the loop that would read it has already ended — a
silent loss. Both live-turn modes therefore fall back to queueing when nothing is running, and the
fallback is visible on the decision via `FellBackToQueue` rather than being indistinguishable from an
ordinary queue.

The same fallback applies if the turn ends *between* the resolver's check and the injection: the
deliverer re-checks `IsRunning` and reports non-delivery, and the orchestrator queues the message.

## The two bounded queues

Two independently configured queues sit on an inbound message's path. They bound different things and
neither is a fallback for the other.

| | `DefaultInboundMessageOrchestrator.DefaultQueueCapacity` | `PendingMessageQueue.Capacity` |
| --- | --- | --- |
| Assembly | `BotNexus.Gateway.Dispatching` | `BotNexus.Agent.Core` |
| Bounds | Messages waiting for a turn of their own, per isolation unit | Messages injected into a turn already running |
| Default | `64` | `0`, meaning unbounded |
| Configured by | The orchestrator's `queueCapacity` constructor argument | `PendingMessageQueue.Capacity` |
| Drained by | The per-isolation-unit worker, strictly FIFO | The agent loop at its steering drain points, per `QueueMode` |
| On overflow | `InboundDispatchStatus.Busy` plus user-visible busy feedback | Throws `PendingMessageQueueFullException` |

A message counts against **exactly one** of them, decided by `IInboundDeliveryResolver`. Queue-bound
messages never consume steering capacity, and steered messages never consume gateway queue capacity —
so a saturated gateway queue cannot block a steer, and a saturated steering queue cannot block normal
traffic.

`QueueMode` is a third, separate knob: it controls how many pending messages the agent drains per
loop iteration (`All` or `OneAtATime`). It governs drain *rate*, not admission, and is unrelated to
either capacity.

## HTTP surface

`POST /api/agents/{agentId}/conversations/{conversationId}/messages` accepts an optional `delivery`
field:

```json
{
  "message": "check CI before you continue",
  "delivery": "steer"
}
```

Omitted, it is `auto` — the endpoint **always queues and never interrupts a running turn**. `steer`
and `interrupt` are honoured when a turn is running and fall back to queueing when one is not. The
field is ignored when `wake` is `false`, because an append-only write schedules no turn at all.

## Status codes

`InboundDispatchStatus.Steered` reports that a message was absorbed by a turn already running, so no
separate dispatch ran and `Dispatches` is empty. It is returned only when a caller explicitly asked
for `Steer` or `Interrupt` **and** a turn was active; `Auto` never yields it.

`InboundDispatchStatus.Stalled` (#3600) reports that a message reached its queue but the queue did
not drain within the orchestrator's bounded observation window, so the caller's await was released
without a processing result.

**`Stalled` is not a drop.** The message stays on the channel and is still processed when the head of
the queue clears; only the *await* is bounded. The bound applies solely to the wait for a message to
reach the front of its queue - once the worker has handed it to `IInboundMessageProcessor`, the await
is unbounded again, so a legitimately long agent turn is never truncated.

The status exists because before #3600 "processed" and "stuck behind a head that is not moving" were
indistinguishable: `AcceptAsync` simply never returned, nothing threw, and nothing was logged. Every
`Stalled` outcome now carries a warning naming the isolation key, channel, conversation, session and
agent, plus user-visible channel feedback via the same seam that reports `Busy`.

The bound is `DefaultInboundMessageOrchestrator.DefaultQueueWaitTimeout` (10 seconds) and is
overridable per host through the constructor's `queueWaitTimeout` argument.

## Boundary observability

`GatewayHubApplicationService.AcceptAsync` logs the resolved `InboundIsolationKey` and the terminal
`InboundDispatchStatus` for **every** inbound message - at Debug for `Accepted`/`Steered`, at Warning
for everything else. Before #3600 this method was a bare forward, which is why a message that never
reached `GatewayHost.ProcessAsync` produced no line of its own and the drop was invisible.
