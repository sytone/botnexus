---
title: "Release v0.17.0"
description: "Release notes for BotNexus v0.17.0"
date: "2026-07-05"
---

# Release v0.17.0

> **Released:** 2026-07-05
>
> **Full diff:** [v0.16.0...v0.17.0](https://github.com/sytone/botnexus/compare/v0.16.0...v0.17.0)

## [0.17.0] - 2026-07-05

### ✨ Features

- **mobile:** Add settings page consuming shared schemaform (#1747)
- **channels:** Derive channel post role from sender kind and speak_as (#1748)

### 🐛 Bug Fixes

- **#1751:** Guard stored-column JSON reads in SQLite stores against corrupt rows (#1759)

### 📖 Documentation

- Daily documentation grooming 2026-07-01 (SignalR conn params, sidebar orphan, v0.16.0 release page) (#1750)

### 🔨 Refactor

- **#1625:** Collapse gateway hub boilerplate behind an application-service facade (#1749)
- **client:** Extract ask_user prompt parsing into AskUserPromptFactory (#1760)
- **channels:** Extract telegram message-splitting into TelegramMessageSplitter (#1765)
- **config:** Extract validation engine into PlatformConfigValidator (#1766)

[0.17.0]: https://github.com/sytone/botnexus/compare/v0.16.0...v0.17.0

