---
title: "Release v0.52.0"
description: "Release notes for BotNexus v0.52.0"
date: "2026-10-28"
---

# Release v0.52.0

> **Released:** 2026-10-28
>
> **Full diff:** [v0.51.0...v0.52.0](https://github.com/sytone/botnexus/compare/v0.51.0...v0.52.0)

## [0.52.0] - 2026-10-28

### ✨ Features

- **cron:** Probe for work before invoking an agent (#5008)
- **plugins:** Select repository marketplace skills from cli (#5040)
- **agents:** Bound tool-result budgets and finalize without tools (#5028)
- **agents:** Persist bounded peer dialogue admission (#5052)
- **sessions:** Preflight explicit legacy relationships (#5003)
- **gateway:** Coordinate current-session conversation compaction (#5032)

### 🐛 Bug Fixes

- **exports:** Preserve legacy tool results through cleanup (#5006)
- **agents:** Propagate cancellation before disposing tool link (#5009)
- **config:** Recover malformed store-backed json projections (#5053)
- **copilot:** Classify bounded oauth http rejections (#5039)
- **plugins:** Reject unsafe git sources and withhold fetch diagnostics (#5017)
- **portal:** Persist presence-aware agent policy edits (#4980)
- **agents:** Retry in-band responses failures (#5023)
- **sessions:** Propagate recovery save cancellation (#5001)
- **agents:** Add bounded retained child inspection (#5024)
- **sessions:** Bound tool transcript reads and enforce ownership (#5038)
- **browser:** Align adapter with pinned cli command contract (#5013)
- **sessions:** Exclude terminal recovery candidates (#5026)
- **portal:** Hide unsupported mobile agent settings (#5022)
- **tests:** Align fixtures with source and session contracts (#5076)
- **agents:** Preserve queued provider recovery admission order (#4939)
- **cron:** Retain interrupted trigger execution evidence (#5000)
- **process:** Synchronize unresolved cleanup with validated main (#4968)
- **tests:** Assert discarded context snapshots structurally (#5091)

### 📖 Documentation

- **agents:** Clarify shared conversation delivery boundaries (#5049)
- **tests:** Reconcile test taxonomy contributor guidance (#5015)

### 🔨 Refactor

- **gateway:** Document residual core reference reasons (#5010)
- **gateway:** Add gateway-owned tool contracts (#4999)

### 🧪 Testing

- **scenarios:** Capture concurrent inbound progress (#5042)
- **config:** Cover collection reload and process restart (#5021)
- **sessions:** Bound candidate query temporary write costs (#5027)
- **channels:** Cover compaction sink failure isolation (#5030)

### ⚙️ Miscellaneous

- **repo:** Improve issue form evidence and context prompts (#5090)

[0.52.0]: https://github.com/sytone/botnexus/compare/v0.51.0...v0.52.0

