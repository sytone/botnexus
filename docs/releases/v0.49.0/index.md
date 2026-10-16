---
title: "Release v0.49.0"
description: "Release notes for BotNexus v0.49.0"
date: "2026-10-07"
---

# Release v0.49.0

> **Released:** 2026-10-07
>
> **Full diff:** [v0.48.0...v0.49.0](https://github.com/sytone/botnexus/compare/v0.48.0...v0.49.0)

## [0.49.0] - 2026-10-07

### ✨ Features

- **gateway:** Fence planned restart admission (#4718)

### 🐛 Bug Fixes

- **providers:** Reject unrouteable config api (#4712)
- **agents:** Preserve configured reasoning state (#4713)
- **sessions:** Bound legacy invocation backfill scans (#4716)
- **config:** Protect assigned provider instances (#4719)

### ⚡ Performance

- **sessions:** Bound summary history counts (#4717)

### 🔨 Refactor

- **persistence:** Make store identity checks extensions (#4715)

### 🧪 Testing

- **gateway:** Coordinate compactor cancellation assertions (#4707)
- **gateway:** Coordinate inbound non-truncation boundary (#4708)
- **agents:** Await sub-agent run disposal boundary (#4714)

[0.49.0]: https://github.com/sytone/botnexus/compare/v0.48.0...v0.49.0

