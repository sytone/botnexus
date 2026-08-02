---
title: "Release v0.40.0"
description: "Release notes for BotNexus v0.40.0"
date: "2026-07-31"
---

# Release v0.40.0

> **Released:** 2026-07-31
>
> **Full diff:** [v0.39.0...v0.40.0](https://github.com/sytone/botnexus/compare/v0.39.0...v0.40.0)

## [0.40.0] - 2026-07-31

### ✨ Features

- **#2121:** Carry the minting source id on conversation provenance (#2609)
- **#2557:** Add opt-in cron job failure alerts (#2594)
- **config:** Additive bundled-agent startup reconciliation (#2635) (#2642)
- **#2634:** Add cron job one-shot disposition and expiry instant (#2652)

### 🐛 Bug Fixes

- **#2591:** Stop the portal service worker pinning a stale bundle (#2593)
- Reject unbalanced brackets in config key paths (#2622)
- **#2606:** Document satellite Capabilities as display-only, add fence (#2620)
- **#2131:** Retry conversation saves in todo and ask_user tools (#2584)
- **#2462:** Gate cron command jobs through an authorization seam (#2599)
- **#2447:** Surface channel startup health for degraded adapters (#2600)
- **#2624:** Reconnect desktop portal indefinitely after terminal close (#2626)
- **#2625:** Make mobile reconnect actually recover after a restart (#2629)
- **gateway:** Enforce sub-agent maxTurns and report TurnsUsed (#2657)
- **razor:** Disambiguate @section variable expressions from directive (#2630)

### 📖 Documentation

- Daily documentation grooming 2026-07-31 (#2612)

### 🧪 Testing

- **architecture:** Fence Razor reserved directive keywords used as bare dotted expressions (#2655)

### 🔧 CI/Build

- **#2513:** Bound every workflow job with timeout-minutes (#2583)

[0.40.0]: https://github.com/sytone/botnexus/compare/v0.39.0...v0.40.0

