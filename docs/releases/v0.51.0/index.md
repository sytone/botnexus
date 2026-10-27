---
title: "Release v0.51.0"
description: "Release notes for BotNexus v0.51.0"
date: "2026-10-24"
---

# Release v0.51.0

> **Released:** 2026-10-24
>
> **Full diff:** [v0.50.1...v0.51.0](https://github.com/sytone/botnexus/compare/v0.50.1...v0.51.0)

## [0.51.0] - 2026-10-24

### ✨ Features

- **cron:** Add an opt-in global scheduled model default (#4943)
- **plugins:** Import conventional claude skills safely (#4956)
- **api:** Expose conversation-bound current context (#4993)
- **plugins:** Import named manifestless claude skills (#4987)
- **sessions:** Inventory legacy persistence without mutation (#4969)
- **plugins:** Import legacy skills with additive metadata (#5018)
- **workflows:** Embed governed workflow core foundation (#5054)

### 🐛 Bug Fixes

- **mcp:** Retain redirect security fixture socket ownership (#4948)
- **tests:** Bound telegram polling fixtures and ensure cleanup (#4947)
- **testing:** Execute conversation seams on the candidate host (#4967)
- **audit:** Make queued deadline observation deterministic (#4973)
- **config:** Reject non-durable world identity bootstrap (#4977)
- **config:** Preserve explicit empty collection shapes (#4979)
- **skills:** Reconcile discovery and linked-file boundaries (#4963)
- **cron:** Reject new runs during planned shutdown (#4953)
- **providers:** Reconcile current catalogue across startup and reload (#4981)
- **gateway:** Reconcile live context diagnostic breakdown (#4983)
- **tests:** Capture legacy telegram dispatch timeout state (#4984)
- **cron:** Reconcile cancellation after normal action returns (#4992)
- **config:** Bind agent default collections once (#4986)
- **cli:** Recover prior gateway after update build failure (#4994)
- **agents:** Preserve interrupted blocking run evidence (#4995)
- **sessions:** Isolate interrupted recovery scan failures (#4988)
- **sessions:** Stop duplicate tool payload writes and idle locks (#4978)
- **agents:** Persist enabled state and guard runtime admission (#4976)
- **qmd:** Enforce allowed stores on unscoped searches (#5029)
- **sessions:** Defer cleanup until application startup (#5019)

### 📖 Documentation

- **ci:** Define required-gate flake triage policy (#5020)

### 🔨 Refactor

- **history:** Share stored-entry visibility policy (#4944)
- **gateway:** Publish rest pin update facts (#4949)
- **gateway:** Publish binding move lifecycle facts (#4959)
- **gateway:** Publish automatic title lifecycle facts (#4989)

### 🧪 Testing

- **providers:** Cover duplicate model id reload isolation (#4945)
- **sessions:** Verify cleanup rollback and restart convergence (#4946)
- **workspace:** Serialize refresh interactions on renderer (#4951)
- **sessions:** Pin protected cleanup evidence dispositions (#4958)
- **cron:** Stabilize missed-run scan windows (#4991)
- **providers:** Cover sqlite activation with stale projections (#4990)
- **mcp:** Assert owned socket closure without rebinding (#5004)
- **portal:** Observe guide request cancellation at the held wait (#5033)

### Build

- **deps-dev:** Bump vite from 7.3.6 to 7.3.7 (#4997)

[0.51.0]: https://github.com/sytone/botnexus/compare/v0.50.1...v0.51.0

