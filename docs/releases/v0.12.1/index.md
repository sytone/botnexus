---
title: "Release v0.12.1"
description: "Release notes for BotNexus v0.12.1"
date: "2026-06-27"
---

# Release v0.12.1

> **Released:** 2026-06-27
>
> **Full diff:** [v0.12.0...v0.12.1](https://github.com/sytone/botnexus/compare/v0.12.0...v0.12.1)

## [0.12.1] - 2026-06-27

### 🐛 Bug Fixes

- **security:** Cap rate-limit client-window dictionary to prevent unbounded growth (#1637)
- **channels:** Make Telegram streaming-buffer flush surrogate-safe (#1643)
- **subagent:** Make count-cap eviction deterministic with monotonic spawn sequence (#1655)
- **security:** Bound Copilot discovery and error-body JSON reads (#1656)
- **blazor-client:** Route failure paths through ILogger instead of Console.Error (#1658)

### 📖 Documentation

- Backfill v0.11.0 release page (#1644)

### ⚡ Performance

- **persistence:** Eliminate N+1 round-trips in conversation list endpoints (#1642)

### 🔨 Refactor

- **cron:** Introduce CronRunStatus constants and FinalizeRunAsync write-back (#1640)
- **config:** Parse config JSON once and share FinishLoad pipeline with backup recovery (#1657)

[0.12.1]: https://github.com/sytone/botnexus/compare/v0.12.0...v0.12.1

