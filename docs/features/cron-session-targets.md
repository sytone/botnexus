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
| Agent (via the `cron` tool, mid-conversation) | `agent-prompt` | Durable non-human, non-default conversation resolved | **the creating conversation** (implicit adoption excludes human/default threads) |
| Agent (via `/loop`, mid-conversation) | `agent-prompt` | Durable current conversation explicitly selected | **the creating conversation**, including a human/default thread |
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
   the isolated default);
5. the conversation is neither human-facing (`HumanAgent`) nor the agent's default home thread.
   This guard prevents implicit adoption; an explicit caller-selected binding still wins.

Any of those failing yields `null`, which is byte-identical to the pre-#2412 behaviour.

## `/loop`: recurring turns in your current conversation

Use `/loop` in an active chat to schedule recurring prompts for that chat's agent. Each run
uses the configured agent tools and provider and may incur model costs. The gateway must have
cron scheduling enabled. There is no headless CLI equivalent for this conversation-bound command;
CLI cron jobs retain the separate semantics in the table above.

Examples to type in chat:

```text
/loop 15m check the queue and report changes
/loop check the queue and report changes
/loop status
/loop stop loop-<id-shown-by-status>
```

Creation returns the generated loop name. `/loop status` lists only marked `/loop` jobs in the
exact current conversation that both target and were created by its agent. Other cron jobs are
not loops, even when they share the conversation. `/loop stop <name>` deletes only a matching
loop. Without a name, zero matches is an error, one is stopped, and more than one is refused:
use `/loop status` to choose a name. Names match exactly.

Accepted explicit intervals are `1m`, `2m`, `3m`, `4m`, `5m`, `6m`, `10m`, `12m`, `15m`, `20m`,
`30m`, `60m`, and `1h`, `2h`, `3h`, `4h`, `6h`, `8h`, `12h`, `24h`. These are whole-minute/hour
periods representable without uneven boundary gaps in a five-field cron schedule. Schedules use
UTC clock boundaries, not a delay measured from creation. Seconds, fractions, unsupported periods
such as `7m`, signed numbers, ISO durations such as `PT5M`, and `every ...` are refused with the
accepted forms; they are not silently treated as prompt text or a default interval.

Without an interval, the command proposes five minutes and consumes `CronSelfPacingBound.Clamp`
with the existing configured floor/ceiling. Creation reports requested and effective seconds,
bounds and clamp reason. If the effective default cannot be represented exactly by a five-field
schedule, creation is refused; supply an accepted explicit interval instead. Explicit intervals
are not clamped. The existing `next_check` tool remains available for subsequent self-pacing.

`/loop` explicitly asks to use the current conversation. It resolves the durable row from the
session/conversation stores and passes that id to the explicit branch of
`CronConversationDefault.Resolve`. This permits human/default conversations without weakening
the implicit-adoption guard. Missing, ambiguous, archived, foreign-agent or stale active-session
bindings are refused rather than creating an invisible unbound job.

Stopping goes through `CronScheduler.DeleteJobAsync`: it cancels in-flight runs and reclaims the
job's own cron-scoped run sessions. The scheduler archives only conversations with that job's
cron-owned provenance. A loop bound to your existing human conversation does not own it, so
stopping the loop leaves your conversation active. Loop identification uses the existing persisted
job metadata (`chatCommand: /loop`); no new cron schema or general command framework is involved.

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
