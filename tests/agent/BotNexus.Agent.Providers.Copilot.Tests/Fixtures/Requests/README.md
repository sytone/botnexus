# Copilot Outbound Request Snapshots

Canonical outbound-request envelopes produced when BotNexus drives a Copilot
model end-to-end. Used by `CopilotRequestSnapshotTests` to lock in the wire
contract on the **request** side, complementing the response-side fixtures
under `../Wire/`.

The dedicated `CopilotProvider` introduced in Phase 1 of the carve-out (#810)
must produce identical envelopes from identical inputs; a snapshot diff is the
canonical signal that the carve-out has changed the contract.

## Layout

```
Fixtures/Requests/
  <api>/<model>/
    <scenario>.json    # method, path, selected headers, request body
```

Each snapshot is a single JSON document with shape:

```json
{
  "method": "POST",
  "path": "/v1/messages",
  "headers": {
    "Authorization": "Bearer <redacted>",
    "anthropic-version": "2023-06-01",
    "anthropic-beta": "..."
  },
  "body": { ... }
}
```

## Header set

Only headers the carve-out can plausibly change are captured. The current
allowlist is:

- `Authorization` — value is replaced with `<scheme> <redacted>`. The
  scheme itself is meaningful (Copilot must use `Bearer`, not `x-api-key`).
- `anthropic-version`
- `anthropic-beta`

Other headers (`accept`, `anthropic-dangerous-direct-browser-access`, etc.)
are exercised by `AnthropicProviderAlignmentTests` and not duplicated here.

## Updating

If a request shape changes intentionally:

1. Run the failing test — its failure message includes the captured envelope.
2. Paste the captured JSON into the snapshot file.
3. Justify the diff in the PR description.

If the snapshot drifts unintentionally, the carve-out has introduced a wire
regression — fix the provider, not the snapshot.
