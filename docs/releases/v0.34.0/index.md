---
title: "Release v0.34.0"
description: "Release notes for BotNexus v0.34.0"
date: "2026-07-23"
---

# Release v0.34.0

> **Released:** 2026-07-23
>
> **Full diff:** [v0.33.0...v0.34.0](https://github.com/sytone/botnexus/compare/v0.33.0...v0.34.0)

## [0.34.0] - 2026-07-23

### ✨ Features

- **scripts:** Break-glass gateway recovery script with interactive Copilot handoff (#2223)
- **#2232:** Add server-side Tools model, persistence, and CRUD API (#2238)
- **tools:** Add optimistic concurrency token to read and edit (#2239)

### 🐛 Bug Fixes

- **extensions:** Share System.IO.Abstractions with host to preserve IFileSystem identity (#2218)
- **#2226:** Stop dropping conversations past LRU cache capacity in list materialiser (#2227)
- **security:** Expand SecretRedactor to cover Basic/Bot/Proxy-Authorization/X-Api-Key and standalone Bearer (#2222)
- **tools:** Quarantine invalid agent descriptors instead of blocking tools (#2228)
- **gateway:** Preserve crash sentinel across multi-turn streams (#2229)

### 📖 Documentation

- **#2224:** Reflect apphost-exe gateway launch as default (post #2199) (#2225)

### 🔨 Refactor

- **#1382:** Replace isolation service locator with explicit tool providers (#2230)

[0.34.0]: https://github.com/sytone/botnexus/compare/v0.33.0...v0.34.0

