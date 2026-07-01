---
title: "Release v0.15.0"
description: "Release notes for BotNexus v0.15.0"
date: "2026-06-30"
---

# Release v0.15.0

> **Released:** 2026-06-30
>
> **Full diff:** [v0.14.0...v0.15.0](https://github.com/sytone/botnexus/compare/v0.14.0...v0.15.0)

## [0.15.0] - 2026-06-30

### ✨ Features

- **gateway:** Wire titling.timeoutSeconds + add titling.enabled switch (#1724)
- **provider:** Add ThinkingLevel.Max and capability-gated copilot mapping (#1728)
- **security:** Emit security events from auth and authorization boundaries (#1729)
- **skills:** Manage shared all-agent skills behind opt-in gate (#1730)
- **chat:** Paginate history with scroll-up load-more (#1733)
- **portal:** Add platform stats overview with live active-loop counts (#1735)

### 🐛 Bug Fixes

- **compaction:** Re-check ShouldCompact between agent-loop iterations (#1725)
- **conversations:** Stop persisting agent-name bindings + dedupe by channel address (#1726)
- **cron:** Skip persisting near-empty wake sessions (#1727)

### 📖 Documentation

- Backfill v0.13.0 release page (#1734)

### 🔨 Refactor

- **domain:** Move CitizenId composition onto value type (#1731)
- **api:** Extract canvas cluster into dedicated controller (#1732)

[0.15.0]: https://github.com/sytone/botnexus/compare/v0.14.0...v0.15.0

