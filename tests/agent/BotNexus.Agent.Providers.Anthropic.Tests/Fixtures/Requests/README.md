# Direct-Anthropic Outbound Request Snapshots

Canonical outbound-request envelopes produced when BotNexus drives a
non-Copilot Anthropic model end-to-end. Used by
`AnthropicDirectRequestSnapshotTests` (Phase 0c of #810) to lock the
contract for the two **direct Anthropic** auth paths so that the Copilot
carve-out cannot regress them:

- `apikey-direct.json` — classic `x-api-key` auth
- `oauth-direct.json` — Claude Code OAuth (`sk-ant-oat...`) tokens

The Copilot envelope is snapshotted separately in
`tests/agent/BotNexus.Agent.Providers.Copilot.Tests/Fixtures/Requests/`.

## Layout

```
Fixtures/Requests/
  <api>/<model>/
    <scenario>.json    # method, path, selected headers, request body
```

## Header allowlist

Only headers the carve-out can plausibly change are captured:

- `Authorization` — value replaced with `<scheme> <redacted>`. Scheme is
  significant: ApiKey path must NOT send Authorization at all; OAuth path
  must send `Bearer`.
- `x-api-key` — value redacted. ApiKey path must send it; OAuth path must
  not.
- `anthropic-version`
- `anthropic-beta` — full comma-joined value (OAuth combines
  claude-code/oauth/fine-grained betas into one header).
- `user-agent` — OAuth path uses `claude-cli/<ver>`; ApiKey path is unset.
- `x-app` — OAuth path uses `cli`; ApiKey path is unset.

Other headers (`accept`, `anthropic-dangerous-direct-browser-access`) are
exercised by `AnthropicProviderAlignmentTests` and not duplicated here.

## Updating

If a request shape changes intentionally:

1. Run the failing test — the failure message includes the captured envelope.
2. Paste the JSON into the snapshot file.
3. Justify the diff in the PR description.

If the snapshot drifts unintentionally during the Copilot carve-out, the
carve-out has leaked across into direct-Anthropic territory — fix the
provider, not the snapshot.
