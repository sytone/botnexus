---
title: "Release v0.7.0"
description: "Release notes for BotNexus v0.7.0"
date: "2026-06-16"
---

# Release v0.7.0

> **Released:** 2026-06-16
>
> **Full diff:** [v0.6.0...v0.7.0](https://github.com/sytone/botnexus/compare/v0.6.0...v0.7.0)

## [0.7.0] - 2026-06-16

### ✨ Features

- **portal:** Warn when a run ends with in_progress todo items (#1486)
- **portal:** Replace redundant Conversations heading with activity filter buttons (#1510)

### 🐛 Bug Fixes

- **portal:** Key mobile chat loop and make tool pill a div to stop render crash (#1484)
- **portal:** Report unrecoverable #blazor-error-ui failures to diagnostics (#1485)
- **tools:** Decode read/edit file bytes UTF-8-first with system code page fallback (#1506)
- **gateway:** Evict completed sub-agent records with bounded retention (#1507)
- **security:** Redact secret-shaped values from DebugTool query and runtime output (#1508)
- **cron:** Record host-aborted runs as failed instead of leaving them stuck running (#1511)
- **portal:** Align mobile agent and conversation list ordering with desktop (#1512)

### 📖 Documentation

- Sync SignalR hub contract with IGatewayHubClient and dedupe ignoreDeadLinks (#1492)

[0.7.0]: https://github.com/sytone/botnexus/compare/v0.6.0...v0.7.0
