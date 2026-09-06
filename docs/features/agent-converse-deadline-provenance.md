# Agent converse deadline provenance

`agent_converse` reports cancellation as structured JSON, rather than a bare
`OperationCanceledException` message. The requested `timeoutSeconds` budget and
the observed `elapsedSeconds` describe the call; elapsed time is not used to infer
which cancellation source fired.

## Two deadlines, different responsibilities

The tool supplies `AgentExchangeRequest.Deadline` at the requested budget. The
turn engine owns an independent cancellation source for that instant. When that
source cancels work and the caller has not cancelled, the engine seals the exchange
session, records `Agent exchange exceeded its deadline.`, and attempts to archive
the conversation using the existing active-session pointer guard. Terminal writes
remain non-cancellable.

After that lifecycle handling, `AgentExchangeDeadlineExceededException` carries
the engine's decision through the exchange service to the tool. It derives from
`OperationCanceledException`, preserving cancellation handling at existing callers
while retaining the original exception and cancellation token for diagnostics.

The tool's linked timer remains a **backstop**, armed five seconds after the
requested deadline. It bounds work outside the turn engine and cancellation paths
that escape it. The backstop need not fire for an engine timeout to be reported
correctly. Neither the timeout floor nor the five-second buffer changes.

## Classification priority

| Evidence at the tool boundary | `cancellationCause` | Meaning |
|---|---|---|
| Ambient caller token cancelled | `callerAborted` | The issuing turn was abandoned; do not blame the peer or recommend an immediate retry. |
| Typed engine deadline, or tool backstop cancelled | `timeout` | The caller's requested budget was exhausted; `cancelledBy` remains `caller` in the existing reporting contract. |
| Other cancellation | `targetUnavailable` | Target-side or runtime cancellation without an identified caller budget source. |

Ambient caller cancellation wins even when another source has also fired. The
engine independently preserves its caller-first catch ordering: a caller abort
observed there does not seal or archive the session. Cancellation arriving after
an announced seal does not undo its durable terminal writes.

## Target state is an observation

`targetState` is sampled when an otherwise unattributed cancellation reaches the
tool. A `busy` or `idle` observation is **not** evidence of the peer's state when
the exchange was admitted, nor proof that a busy peer rejected it. Use admission
and exchange lifecycle evidence to diagnose that question. Timeout and caller
abort reports deliberately do not probe target state and report `unknown`.

The regression suite crosses the real tool, exchange service, and turn engine
with in-memory stores and a cancellable target handle. It checks the engine
exception crossing while the tool backstop is still unsignalled, the structured
report, and the persisted seal/archive outcome. Separate target-side and caller
cancellations protect the other branches of the contract.
