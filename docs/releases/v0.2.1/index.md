---
title: "Release v0.2.1"
description: "Release notes for BotNexus v0.2.1"
date: "2026-06-07"
---

# Release v0.2.1

> **Released:** 2026-06-07
>
> **Full diff:** [v0.2.0...v0.2.1](https://github.com/sytone/botnexus/compare/v0.2.0...v0.2.1)

## [0.2.1] - 2026-06-07

### ✨ Features

- **portal:** Add Message History tab to SessionDebugPanel with role badges and paging (#933)
- **copilot:** Add CLI-fidelity request headers and response observability (Phase 5, #810) (#974)
- **conversations:** Add pin/unpin support with sorted display ordering (#973)
- **gateway:** Add log diagnostics ring buffer and REST endpoint for pattern monitoring (#975)

### 🐛 Bug Fixes

- **sessions:** Retry ReplaceHistoryAsync on phantom rollback errors with fresh connection (#963)
- **gateway:** Add compaction circuit breaker to skip after consecutive LLM failures (#964)
- **portal:** Style interrupt-steer button to match steer, stop, and send controls (#968)
- **gateway:** Truncate entry content in compaction prompt to prevent context overflow (#969)
- **cli:** Render startup banner before console encoding strands stderr writer (#978)

### 🔨 Refactor

- **domain:** Remove redundant Trim calls before domain primitive From and add architecture enforcement (#970)

### 🧪 Testing

- **portal:** Add vertical slice data flow tests for parent-child wiring (#971)
- **conversations:** Add abstract parity test base for IConversationStore with all 3 implementations (#976)

[0.2.1]: https://github.com/sytone/botnexus/compare/v0.2.0...v0.2.1

