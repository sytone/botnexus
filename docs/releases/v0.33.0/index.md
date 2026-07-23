---
title: "Release v0.33.0"
description: "Release notes for BotNexus v0.33.0"
date: "2026-07-23"
---

# Release v0.33.0

> **Released:** 2026-07-23
>
> **Full diff:** [v0.32.0...v0.33.0](https://github.com/sytone/botnexus/compare/v0.32.0...v0.33.0)

## [0.33.0] - 2026-07-23

### ✨ Features

- **agents:** Support per-parent sub-agent budget overrides (#2207)
- **portal:** Show agent display name instead of generic assistant label (#2210)
- **#2126:** Generate provisional conversation title from first user message (#2211)

### 🐛 Bug Fixes

- **provider:** Classify provider-specific token-limit errors as context overflow (#2192)
- **extensions:** Emit managed dependency closure for extension assemblies (#2193)
- **sessions:** Skip and self-heal orphaned sessions with deleted conversations (#2194)
- **cli:** Stop doctor config prompt from hanging without interactive stdin (#2198)
- **agents:** Normalize pathological token-per-line sub-agent completion summaries (#2200)
- **platform:** Initialize watchdog state on first run (#2201)
- **portal:** Recover stuck turn-active input when RunEnded is missed (#2202)
- **#2199:** Launch gateway via apphost exe to avoid name-based dotnet kills (#2203)
- **gateway:** Verify scheduler responsiveness before fatal liveness alert (#2204)
- **cli:** Suppress self-referential command suggestions and qualify matches (#2205)
- **gateway:** Suppress unchanged config reload notifications and no-op writes (#2206)
- **#2136:** Stop registering sub-agent archetypes as named conversational agents (#2209)
- **tools:** Treat identical edit replacement as idempotent no-op (#2212)
- **config:** Preserve per-agent fields when merging agent defaults (#2215)
- **security:** Centralize tool-audit projection for blocking trigger runs (#2216)

### 📖 Documentation

- Backfill v0.29.0/v0.30.0/v0.32.0 release pages and wire azure runner sidebar (#2214)
- **tools:** Clarify todo as generic agent execution checklist (#2217)

[0.33.0]: https://github.com/sytone/botnexus/compare/v0.32.0...v0.33.0

