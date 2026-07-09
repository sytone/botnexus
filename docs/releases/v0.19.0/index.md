---
title: "Release v0.19.0"
description: "Release notes for BotNexus v0.19.0"
date: "2026-07-07"
---

# Release v0.19.0

> **Released:** 2026-07-07
>
> **Full diff:** [v0.18.0...v0.19.0](https://github.com/sytone/botnexus/compare/v0.18.0...v0.19.0)

## [0.19.0] - 2026-07-07

### ✨ Features

- **copilot:** Honor advertised supported_endpoints in model discovery (#1798)

### 🐛 Bug Fixes

- **webtools:** Bound web_fetch response body reads to prevent OOM/DoS (#1796)

### 📖 Documentation

- **#1550:** Correct Telegram config to real channels:telegram schema (#1786)
- **tools:** Surface PowerShell/Python point-of-use gotchas in shell and exec descriptions (#1788)
- **webhooks:** Add python sender example for all response modes (#1789)
- **webhooks:** Add javascript and powershell sender examples (#1790)
- **webhooks:** Add webhook guide and API reference (#1791)
- Document ContentDelta payload role field in SignalR hub contract (#1792)
- **webhooks:** Add csharp sender example for all response modes (#1794)

### 🔨 Refactor

- **copilot:** Resolve enterprise/individual endpoint at the registration seam (#1787)
- **copilot:** Resolve the copilot mcp endpoint at the registration seam (#1799)

### 🧪 Testing

- **gateway:** Deterministic poll for agent hot-reload apply to fix flake (#1801)

[0.19.0]: https://github.com/sytone/botnexus/compare/v0.18.0...v0.19.0

