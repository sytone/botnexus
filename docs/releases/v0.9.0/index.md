---
title: "Release v0.9.0"
description: "Release notes for BotNexus v0.9.0"
date: "2026-06-23"
---

# Release v0.9.0

> **Released:** 2026-06-23
>
> **Full diff:** [v0.8.1...v0.9.0](https://github.com/sytone/botnexus/compare/v0.8.1...v0.9.0)

## [0.9.0] - 2026-06-23

### ✨ Features

- **security:** Add SecurityEvent model and trusted ring-buffer sink (#1533)
- **cron:** Add opt-in DeleteAfterRun cleanup for ephemeral run sessions (#1571)

### 🐛 Bug Fixes

- **cli:** Align port-availability probe with the gateway wildcard bind (#1537)
- **tools:** Coerce losslessly-safe tool argument shapes before rejecting (#1562)
- **tools:** Return nearest-line diagnostic on edit 0-match instead of bare error (#1563)
- **memory:** Sanitize control/role-injection markup before indexing transcript to memory (#1569)

### 📖 Documentation

- Backfill v0.7.0-v0.8.1 release pages + CLI reference accuracy fixes (#1534)

### ⚡ Performance

- **persistence:** Bound SQLite session/conversation caches and lock pools (#1530)

### 🔨 Refactor

- **gateway:** Extract PrepareTurnAsync from ProcessAsync (#1531)
- **providers:** Unify duplicated completions converter into Core (#1543)
- **gateway:** Split cross-world federation routing out of AgentExchangeService (#1544)
- **providers:** Unify OpenAI/Copilot Responses stream parsers into Core (#1546)
- **config:** Split ConfigPathResolver.TryConvertValue into a dispatcher (#1567)
- **sessions:** Decompose LlmSessionCompactor.CompactAsync (#1568)
- **gateway:** Split DefaultSubAgentManager spawn/completion into testable helpers (#1570)

### 🔧 CI/Build

- **security:** Add a guard for security-sensitive boundary files (#1529)
- **workflows:** Add per-ref concurrency groups to stop stacked CodeQL/CI runs (#1549)

[0.9.0]: https://github.com/sytone/botnexus/compare/v0.8.1...v0.9.0

