---
title: "Release v0.24.0"
description: "Release notes for BotNexus v0.24.0"
date: "2026-07-13"
---

# Release v0.24.0

> **Released:** 2026-07-13
>
> **Full diff:** [v0.23.0...v0.24.0](https://github.com/sytone/botnexus/compare/v0.23.0...v0.24.0)

## [0.24.0] - 2026-07-13

### ✨ Features

- **telemetry:** Add config-gated OTLP export for remote collection (#1925)
- **cli:** Add redacted agent export command (#1928)
- **api:** Filter sub-agents and built-ins from agent list by default (#1940)
- **#1888:** Add at-a-glance summary stat strip to Activity dashboard (#1945)
- **cli:** Add on-demand prune of terminal sub-agent workspaces (#1947)
- **portal:** Add pop-out modal for tool args and results (#1953)

### 🐛 Bug Fixes

- **docs:** Escape raw <id> breaking VitePress build (#1924)
- **security:** Add Telegram bot-token pattern to SecretRedactor (#1930)
- **gateway:** Stop liveness watchdog firing false FATAL alerts when idle (#1932)
- **security:** Use timing-safe comparison for gateway api key auth (#1938)
- **portal:** Close unclosed .agent-card-last-activity CSS rule breaking settings modal (#1944)

### 📖 Documentation

- Daily documentation grooming 2026-07-11 (v0.23.0 release page + sidebar orphans + vitepress build fix) (#1926)

### Build

- **deps:** Bump esbuild and vite (#1939)

[0.24.0]: https://github.com/sytone/botnexus/compare/v0.23.0...v0.24.0

