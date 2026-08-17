# Conversation Cost

The Activity page's **Cost** subsection (`/activity/costs`) ranks conversations by accumulated
spend, so the ones that have outgrown their context are visible rather than buried.

## Why it exists

`/activity` answers *recency* and *status*. It never answered *accumulation*. Measured on a live
instance, 2,039 conversations carrying history spanned a ~8,000x range in stored transcript, the top
ten accounted for 29.4% of everything, and the four most expensive were all recurring automation
rather than human chats. None of that was visible anywhere in the portal.

Three signals were unavailable at the conversation level:

| Signal | What it tells you |
|---|---|
| Session count | The ramp signal — a conversation spanning hundreds of sessions is a long-running process, not a chat |
| Message count | Total accumulated transcript across every session the conversation owns |
| Compaction-summary count | Context pressure — a conversation repeatedly compacting is one that no longer fits |

## What it shows

One row per conversation, ranked by total cost descending, with columns for agent, conversation
title, session count, message count, compaction-summary count and total cost.

The subsection **inherits the main Activity dashboard's filters** — agent, origin, cron and
recency. That inheritance is structural rather than duplicated: the cost projection delegates
filtering wholesale to the same `ActivityDashboardProjection.Project` the overview table uses, so a
facet added to the dashboard applies here with no further change, and the two can never disagree
about which conversations match.

Because the top of the unfiltered ranking is dominated by automation, hiding cron conversations is
the single most useful interaction on the page: it is one click from "what is the platform spending
overall" to "what am *I* spending".

## Not measured is not zero

Every cost signal the platform cannot presently measure is **nullable, and `null` renders as
`not measured`** — never as `0`.

This is not cosmetic. Reporting an unmeasured conversation as zero would present *"we did not
look"* as *"this conversation is free"*, which inverts the ranking the whole feature exists to
produce. A measured zero (a conversation that genuinely never compacted) and an unmeasured one are
rendered differently, sort differently, and are distinguishable in the API payload.

Token totals are unmeasured today on every conversation: no per-conversation provider-usage
measurement exists on this seam yet. They are carried as a nullable field so that when the usage
seam lands, the column becomes populated without any change to how the absence was being reported.

## How it is derived

The rollup is computed **at read time from rows that already exist** — a `GROUP BY` over the
`sessions` and `session_history` tables. No stored counter column is added, and none is maintained.

A maintained counter can drift from the transcript it claims to describe; a derived aggregate
structurally cannot. This follows the same derive-don't-store rule the platform applies elsewhere.

## API

```
GET /api/conversations/costs
```

Returns one row per listed conversation, ranked by accumulation descending with a deterministic
conversation-id tie-break:

```json
[
  {
    "conversationId": "c_...",
    "sessionCount": 527,
    "messageCount": 8720000,
    "compactionSummaryCount": 28,
    "totalTokens": null
  }
]
```

`compactionSummaryCount` and `totalTokens` are nullable. A `null` means the configured session store
did not measure that signal — it is not a zero.

### Store capability

Counting compaction summaries without hydrating every transcript needs a query engine, so the
rollup is served through an **optional** `IConversationCostReader` capability rather than a required
member on every session store. The SQLite store implements it and answers the whole rollup in one
aggregate query.

A gateway configured with a store that does not implement it degrades honestly: session and message
counts still come from the transcript-free session summaries, and `compactionSummaryCount` is
reported as `null` rather than fabricated.

## Out of scope

Billing, currency and per-provider pricing; any enforcement, throttling, auto-archiving or
auto-splitting driven by the numbers; and backfilling historical cost. The subsection reports; it
does not act.
