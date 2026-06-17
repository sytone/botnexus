---
title: "Release v0.5.0"
description: "Release notes for BotNexus v0.5.0"
date: "2026-06-14"
---

# Release v0.5.0

> **Released:** 2026-06-14
>
> **Full diff:** [v0.4.0...v0.5.0](https://github.com/sytone/botnexus/compare/v0.4.0...v0.5.0)

## [0.5.0] - 2026-06-14

### ✨ Features

- **prompts:** Wrap runtime-context block in internal delimiters (#1433)

### 🐛 Bug Fixes

- **tests:** Serialize provider-registry tests to stop static-registry race (#1421)
- **gateway:** Set explicit SignalR hub transport limits (#1428)
- **docker:** Install curl so container healthcheck reports healthy (#1434)

### 📖 Documentation

- Document debug memory and doctor config CLI commands (#1431)

### ⚡ Performance

- **gateway:** Avoid redundant session re-read in fan-out and extract DeliverToBindingAsync (#1423)
- **gateway:** Resolve conversation id once in InProcessIsolationStrategy (#1429)

### 🔨 Refactor

- **config:** Extract FinishLoad to dedup sync and async config loading (#1422)
- **providers:** Extract BuildRequestPayload into per-provider RequestBuilders (#1424)
- **gateway:** Consolidate sub-agent state into a single SubAgentRecord (#1425)
- **persistence:** Extract shared world-id and citizen logic for conversation stores (#1426)

[0.5.0]: https://github.com/sytone/botnexus/compare/v0.4.0...v0.5.0
