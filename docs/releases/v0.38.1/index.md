---
title: "Release v0.38.1"
description: "Release notes for BotNexus v0.38.1"
date: "2026-07-30"
---

# Release v0.38.1

> **Released:** 2026-07-30
>
> **Full diff:** [v0.38.0...v0.38.1](https://github.com/sytone/botnexus/compare/v0.38.0...v0.38.1)

## [0.38.1] - 2026-07-30

### 🐛 Bug Fixes

- **agent:** Bound BeforeToolCall hook with fail-closed timeout (#2547)
- **#2548:** Wire agent-core diagnostics into the host logger (#2551)
- **#2552:** Reject webhook URLs with credentials or bad schemes (#2558)
- **#2388:** Queue inbound message when the agent is mid-turn (#2570)
- **#2564:** Reset SSE reconnect counter only on delivered events (#2569)
- **#2320:** Snapshot store collections before mobile chat render (#2567)
- **gateway:** Skip fan-out stream events to targets that cannot satisfy preconditions (#2559) (#2563)
- Produce SummarizationFailed skip reason on provider failure (#2562)
- **#2493:** Skip no-op update rebuild and narrow build scope (#2502)
- **#2555:** Bound response body reads with idle chunk timeout (#2579)
- **#2553:** Sanitise cron job name before display (#2578)
- **#2521:** Confirm tree kill before releasing process slot (#2577)
- **#2554:** Clamp missed-run scan to schedule activation (#2576)
- **#2528:** Stop activity table overflowing and derive row labels (#2581)
- **#2415:** Accept valid boxings for timeout_seconds and exec env (#2574)
- **#2417:** Preflight inline python -c and nested pwsh quoting (#2573)
- **#2568:** Inline textual attachment payloads (#2571)
- **#2383:** Apply agent fileaccess changes on config hot-reload (#2565)

### 📖 Documentation

- Daily documentation grooming 2026-07-30 (#2561)
- **#2580:** Document installing the cli without nuget.org access (#2582)

### 🔨 Refactor

- **#2575:** Replace BotNexus.Deploy.proj with Traversal dirs.proj (#2585)

[0.38.1]: https://github.com/sytone/botnexus/compare/v0.38.0...v0.38.1

