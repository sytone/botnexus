# Cron Cost

The Activity page's **Cron** subsection (`/activity/cron`) ranks scheduled jobs by total spend over
a bounded window, and surfaces one derived signal the raw measurements do not carry: **tool calls
per turn**.

## Why it exists

The measurement seam has been complete since the per-run cron cost metrics shipped: every run
records turn count, tool call count, duration and token counts, `GET /api/cron/costs` serves a
per-job rollup ordered by total spend, and the rollup already reports its own window clamp. Until
this subsection, **no client consumed any of it** — the platform knew what every scheduled job cost
and no user could see it.

## Tool calls per turn is the actionable column

Total tokens tells you what a job costs. `toolCalls / turns` tells you *why*.

A job averaging many tool calls per turn is one where the agent is groping — re-reading files,
retrying a malformed command, paginating something a single query would have returned. That is a
skill or script defect, and it is fixable in a way that "this job is expensive" alone is not. It is
the one value this subsection **derives** rather than transcribes.

It is computed from the per-measured-run averages (`AverageToolCallsPerRun / AverageTurnsPerRun`)
and is **absent — not `0`, not `NaN`, not a throw — whenever either input is unmeasured or turns are
zero**. A ratio over zero turns is not a small number, it is an undefined one; rendering it as `0`
would rank the least-measurable jobs as the most efficient ones.

## Ranked by total, not per-run average

Rows are ordered by **total** spend descending. This is deliberate and it is the ranking that
matters: a job costing a quarter as much per run but firing 24x more often is the platform's larger
consumer, and a per-run figure alone reports it as the cheaper one. Both figures are displayed;
total is the sort key.

## What it shows

One row per cron job, with columns for job name, run count, measured run count, total tokens, total
tool calls, total turns, total duration, tokens per run, and the derived tool-calls-per-turn ratio.
Each row links to that job on the cron page, keyed on the row's **own** job id rather than its
display position, so a re-sort can never redirect a click.

A window selector (24 hours / 7 days / 30 days) drives the endpoint's `windowDays` argument.

![The cron cost subsection: jobs ranked by total spend, with the derived tool-calls-per-turn column and an unmeasured command job](/assets/cron-cost-3289.png)

## Not measured is not zero

Every total is nullable and `null` renders as `not measured`, never as `0`.

A `command` or `webhook` job has no turn or token concept at all. Coercing it to zero would present
*"we did not look"* as *"this job is free"* and invert the exact ranking the feature exists to
establish. Both `RunCount` and `MeasuredRunCount` are shown side by side, so a job that ran 120
times and measured nothing is visually distinct from a genuinely cheap one — and per-run averages
divide by the measured count, never by the run count, so a real figure is never diluted by runs that
reported nothing.

## Window clamping is stated, not hidden

The gateway clamps the requested window to the configured cron run retention and reports the clamp
on each rollup. When the response sets `windowTruncatedByRetention`, the subsection says so and
names the effective window.

A truncated total that looks complete is worse than a visibly bounded one — the notice is the
difference between a bounded number and a wrong number.

## API

This subsection adds **no new endpoint, controller action, store method or persisted column**. It
consumes the existing endpoint exactly as it stands:

```
GET /api/cron/costs?windowDays=7
```

```json
[
  {
    "jobId": "nightly-maintenance",
    "runCount": 192,
    "measuredRunCount": 192,
    "totalTokens": 4800000,
    "totalToolCalls": 3840,
    "totalTurns": 1920,
    "totalDurationMs": 5400000,
    "windowStart": "2026-08-11T00:00:00+00:00",
    "windowDays": 7,
    "windowTruncatedByRetention": false
  }
]
```

Every total is nullable. A `null` means that signal was not measured — it is not a zero.

## Out of scope

Billing, currency and per-provider token pricing; any enforcement, throttling or auto-disabling of
expensive jobs; and automatically attributing an inefficient ratio to a specific tool or skill. The
ratio points a human at a job; acting on the finding stays a user decision.
