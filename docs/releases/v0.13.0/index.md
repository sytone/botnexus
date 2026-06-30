---
title: "Release v0.13.0"
description: "Release notes for BotNexus v0.13.0"
date: "2026-06-28"
---

# Release v0.13.0

> **Released:** 2026-06-28
>
> **Full diff:** [v0.12.1...v0.13.0](https://github.com/sytone/botnexus/compare/v0.12.1...v0.13.0)

## [0.13.0] - 2026-06-28

### ✨ Features

- **config:** Add ConfigField attribute and annotate PlatformConfig (#1677)

### 🐛 Bug Fixes

- **agent:** Audit claims per-turn so multi-turn fabrication is not masked (#1662)
- **telegram:** Deliver tool activity as standalone messages with shared cross-channel icon (#1664)
- **cron:** Purge retention on real terminal statuses (ok/error/timed_out) (#1669)
- **cron:** Scope cron create/update target agent to the calling agent (#1673)
- **copilot:** Bound streaming SSE body to prevent unbounded read OOM (#1674)
- **titling:** Apply provider API-endpoint override to auto-title model (#1675)
- **blazor-client:** Observe reconnect fire-and-forget and synchronize shared HashSet state (#1672)
- **agent:** Gate tool dispatch on ToolUse terminal to ignore truncated calls (#1676)

### 📖 Documentation

- Backfill v0.12.0 release page (#1665)

### 🔨 Refactor

- **prompt:** Hoist GetGatewayData, PascalCase publics, named section order (#1660)
- **blazor-client:** Extract ToChatMessage factory, split history loader, unify user echo (#1670)
- **subagent:** Decompose SpawnAsync into named helpers (#1671)
- **persistence:** Extract per-store row-mappers and drop dead FieldCount probing (#1678)

[0.13.0]: https://github.com/sytone/botnexus/compare/v0.12.1...v0.13.0

