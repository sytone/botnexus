# Hermes Decision — Issue 24 test strategy

Date: 2026-05-07

## Decision
For issue #24, QA will lock in three regression contracts before runtime implementation:
1. Platform config must preserve per-agent and defaults-level `ToolTimeoutSeconds` for descriptor/runtime wiring.
2. In-process agent creation must propagate configured timeout into `AgentOptions.ToolTimeout`.
3. AgentCore timeout behavior keeps passing contracts for structured timeout error + continued execution after timeout.

## Rationale
The runtime timeout behavior is partially implemented in AgentCore, but gateway config-to-runtime wiring is currently missing. These tests separate already-working behavior from missing plumbing so implementation can land safely without regressing existing timeout handling.
