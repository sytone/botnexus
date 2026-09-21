# Understand agent run completion

This page is for BotNexus developers who need to distinguish a completed run from progress or a legitimate wait.

## Completion contract

A model response is not by itself proof that an execution loop completed. When the host supplies a run-completion evaluator, `AgentLoopRunner` evaluates structured state after ordinary tool and follow-up queues become idle and before it emits `AgentEndEvent`.

The evaluator returns one of these states:

| State | Meaning |
| --- | --- |
| `Completed` | No actionable checklist work remains. |
| `Working` | Actionable work remains and the runtime must continue the same run. |
| `Parked` | Work remains, but a structured stop reason, evidence, continuation owner, and wake condition justify ending this run. |
| `IncompleteWithoutStopReason` | The bounded automatic continuation budget was exhausted while actionable work remained. This is not success. |
| `Failed` or `Cancelled` | The run ended through its existing failure or cancellation path. |

The default automatic continuation limit is two. If every evaluation still returns `Working`, the final event records `IncompleteWithoutStopReason` and includes the open item identifiers. This prevents an infinite completion loop.

## Checklist behavior

The in-process gateway evaluates the conversation's persisted `todo` checklist. It treats `pending` and `in_progress` items as actionable. `done` and `cancelled` are terminal.

A persisted `ask_user` request is currently the only checklist stop disposition the gateway can prove directly. It produces a `Parked` result with `UserInput` as the reason. An assistant sentence such as “I filed an issue,” “phase complete,” or “I will continue later” does not create a stop disposition.

A conversation with no checklist keeps the previous behavior and completes normally. Open checklist items can carry across sessions; they remain work until the agent completes or cancels them, or records a supported structured wait.

## Events and callers

`AgentEndEvent` carries the authoritative completion result. The in-process gateway projects it onto both:

- the streaming `RunEnded` event; and
- blocking `AgentResponse` results used by cron, agent exchange, and sub-agent execution.

Callers must use this state instead of inferring completion from a polished response or from inactivity. Portal-specific presentation and richer sub-agent historical projections are separate consumer work; the runtime contract is already channel-neutral.

## Safety boundaries

The completion gate does not bypass cancellation, approval, destructive-action, access-policy, or safety checks. Cancellation and failures keep their existing control flow and are reported as `Cancelled` or `Failed`. A future host may add other `RunStopReason` values only when it can provide structured evidence and a continuation owner or wake condition; free text is never sufficient.
