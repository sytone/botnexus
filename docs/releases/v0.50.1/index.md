---
title: "Release v0.50.1"
description: "Release notes for BotNexus v0.50.1"
date: "2026-10-16"
---

# Release v0.50.1

> **Released:** 2026-10-16
>
> **Full diff:** [v0.50.0...v0.50.1](https://github.com/sytone/botnexus/compare/v0.50.0...v0.50.1)

## [0.50.1] - 2026-10-16

### ✨ Features

- **cron:** Add conversation-bound loop chat commands (#4838)
- **sessions:** Add scoped compaction token ceilings (#4899)
- **gateway:** Add bounded structured diagnostic previews (#4845)
- **gateway:** Review and apply agent proposals (#4602)
- **sessions:** Expose fresh compaction policy diagnostics (#4920)
- **commands:** Show fresh compaction policy in context diagnostics (#4927)
- **sessions:** Measure legacy payload cleanup batches (#4931)
- **channels:** Notify readers after durable cursor advances (#4932)

### 🐛 Bug Fixes

- **gateway:** Make askuser timeout coverage deterministic (#4868)
- **browser:** Fail closed without destination enforcement (#4832)
- **tools:** Keep timeout fixtures alive until cancellation (#4872)
- **agents:** Preserve progress from opaque success receipts (#4847)
- **cron:** Terminate command processes on caller cancellation (#4844)
- **agents:** Preserve unsuccessful terminal safety and block order (#4849)
- **trailguide:** Bound atomic workspace refresh contention (#4887)
- **portal:** Show complete activity titles and cron job names (#4890)
- **anthropic:** Complete streams when cancelled before scheduling (#4894)
- **cron:** Handle disappearing proc stat in stop fixture (#4896)
- **conversations:** Acquire binding move locks by stripe (#4842)
- **cron:** Stop one hung/slow agent turn starving every scheduled job (#4731)
- **providers:** Complete canceled completions streams (#4904)
- **providers:** Settle canceled responses producers (#4903)
- **copilot:** Settle messages streams canceled before dispatch (#4906)
- **providers:** Classify integration mock cancellation as aborted (#4905)
- **sqlite:** Service pooled connection ownership in pr 4837 (#4837)
- **gateway:** Sanitize auth-file staleness diagnostics (#4875)
- **gateway:** Capture audit cancellation before deferred append (#4873)
- **deps:** Patch pkcs and signalr security dependencies (#4912)
- **extensions:** Reconcile generated guide asset inventory (#4877)
- **gateway:** Report running binary provenance in version endpoint (#4869)
- **cli:** Share active log reads and isolate file errors (#4870)
- **sessions:** Report payload-free sqlite capacity observations (#4878)
- **skills:** Publish shared prompts atomically (#4866)
- **portal:** Verify conversation menu top-layer rendering (#4907)
- **buildtest:** Preserve bounded test hang evidence (#4853)
- **tooling:** Verify worktree prune before cleanup success (#4900)
- **cli:** Reject config writes targeting missing agents (#4918)
- **skills:** Publish example skill seeds atomically (#4914)
- **gateway:** Watchdog must detect cron dispatch stalls (#4689) (#4732)
- **build:** Await diagnostic ownership cleanup completion (#4935)
- **cron:** Make heartbeat quiet-hours tests clock-independent (#4924)
- **config:** Accept watcher reloads before notifying options (#4929)
- **portal:** Repair copilot usage attribution and refresh (#4930)

### 📖 Documentation

- **extensions:** Correct repository lifecycle guidance (#4880)
- **platform:** Reconcile heartbeat thinking and memory contracts (#4851)

### ⚡ Performance

- **sessions:** Retain ordering benchmark and non-adoption evidence (#4913)

### 🔨 Refactor

- **gateway:** Publish binding removal facts (#4874)
- **streaming:** Make completions usage parsing an extension (#4871)
- **gateway:** Publish rest override update facts (#4925)

### 🧪 Testing

- **webhooks:** Coordinate processor readiness and deadline dispatch (#4828)
- **gateway:** Assert definitive locked audit failure (#4841)
- **gateway:** Make audit telemetry success fixture deterministic (#4833)
- **gateway:** Make post-run saturation assertions deterministic (#4830)
- **portal:** Enforce shared icon asset ownership (#4834)
- **config:** Pin dictionary schema metadata parity (#4835)
- **webtools:** Coordinate dns cancellation deterministically (#4820)
- **cli:** Control pid identity startup child lifetime (#4895)
- **buildtest:** Prove child completion independently of startup (#4852)
- **gateway:** Coordinate caller-abort cancellation deterministically (#4909)
- **tests:** Isolate atomic publication fixture within existing guard (#4921)

[0.50.1]: https://github.com/sytone/botnexus/compare/v0.50.0...v0.50.1

