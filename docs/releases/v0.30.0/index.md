---
title: "Release v0.30.0"
description: "Release notes for BotNexus v0.30.0"
date: "2026-07-21"
---

# Release v0.30.0

> **Released:** 2026-07-21
>
> **Full diff:** [v0.29.0...v0.30.0](https://github.com/sytone/botnexus/compare/v0.29.0...v0.30.0)

## [0.30.0] - 2026-07-21

### ✨ Features

- **maintenance:** Decouple autonomous worker capacity (#2172)

### 🐛 Bug Fixes

- **agents:** Preserve sub-agent timeout terminal reason (#2156)
- **agents:** Make sub-agent tool execution write-ahead durable (#2160)
- **validation:** Reject incomplete playwright runs (#2162)
- **providers:** Normalize mistral-family tool-call ids (#2166)
- **channels:** Preserve copilot messages deltas (#2171)
- **cli:** Extend gateway startup readiness timeout (#2175)

### 📖 Documentation

- **cli:** Correct agent creation quick start (#2176)

### 🧪 Testing

- **channels:** Cover service bus stream consolidation (#2165)

### ⚙️ Miscellaneous

- **validation:** Make remote validation authoritative (#2161)

[0.30.0]: https://github.com/sytone/botnexus/compare/v0.29.0...v0.30.0

