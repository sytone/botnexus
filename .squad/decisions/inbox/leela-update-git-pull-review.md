# Leela Decision — update git pull cancellation review (2026-07-29)

## Decision

**APPROVED** — The `fix/update-pull-cancel` branch correctly addresses the reported `botnexus update` cancellation failure.

## Key Points

1. **Cancellation is no longer masked as a generic failure.** `OperationCanceledException` gets its own catch block returning exit code 130 with a clear "Update cancelled" message.
2. **Process cleanup on cancel is correct.** `proc.Kill(entireProcessTree: true)` prevents orphaned git processes.
3. **Stream draining avoids deadlock.** `Task.WhenAll(stdout, stderr, WaitForExit)` is the correct pattern — never call `WaitForExit` before draining redirected streams.
4. **Gateway sequencing is safe.** Pull failure returns early before any stop/start, so a cancelled pull never touches gateway state.
5. **`GitPullResult` record struct is the right scope.** Private to UpdateCommand — no need for wider abstraction.

## Follow-Up (non-blocking)

`FirstNonEmptyLine` should split on `['\r', '\n']` instead of `Environment.NewLine` to handle git's mixed line-ending output on Windows. Low priority — only affects diagnostic display.

## Affects

- `src/gateway/BotNexus.Cli/Commands/UpdateCommand.cs`
- `tests/BotNexus.Cli.Tests/Commands/UpdateCommandTests.cs`
