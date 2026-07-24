---
title: "Release v0.35.0"
description: "Release notes for BotNexus v0.35.0"
date: "2026-07-23"
---

# Release v0.35.0

> **Released:** 2026-07-23
>
> **Full diff:** [v0.34.0...v0.35.0](https://github.com/sytone/botnexus/compare/v0.34.0...v0.35.0)

## [0.35.0] - 2026-07-23

### ✨ Features

- **#2237:** Add age-based sweep for completed sub-agent workspace directories (#2240)
- **#453:** Add provider-level HTTP request/response auditing (#2241)
- **tools:** Preflight-parse powershell shell and exec commands before execution (#2250)
- **cli:** Make bare doctor run the complete diagnostic suite (#2257)
- **gateway:** Detect and auto-heal session consistency discrepancies (#2258)
- **build:** Reuse content-addressed validation receipts in pre-commit (#2264)
- **security:** Redact action-required secrets before cron external delivery (#2266)
- **channels:** Preserve typed sub-agent response kind through persistence and delivery (#2267)
- **gateway:** Add webhook-specific conversation retention policy (#2268)
- **portal:** Add user-defined conversation sections (#2269)
- **channels:** Reload late-loaded channel options on config change (#2270)

### 🐛 Bug Fixes

- **#2243:** Prevent sub-agent virtual session from hijacking active view (#2244)
- **extensions:** Unify extension assembly resolution against host-loaded assemblies (#2251)
- **gateway:** Reactivate archived conversations before internal message dispatch (#2252)
- **tooling:** Handle windows worktree locks without retry storms or branch deletion (#2253)
- **#2243:** Mark sub-agents at spawn so active-view guard survives registration race (#2254)
- **config:** Register JsonStringEnumConverter so schema emits string enums (#2256)
- **portal:** Support lossless config list and dictionary lifecycle editing (#2262)
- **gateway:** Deduplicate signalr stream delivery for internal turns with observer bindings (#2263)
- **cron:** Split cron definition updates from scheduler runtime state (#2265)
- **agents:** Make agent lifecycle persistence and registry changes atomic (#2271)

### 📖 Documentation

- **#221:** Add CONTRIBUTING.md and enhance root README (#2242)
- **#220:** Add arc42-lite architecture overview and ADR foundation (#2259)

### 🔨 Refactor

- **#2246:** Replace ActiveAgentId setter with single SelectView(source) seam (#2261)

### 🧪 Testing

- **#2226:** Add cross-store cache-capacity parity guard for list completeness (#2260)

[0.35.0]: https://github.com/sytone/botnexus/compare/v0.34.0...v0.35.0

