---
title: "Release v0.28.0"
description: "Release notes for BotNexus v0.28.0"
date: "2026-07-17"
---

# Release v0.28.0

> **Released:** 2026-07-17
>
> **Full diff:** [v0.27.0...v0.28.0](https://github.com/sytone/botnexus/compare/v0.27.0...v0.28.0)

## [0.28.0] - 2026-07-17

### ✨ Features

- **config:** Drive secret redaction from [ConfigField] annotations (#2018)
- **portal:** Add Cron Jobs management page under Agents nav (#2024)
- **#1888:** Make Activity scheduled stat card toggle cron visibility (#2027)
- **cron:** Promote Cron Jobs to top-level nav with detail view, history, and provider/model pickers (#2031)
- **cron:** Use single-page job editor and history (#2044)
- **signalr:** Add desktop chat attachments (#2053)
- **channels:** Add opt-in service bus streaming replies (#2054)
- **portal:** Expose configured default agent (#2068)
- **portal:** Pretty-print structured JSON tool results (#2081)
- **providers:** Add capability-aware Copilot WebSocket transport (#2082)

### 🐛 Bug Fixes

- **cli:** Prune stale extension files on deploy (#2017)
- **providers:** Treat blank env api keys as unconfigured (#2023)
- **#2025:** Route auto-title and compaction LLM calls through shared GatewayAuthManager credential seam (#2026)
- **gateway:** Recover orphaned crash sentinels via post-registration scan and write-time self-heal (#2033)
- **config:** Quarantine invalid agent definitions (#2050)
- **copilot:** Normalize gpt-5.6 response delta CRLF (#2052)
- **auto-title:** Preserve streamed title token boundaries (#2051)
- **cron:** Terminalize completed sessions (#2067)
- **gateway:** Keep auth available during invalid config reloads (#2070)
- **portal:** Prevent doubled mobile markdown spacing (#2080)

### 📖 Documentation

- Document servicebus FullyQualifiedNamespace managed-identity option (#2020)

### 🔨 Refactor

- **blazor-client:** Collapse duplicated best-effort hydration into one helper (#2028)

### 🧪 Testing

- **portal:** Eliminate received-call race in pin-button bUnit tests (#2019)
- **config:** Add fitness function for secret-shaped config properties (#2029)

[0.28.0]: https://github.com/sytone/botnexus/compare/v0.27.0...v0.28.0

