---
title: "Release v0.8.0"
description: "Release notes for BotNexus v0.8.0"
date: "2026-06-17"
---

# Release v0.8.0

> **Released:** 2026-06-17
>
> **Full diff:** [v0.7.0...v0.8.0](https://github.com/sytone/botnexus/compare/v0.7.0...v0.8.0)

## [0.8.0] - 2026-06-17

### ✨ Features

- **gateway:** Persist pending ask_user prompt and hydrate it on connect (#1513)

### 🐛 Bug Fixes

- **signalr:** Reject hub session-key targeting reserved internal namespaces (#1514)
- **gateway:** Redact secrets for every config section, not just providers (#1527)
- **cron:** Defer scheduled heartbeat while an agent run is active (#1528)

### 📖 Documentation

- Add v0.3.0-v0.6.0 release pages and document provider copilot CLI (#1517)

### 🧪 Testing

- **security:** Add architecture fence for config/secret-echoing redaction (#1515)

[0.8.0]: https://github.com/sytone/botnexus/compare/v0.7.0...v0.8.0
