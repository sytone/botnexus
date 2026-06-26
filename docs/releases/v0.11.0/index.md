---
title: "Release v0.11.0"
description: "Release notes for BotNexus v0.11.0"
date: "2026-06-24"
---

# Release v0.11.0

> **Released:** 2026-06-24
>
> **Full diff:** [v0.10.1...v0.11.0](https://github.com/sytone/botnexus/compare/v0.10.1...v0.11.0)

## [0.11.0] - 2026-06-24

### ✨ Features

- **channels:** Add Telegram Rich Message API client methods (#1591) (#1592)
- **channels:** Send Telegram outbound via Rich Markdown with MarkdownV2 fallback (#1591) (#1594)
- **channels:** Stream Telegram replies via Rich Message drafts (#1591) (#1604)
- **sessions:** Cap oversized tool results at write time (#1605)

### 🐛 Bug Fixes

- **qmd:** Kill orphaned qmd subprocess on caller cancellation (#1601)
- **security:** Bound untrusted external JSON HTTP response reads (#1603)

### 📖 Documentation

- Backfill v0.9.0 release page + document cron DeleteAfterRun config (#1593)

[0.11.0]: https://github.com/sytone/botnexus/compare/v0.10.1...v0.11.0

