---
title: "Release v0.47.0"
description: "Release notes for BotNexus v0.47.0"
date: "2026-10-01"
---

# Release v0.47.0

> **Released:** 2026-10-01
>
> **Full diff:** [v0.46.0...v0.47.0](https://github.com/sytone/botnexus/compare/v0.46.0...v0.47.0)

## [0.47.0] - 2026-10-01

### ✨ Features

- **agents:** Add generic a2a client foundation (#4346)
- **agents:** Add managed task flow ledger (#4347)
- **providers:** Add microsoft foundry responses authentication (#4352)
- **portal:** Add durable conversation read cursor stores (#4355)
- **plugins:** Expose lifecycle through CLI and API (#4357)
- **plugins:** Add generated marketplace catalog adapter (#4358)
- **providers:** Add named copilot instance routing (#4353)
- **portal:** Add prompt template picker (#4337)
- **agents:** Bundle trailguide workspace corpus (#4371)
- **cli:** Reconcile registered extension repository clones (#4373)
- **agents:** Persist accepted task results and delivery (#4374)
- **cli:** Bundle copilot session import prompt (#4377)
- **agents:** Make trailguide docs-first (#4387)
- **search:** Add memory and file contributors (#4391)
- **search:** Add bounded aggregation endpoint (#4393)
- **agents:** Advise oversized sub-agent budgets (#4394)
- **agents:** Persist governed agent proposals (#4398)
- **portal:** Share conversation action menu (#4408)
- **providers:** Add payload-free terminal server diagnostics (#4411)
- **config:** Make extension scopes explicit (#4412)
- **sessions:** Normalize new tool invocations (#4413)
- **tools:** Add typed result envelopes (#4414)
- **agent:** Preserve satellite execution context (#4416)
- **gateway:** Capture structured diagnostics (#4436)
- **skills:** Gate exact security acknowledgements (#4437)
- **agents:** Bundle trailguide guidance skills (#4442)
- **portal:** Show released version history (#4449)
- **cli:** Select source release targets (#4453)
- **mobile:** Add attachment and camera composer inputs (#4456)
- **portal:** Add opt-in simplified shell preference (#4457)
- **search:** Add agent conversation and session contributors (#4466)
- **search:** Add agent conversation and session contributors (#4466)
- **sessions:** Backfill legacy tool invocations (#4486)
- **agents:** Lease tool result detail in live context (#4488)
- **skills:** Emit revision-pinned security evidence (#4496)
- **portal:** Render chat attachments (#4500)
- **portal:** Simplify opted-in home hierarchy (#4503)
- **extensions:** Reconcile repositories across lifecycle commands (#4505)
- **agents:** Add a2a delegation extension (#4509)
- **gateway:** Derive stable conversation reader identity (#4515)
- **skills:** Explain budget refusals with bounded recovery (#4516)
- **agents:** Persist durable task flow waits (#4517)
- **portal:** Add configuration subsection navigation (#4519)
- **matrix:** Send read receipts after dispatch (#4520)

### 🐛 Bug Fixes

- **memory:** Apply configured temporal decay policy (#4348)
- **gateway:** Contain context export paths (#4349)
- **gateway:** Sanitize structured log text (#4350)
- **providers:** Reconcile config models on reload (#4351)
- **cli:** Discover live gateway during status (#4356)
- **cli:** Stop gateways discovered without pid files (#4360)
- **providers:** Redact streamed provider errors (#4361)
- **providers:** Retry rejected copilot effort once (#4362)
- **gateway:** Recover agent-origin interrupted turns (#4372)
- **portal:** Preserve clipboard image paste binding (#4381)
- **portal:** Keep conversation timestamps clear of actions (#4385)
- **agents:** Fail closed on compaction conflicts (#4386)
- **cron:** Route agent prompts through cron trigger (#4389)
- **gateway:** Deduplicate streamed tool audit starts (#4390)
- **cli:** Report every updated configuration backend (#4396)
- **memory:** Enforce memory expiry (#4397)
- **gateway:** Reject malformed reset durations (#4400)
- **ci:** Reject incomplete pull request inventories (#4401)
- **persistence:** Normalize sqlite database extensions (#4392)
- **tools:** Warn about powershell pid collisions (#4409)
- **cron:** Reconcile sealed run owners (#4410)
- **agents:** Validate parked run dispositions (#4415)
- **gateway:** Keep config diagnostics nonfatal (#4422)
- **providers:** Distinguish unavailable reasoning summaries (#4423)
- **portal:** Reconcile active run state (#4427)
- **providers:** Preserve unknown copilot billing (#4428)
- **config:** Migrate legacy extension settings safely (#4429)
- **security:** Enforce filesystem root denials (#4433)
- **gateway:** Enforce prompt file access policy (#4432)
- **tests:** Disable shared PowerShell startup profiles (#4434)
- **portal:** Make client view selection explicit (#4443)
- **portal:** Reconcile generated guide assets (#4447)
- **portal:** Preserve confirmation danger contrast (#4450)
- **sessions:** Honor jsonl write cancellation (#4451)
- **skills:** Make repeat loads context-aware (#4454)
- **portal:** Show cron failure reasons in activity (#4455)
- **cli:** Retry atomic extension deployment locks (#4464)
- **skills:** Enable shared management by default (#4477)
- **portal:** Replace export label with save icon (#4478)
- **portal:** Preserve desktop sidebar across navigation (#4484)
- **agents:** Recover terminal automated turns (#4487)
- **portal:** Surface canvas submission outcomes (#4491)
- **providers:** Use discovered Copilot models (#4492)
- **agents:** Preserve bounded sub-agent evidence (#4493)
- **portal:** Keep pin state route-owned (#4498)
- **portal:** Clear stale turn state from snapshots (#4507)
- **agents:** Retry once after provider auth rejection (#4508)
- **gateway:** Bound sqlite audit persistence (#4510)
- **providers:** Report runtime activation separately (#4512)
- **gateway:** Propagate webhook execution deadlines (#4513)
- **ci:** Preserve startup probe cleanup ownership (#4521)
- **tools:** Dispose terminal web fetch responses (#4524)

### 📖 Documentation

- **providers:** Define provider account assignment semantics (#4354)
- **development:** Document issue filing fallback (#4395)
- **tools:** Qualify background process lifecycle guarantees (#4417)
- **platform:** Reconcile configuration and prompt contracts (#4452)
- **cli:** Reconcile command-local options (#4518)

### 🔨 Refactor

- **gateway:** Publish interrupted-turn session facts (#4446)
- **config:** Type agent configuration ids (#4458)
- **gateway:** Route legacy steering through inbound orchestrator (#4469)
- **providers:** Separate semantic generation and execution options (#4470)
- **agents:** Deprecate custom prompt files (#4472)
- **gateway:** Publish compaction session facts (#4468)
- **gateway:** Publish active-session reset facts (#4494)
- **sessions:** Type warmup agent ids (#4499)
- **mobile:** Make conversation identity route-owned (#4501)
- **gateway:** Unify inbound activity publication (#4502)
- **agents:** Remove custom prompt-file selection (#4506)

### 🧪 Testing

- **portal:** Cover spa deep-link caching (#4399)
- **exec:** Restore launch-time background pruning coverage (#4402)
- **cron:** Inventory persistence writes (#4425)
- **sessions:** Audit delta save mutations (#4438)
- **channels:** Add real conversation event seam (#4441)
- **validation:** Assert snapshot path safety (#4445)
- **channels:** Cover service bus event projection (#4448)
- **cron:** Cover persistence write seams (#4459)
- **channels:** Add multi-channel event scenario (#4465)
- **channels:** Cover matrix event projection (#4467)
- **config:** Prove automatic sqlite option reload (#4483)
- **channels:** Cover lifecycle event projection (#4495)
- **channels:** Prove service bus request isolation (#4497)
- **webhooks:** Cover persistence write seams (#4504)
- **gateway:** Cover external structured log classes (#4511)

### 🔧 CI/Build

- **buildtest:** Allow five concurrent validation runners (#4431)

[0.47.0]: https://github.com/sytone/botnexus/compare/v0.46.0...v0.47.0

