---
title: "Release v0.27.0"
description: "Release notes for BotNexus v0.27.0"
date: "2026-07-15"
---

# Release v0.27.0

> **Released:** 2026-07-15
>
> **Full diff:** [v0.26.0...v0.27.0](https://github.com/sytone/botnexus/compare/v0.26.0...v0.27.0)

## [0.27.0] - 2026-07-15

### ✨ Features

- **servicebus:** Managed-identity auth and deploy NuGet dependencies (#2003)
- **signalr:** Attach channel identity to user across connection lifecycle (#2004)
- **config:** Complete [ConfigField] annotation coverage across config POCOs (#2016)

### 🐛 Bug Fixes

- **copilot:** Validate advertised endpoints.api host before routing bearer token (#2007)
- **portal:** Stop prior reply flashing as raw markdown on send (#2008)
- **servicebus:** Self-bind channel options from config on late load (#2011)
- **extensions:** Share configuration assemblies with dynamically-loaded extensions (#2015)

### 📖 Documentation

- Daily documentation grooming 2026-07-15 (sub-agent observability endpoint + v0.26.0 release page) (#2005)

### ⚡ Performance

- **conversations:** Rewrite ListForCitizen OR-join as sargable UNION (#2009)

### 🧪 Testing

- **e2e:** Add channel e2e test structure with portal playwright and stubs (#1998)

[0.27.0]: https://github.com/sytone/botnexus/compare/v0.26.0...v0.27.0

