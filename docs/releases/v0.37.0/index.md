---
title: "Release v0.37.0"
description: "Release notes for BotNexus v0.37.0"
date: "2026-07-26"
---

# Release v0.37.0

> **Released:** 2026-07-26
>
> **Full diff:** [v0.36.0...v0.37.0](https://github.com/sytone/botnexus/compare/v0.36.0...v0.37.0)

## [0.37.0] - 2026-07-26

### ✨ Features

- **#2132:** Add atomic session metadata and lifecycle mutations (#2351)
- **#2123:** Serialize webhook deliveries per canonical conversation (#2359)

### 🐛 Bug Fixes

- **#2368:** Link pr template by absolute url for vitepress (#2371)
- **#2349:** Make Remove-Worktree tests execute inside It blocks (#2350)
- **#2330:** Refuse to rebase a worktree with uncommitted work (#2361)
- **#2369:** Verify gateway process identity before killing a pid (#2382)
- **scripts:** Scope pre-commit hook to impacted projects with bounded steps (#2348)
- **#2358:** Validate config on reload and keep last-known-good on failure (#2362)
- **#2057:** Mutate raw config paths instead of typed root rewrites (#2352)
- **#2370:** Bound sqlite -wal growth with journal_size_limit (#2375)

### 🔧 CI/Build

- Add warning-first pr conventions guard with ui evidence check (#2318)

[0.37.0]: https://github.com/sytone/botnexus/compare/v0.36.0...v0.37.0

