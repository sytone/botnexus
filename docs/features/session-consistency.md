# Session Consistency Monitor & Auto-Heal

BotNexus persists conversations and the sessions inside them. A conversation's
`ActiveSessionId` is the pointer the router follows to route the next inbound message
to the correct live session. Under rare interleavings — a cron job that hijacked the
pointer, a session deleted out from under a conversation, or a cron session left `Active`
long past completion — this pointer (or a session's lifecycle status) can drift into an
inconsistent state. Historically these discrepancies were only discoverable through ad-hoc
SQLite queries, and repairing them by hand would bypass the product's invariants.

The **session consistency monitor** (issue #2046) detects, reports, and safely repairs the
recoverable discrepancies itself, entirely through the supported session/conversation store
APIs — there is no raw SQL mutation.

## How it runs

A background `SessionConsistencyHostedService` drives a `SessionConsistencyChecker`:

- The first pass runs a short, configurable delay after host startup, so agent registration,
  store hydration, and interrupted-turn recovery settle first and the pass does not race those
  subsystems.
- Subsequent passes run periodically at a bounded cadence.
- Each pass is idempotent and bounded — running it repeatedly on an already-consistent world
  detects nothing and mutates nothing.

The monitor is opt-out via configuration and supports a report-only **dry-run** mode so
operators can validate detection before enabling auto-heal.

## Detected invariants

| Invariant | Condition | Repair |
|-----------|-----------|--------|
| `active-session-missing` | A conversation's `ActiveSessionId` references a session that no longer exists. | Clear the dangling pointer; the router mints a fresh session on the next inbound. |
| `active-session-cron-poison` | A non-cron (human/agent) conversation points at a `cron:` session while a more-recent non-cron session exists in the same conversation. | Re-point to the latest non-cron session. |
| `stale-active-cron` | A `cron:` session left `Active` past a conservative threshold with no live turn registered. | Seal the session via the session store. |

### Live-turn safety

Repairs never re-point or terminalize a session while a turn is genuinely executing on it.
The checker consults the live-turn tracker introduced in #2030 (`ISessionTurnTracker`): if a
turn is in flight on the pointer or session in question, the discrepancy is reported but not
repaired, and the next pass reconsiders it once the turn completes.

## Observability

Every discrepancy is emitted as a structured log line carrying the invariant name, the
conversation and session ids, the previous state, and the chosen disposition (repaired,
skipped due to a live turn, or report-only). This lets operators inspect detected and repaired
discrepancies without querying SQLite directly.

## Configuration

See [Session Consistency Monitor configuration](../configuration.md#session-consistency-monitor)
for the full option table. Summary:

```json
{
  "gateway": {
    "sessionConsistency": {
      "enabled": true,
      "dryRun": false,
      "checkInterval": "00:30:00",
      "startupDelay": "00:01:00",
      "staleActiveCronThreshold": "06:00:00",
      "maxConversationsPerRun": 5000
    }
  }
}
```

## Related work

- **#867** — prevention: a cron session must never overwrite `ActiveSessionId` on a non-cron
  conversation. The monitor repairs pre-existing or newly-introduced inconsistencies that
  prevention alone cannot undo.
- **#2030** — interrupted-turn recovery and the live-turn tracker the monitor reuses rather
  than inventing a second concurrency signal.
- **Session cleanup** — the complementary `SessionCleanupService` prunes expired/sealed and
  near-empty cron noop sessions; the consistency monitor focuses on pointer and lifecycle
  correctness.
