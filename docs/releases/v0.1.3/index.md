---
title: "Release v0.1.3"
description: "Release notes for BotNexus v0.1.3"
date: "2026-05-05"
---

# Release v0.1.3

> **Released:** 2026-05-05
>
> **Full diff:** [v0.1.2...v0.1.3](https://github.com/sytone/botnexus/compare/v0.1.2...v0.1.3)

## [0.1.3] - 2026-05-05

### 🐛 Bug Fixes

- **config:** Support toolIds ["*"] as all-tools wildcard (#141)
- **tests:** Make CancellationDuringStreaming deterministic (#142)
- **sessions:** Migrate orphaned sessions to default conversation on startup (#143)
- **portal:** Filter NO_REPLY sentinel from conversation UI (#144)
- **gateway:** Demote stale SignalR bindings on disconnect and fan-out failure (#145)

### 🔨 Refactor

- **gateway:** Decouple extension tool wiring via IAgentToolContributor (#146)
- **gateway:** Binding-first routing — remove default conversation fallback, add ReattachBindingAsync (#148)

[0.1.3]: https://github.com/sytone/botnexus/compare/v0.1.2...v0.1.3

