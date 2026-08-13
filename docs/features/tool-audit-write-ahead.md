# Tool-Audit Write-Ahead

Every tool a BotNexus agent invokes is recorded in the session transcript **before** the tool runs,
not after it returns. The audit record is the platform's account of what an agent actually did —
independent of what the agent *says* it did in its reply.

## The guarantee

1. **Write ahead, then execute.** The invocation record — tool name, correlation id, and redacted
   arguments — is durably persisted before the tool is entered.
2. **Fail closed on side-effecting tools.** If the record cannot be persisted, a side-effecting
   tool (`exec`, `shell`, `process`) is **blocked**. The agent receives an explicit error and the
   command never runs.
3. **An interruption is explicit.** A call that started and never produced a result — cancellation,
   timeout, provider death, or a crash mid-tool — is closed out with an explicit
   *"did not complete"* record carrying the original arguments, rather than silently vanishing from
   the transcript.

Rows are rendered by the single tool-audit sink shared with the streaming and blocking execution
paths, so a write-ahead row, a streamed row and a blocking-run row all read the same way.

## Why fail closed

An audit trail that can be skipped under load is not an audit trail. If a destructive shell command
executes while the transcript write is failing, the only surviving account of that command is the
agent's own prose summary — which is exactly the thing an audit record exists to corroborate.
Refusing the call converts an unobservable action into an observable refusal.

## Why only side-effecting tools

Fail-closed is deliberately scoped. Blocking a read-only tool on a durability incident converts a
storage problem into a total agent outage while adding no containment — a `read` that executed
unrecorded changed nothing outside the transcript. Read-only tools therefore keep best-effort
behaviour: the failure is logged and counted, and the run continues.

The side-effecting set is:

| Tool | Reason |
| --- | --- |
| `exec` | Runs an arbitrary process. |
| `shell` | Runs an arbitrary shell command. |
| `process` | Manages, signals, and feeds input to running processes. |

## What an interrupted record looks like

An interrupted call leaves two rows for the same `tool_call_id`: the start row written ahead of
execution, and an explicitly flagged incomplete row.

| Row | Kind | Error | Content |
| --- | --- | --- | --- |
| start | `ToolStart` | no | `Tool 'exec' started.` |
| interruption | `ToolResult` | **yes** | `Tool 'exec' did not complete — result synthesized for transcript consistency.` |

The incomplete row carries the same redacted arguments as the start row, so an interrupted
invocation stays forensically readable without a self-join across the transcript.

## Observability

Failures to persist a write-ahead record are counted on
`botnexus.tool.write_ahead.failures`, tagged with `botnexus.tool.name` and
`botnexus.session.id`. A non-zero value means either a side-effecting tool was refused or a
read-only tool executed without an audit record — both are durability incidents worth alerting on.

## Secret handling

Arguments pass through the shared secret redactor before they are written, so credentials supplied
as tool arguments never reach the transcript verbatim. Redaction happens before persistence, not
before display, so a transcript reader and a transcript file see the same redacted value.
