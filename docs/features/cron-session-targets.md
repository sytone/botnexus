# Cron Session-Target Semantics

When a cron job fires, its output has to land *somewhere*. This page documents which conversation
a job is bound to, who decides that, and when.

Until BotNexus [#2412](https://github.com/Sytone/botnexus/issues/2412) these defaults existed only
in code and had to be inferred by reading `CronTool`, `CronScheduler` and `CronTrigger` together.

## The binding

`CronJob.ConversationId` is the canonical link from a cron job to its long-lived conversation.
Every run of that job lands in that one conversation for the life of the job.

The binding is established at one of two moments:

| Moment | Mechanism |
| --- | --- |
| **At create** | `CronConversationDefault.Resolve` picks the creating agent's durable conversation, if there is one. |
| **At first run** | If still unbound, `CronTrigger` mints a fresh conversation and `CronScheduler` pins it with a compare-and-swap (`ICronStore.TrySetConversationIdAsync`). |

Once stamped the binding is **immutable for the life of the job**. A definition edit never
retargets it — that column is CAS-owned precisely so a routine edit racing a live run cannot move a
job's output out from under the reader. To get a fresh thread, delete the job and create a new one.

## Session-target table

| Creator | Action type | Conversation context | Resulting target |
| --- | --- | --- | --- |
| Agent (via the `cron` tool, mid-conversation) | `agent-prompt` | Durable conversation resolved | **the creating conversation** |
| Agent (via the `cron` tool) | `command` | any | isolated — pinned on first run |
| CLI (`botnexus cron …`, headless `agent exec`) | any | none | isolated — pinned on first run |
| REST API (`POST /api/cron`) | any | none | isolated — pinned on first run |
| Heartbeat (`HeartbeatAction`) | system | per-agent | the agent's own heartbeat conversation — **unchanged** |
| Memory dreaming, skill review, other system jobs | system | per-agent | each action's own per-agent conversation — **unchanged** |
| Any caller supplying an explicit conversation | any | explicit | **the explicitly supplied conversation** |

"Isolated" here means the job row is stored with a `null` conversation, and the scheduler's
first-run CAS pins whichever conversation `CronTrigger` created. That is the historical behaviour
and it is preserved verbatim for every row in the table that says so.

## Why agent-created `agent-prompt` jobs default to the creating conversation

An agent asked to "check this every 15 minutes" creates the job while speaking in a conversation a
human is reading. Before this default, the job was stored unbound, so the first run minted a brand
new cron conversation and every subsequent run reported into it — a conversation nobody was
watching. The work happened; the answer went to a room with no one in it.

This repo has already paid for the wider version of that problem:
`CronScheduler.MigrateLegacyCronConversationsAsync` is a one-shot startup migration that exists
purely to reconcile cron sessions orphaned by bindings that were never established up front.
Defaulting at create time prevents the orphan rather than reconciling it afterwards.

## Durability: which key is bound

The bound value **must be the durable conversation key** — the id persisted in the conversation
store and still resolvable after a gateway restart. It is resolved through the same memoised
bound-conversation lookup every other conversation-aware tool provider uses: the session store
first, then the conversation whose `ActiveSessionId` matches the running session.

A transient, channel-scoped, or policy-scoped key must **never** be bound. Such a key can be empty,
and it can be retired by routine cleanup — which would leave the job pointing at a conversation
that no longer exists. That dangling binding is strictly worse than no binding at all, because an
unbound job self-heals on its next run while a dangling one does not.

For the same reason an *uninitialised* `ConversationId` (the value-object "unset" sentinel) is
rejected rather than coerced: it is not a durable key, so persisting it would manufacture exactly
the dangling state this design avoids.

## Scope of the default

The default is deliberately narrow. It applies **only** when all of the following hold:

1. the job's action type is `agent-prompt` (a `command` job costs no model turn and emits no
   conversational output, so binding it to a human's conversation would just be noise);
2. the job is not system-provisioned (heartbeat and friends manage their own per-agent
   conversations and are untouched);
3. the caller did not supply an explicit conversation (an explicit choice is a decision, never a
   gap for a default to fill);
4. a durable conversation actually resolved (a CLI, REST or headless caller has none, and keeps
   the isolated default).

Any of those failing yields `null`, which is byte-identical to the pre-#2412 behaviour.

## Self-pacing: the `next_check` bound

A conversation-bound `agent-prompt` job may pace itself. Rather than carrying a hand-tuned cron
expression, it calls the cron tool's `next_check` action with a proposed delay:

```
cron action=next_check jobId=<my job> nextCheckSeconds=900
```

The proposal is **clamped** to a configured `[floor, ceiling]` - by default 60s to 3600s, set via
`cron:selfPacingFloorSeconds` and `cron:selfPacingCeilingSeconds`. Unbounded self-pacing is a
runaway-cost surface in both directions: no floor lets a job re-fire continuously, and no ceiling
lets a job park itself a month out and look scheduled while doing nothing. Misconfiguring either
bound degrades to the built-in default, never to *no* bound.

The clamp is **observable**. The response always reports the requested value beside the effective
one, plus the bounds in force and which one was applied:

```json
{ "requestedSeconds": 1, "effectiveSeconds": 60, "floorSeconds": 60, "ceilingSeconds": 3600,
  "wasClamped": true, "clampReason": "Floor" }
```

Reporting only the effective value would make a loop pinned at the floor indistinguishable from one
genuinely pacing itself - the #3244 finding that a bound whose application is invisible cannot be
reasoned about, and therefore cannot be fixed.

Two further properties are load-bearing:

- **The write targets `BackoffUntil`, not `NextRunAt`.** Per #3350 those are different facts:
  `NextRunAt` is the scheduler's expression cache and is corrected freely, so a deliberate deferral
  written there would be indistinguishable from a stale cache and silently corrected away.
- **Scope follows the same `CanManage` rule as `history` and `costs`.** A `next_check` against
  another agent's job is refused, not applied - otherwise any agent could stall every other agent's
  loops.

Self-paced jobs change nothing about session targeting: they remain agent-created `agent-prompt`
jobs and follow row 1 of the table above.


## Related

- [`CronSelfPacingBound`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Cron/CronSelfPacingBound.cs) — the clamp decision and its observability contract.
- [`CronConversationDefault`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Cron/CronConversationDefault.cs) — the single decision.
- `CronScheduler.TryPinConversationAsync` — the first-run CAS pin and its loser-reconciliation path.
- `CronTrigger` — reuses a pinned conversation verbatim, or mints one when the job is unbound.
