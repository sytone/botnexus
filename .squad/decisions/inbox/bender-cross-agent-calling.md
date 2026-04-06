# Bender Decision Inbox — Cross-Agent Calling (2026-04-06)

## Decision
Use deterministic local cross-agent session scoping:

`{sourceAgentId}::cross::{targetAgentId}`

and require target validation through `IAgentRegistry` before supervisor execution.

## Why
- Keeps cross-agent runs discoverable/reusable per caller-target pair.
- Prevents silent fan-out of random GUID sessions for the same agent handoff path.
- Fails fast with a clear registration error before isolation strategy work begins.
- Supports recursion guardrails by making call-path analysis stable (`A -> B -> A` detection).

## Runtime Contract Notes
- `CallCrossAgentAsync` remains local-first only when `targetEndpoint` is empty.
- Non-empty `targetEndpoint` still throws `NotSupportedException` until remote transport is implemented.

## Squad Sign-off Requirement
This note is for owner/lead review.  
The squad should **not** auto-implement or broaden this decision without explicit owner sign-off.
