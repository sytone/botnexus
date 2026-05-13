---
title: "Release v0.1.10"
description: "Release notes for BotNexus v0.1.10"
date: "2026-05-13"
---

# Release v0.1.10

> **Released:** 2026-05-13
>
> **Full diff:** [v0.1.9...v0.1.10](https://github.com/sytone/botnexus/compare/v0.1.9...v0.1.10)

## [0.1.10] - 2026-05-13

### ✨ Features

- **gateway:** Inject memory prompt guidance from config (#190)
- **webui:** Manage locations from configuration UI (#205)
- **blazor-client:** Support URL-routed portal state (#209)
- **cli:** Show applied commit subjects on update (#213)

### 🐛 Bug Fixes

- **gateway:** Harden SignalR extension dispatcher activation (#186)
- Preserve latest conversation history on refresh (#187)
- Hydrate latest Quill history after refresh (#189)
- Route steering to active session and conversation (#191)
- **cron:** Surface scheduled runs and model overrides (#193)
- **cron:** Stable per-job conversation — all runs of a job share one conversation (#195)
- Enable conversation cleanup and sidebar scrolling (#197)
- Archive old cron conversation cleanup ids (#200)
- Keep sub-agent output in originating conversation (#201)
- Archive old cron conversation cleanup ids (#202)
- **blazor:** Guard chat enter interop after refresh (#203)
- Stabilize cron conversations and sidebar scrolling (#204)
- Enforce warnings as errors (#206)
- **cli:** Deploy freshest extension build outputs (#207)
- **conversations:** Keep deleted cron conversations hidden (#212)

### 📖 Documentation

- Enforce worktree policy for all code changes (#210)

### 🔨 Refactor

- **tests:** Mirror source project structure (#188)
- **domain:** Remove GetOrCreateDefaultAsync — replace with explicit named conversations (#196)
- **config:** Unify runtime config options (#208)

### ⚙️ Miscellaneous

- **squad:** Optimize team context files (#198)

[0.1.10]: https://github.com/sytone/botnexus/compare/v0.1.9...v0.1.10

