---
title: "Release v0.1.1"
description: "Release notes for BotNexus v0.1.1"
date: "2026-05-03"
---

# Release v0.1.1

> **Released:** 2026-05-03
>
> **Full diff:** [v0.1.0...v0.1.1](https://github.com/sytone/botnexus/compare/v0.1.0...v0.1.1)

## [0.1.1] - 2026-05-03

### ✨ Features

- **telegram:** Add botnexus-extension.json manifest so CLI deploys Telegram channel (#108)
- Replace manual changelog with git-cliff + migrate docs from MkDocs to VitePress (#107)
- **telegram:** Bind each Telegram bot to a configured agent (#109)
- **bootstrap:** Meaningful scaffold templates with first-run ritual (#113)

### 🐛 Bug Fixes

- **gateway:** Conversation-scoped session persistence, ThreadId routing, binding-aware fan-out (#106)
- **actions:** Make publish-cli workflow runner-compatible and branch-agnostic
- **ci:** Fix YAML syntax error in publish-cli.yml (#111)

### 📖 Documentation

- Sync user docs with current architecture (#110)

### ⚙️ Miscellaneous

- **workflow:** Select release type and auto-increment version

[0.1.1]: https://github.com/sytone/botnexus/compare/v0.1.0...v0.1.1

