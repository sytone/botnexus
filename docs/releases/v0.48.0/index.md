---
title: "Release v0.48.0"
description: "Release notes for BotNexus v0.48.0"
date: "2026-10-07"
---

# Release v0.48.0

> **Released:** 2026-10-07
>
> **Full diff:** [v0.47.0...v0.48.0](https://github.com/sytone/botnexus/compare/v0.47.0...v0.48.0)

## [0.48.0] - 2026-10-07

### ✨ Features

- **agents:** Coordinate transient provider recovery (#4489)
- **cli:** Manage named copilot provider instances (#4514)
- **sessions:** Automate legacy invocation backfill (#4530)
- **a2a:** Accept stateless message responses (#4533)
- **skills:** Add containerized ui evidence workflow (#4539)
- **agents:** Finalize bounded sub-agent runs (#4546)
- **agents:** Measure tool result context leases (#4547)
- **gateway:** Request planned host shutdown (#4536)
- **agents:** Unify sub-agent run detail (#4551)
- **portal:** Add export message range selection (#4557)
- **persistence:** Version gateway-owned sqlite stores (#4561)
- **gateway:** Add satellite connection data plane (#4562)
- **memory:** Run bounded resumable re-embedding (#4563)
- **sessions:** Report legacy backfill progress (#4571)
- **cli:** Validate live provider readiness (#4572)
- **agents:** Expose provider recovery lifecycle (#4575)
- **a2a:** Enforce delegation policy boundary (#4578)
- **portal:** Highlight active conversation composer (#4584)
- **sessions:** Persist run completion disposition (#4595)
- **agents:** Dispatch durable task flow continuations (#4549)
- **portal:** Manage conversation channel bindings (#4588)
- **agents:** Filter and page sub-agent run history (#4614)
- **persistence:** Version sqlite secret store reads and writes (#4616)
- **sessions:** Clear normalized legacy tool payloads (#4573)
- **gateway:** Expose conversation read cursor api (#4550)
- **portal:** Expose bounded client run-state diagnostics (#4609)
- **memory:** Converge sqlite memory schema version stamps (#4639)
- **channels:** Echo foreign user messages to telegram (#4675)
- **providers:** Expose provider instance types (#4686)
- **providers:** Add microsoft foundry responses authentication (#4693)
- **skills:** Bundle shared skill optimization prompt (#4694)
- **portal:** Distinguish provider instance types (#4700)
- **cli:** Add microsoft foundry provider configuration (#4702)
- **portal:** Add configurable active-run pulse (#4704)

### 🐛 Bug Fixes

- **portal:** Compact conversation composer controls (#4481)
- **gateway:** Terminalize cancelled queued webhooks (#4525)
- **gateway:** Make provider usage accounting truthful (#4527)
- **mobile:** Canonicalize standalone app launch (#4535)
- **tools:** Preserve output drains on pid collision (#4522)
- **cron:** Project agent completion outcomes (#4541)
- **mobile:** Isolate transcript repair from roster failure (#4552)
- **cli:** Define redundant read payload metric (#4553)
- **portal:** Corroborate activity session liveness (#4554)
- **gateway:** Activate home world sentinel (#4558)
- **agents:** Fence startup config source revisions (#4568)
- **portal:** Reconcile missed terminal run state (#4570)
- **portal:** Serve local release history (#4576)
- **portal:** Style the home composer surface (#4585)
- **channels:** Enforce final-only Teams streaming replies (#4586)
- **channels:** Separate telegram thinking from reply content (#4611)
- **channels:** Isolate tui console output (#4598)
- **ci:** Isolate local install fixture from release refs (#4623)
- **docs:** Highlight pwsh fences as powershell
- **ci:** Bound core test host lifetime (#4590)
- **agent:** Initialize Tools and Messages in AgentState
- **cli:** Block removal of assigned providers (#4582)
- **ci:** Preserve coverage collector argument in bounded runner (#4635)
- **agents:** Arbitrate sub-agent kill before snapshot capture (#4629)
- **channels:** Reject foreign service bus binding snapshots (#4628)
- **sessions:** Avoid hydrating transcripts during reconciliation (#4660)
- **sessions:** Unify shared database journal policy (#4664)
- **sessions:** Bound legacy tool payload cleanup (#4665)
- **sessions:** Commit session aggregate atomically (#4671)
- **providers:** Preserve overlays with legacy copilot config (#4672)
- **cli:** Block disabling assigned providers (#4673)
- **sessions:** Bound startup maintenance scans (#4674)
- **conversations:** Make retention archive compare-and-set (#4676)
- **portal:** Center composer send glyph (#4677)
- **sessions:** Serialize legacy agent id removal (#4678)
- **persistence:** Report incomplete wal checkpoints (#4680)
- **persistence:** Enforce sqlite foreign keys (#4681)
- **prompts:** Share narration threshold across guidance rungs (#4643)
- **portal:** Ignore stale run snapshots after live events (#4647)
- **signalr:** Close active-run subscription race (#4697)
- **agent:** Preserve durable ask-user waits (#4698)
- **providers:** Report rejected catalogue activation (#4690)
- **agent:** Isolate informational diagnostic observer failures (#4687)
- **agent:** Bound repeated non-progress tool turns (#4637)

### 📖 Documentation

- **agent:** Define extension-point naming
- **agent:** Define reusable policy rule composition
- **config:** Require cross-language sqlite authority review (#4605)

### ⚡ Performance

- **sessions:** Bound cleanup projection payload scans (#4692)
- **persistence:** Avoid repeated foreign key scans (#4695)
- **sessions:** Bound interrupted-turn hydration (#4699)

### 🔨 Refactor

- **gateway:** Publish conversation creation facts (#4548)
- **channels:** Route tui steering intent through gateway (#4560)
- **qmd:** Type memory backend agent ids (#4556)
- **portal:** Remove ambient conversation state (#4577)
- **channels:** Project agent365 conversation events (#4599)
- **gateway:** Publish conversation metadata facts (#4601)
- **gateway:** Publish conversation archive facts (#4612)
- **mcp:** Carry typed agent ids through warmup cache (#4615)
- **config:** Make raw json operations extensions (#4580)
- **agent:** Align extension contracts with responsibility names
- **agent:** Align extension test names and filenames
- **agent:** Organize extension contracts by responsibility
- **cron:** Carry typed alert target through rejection messages (#4618)
- **gateway:** Publish binding attachment facts (#4620)
- **agent:** Clarify extension contracts and runtime wiring
- **gateway:** Make conversation row mapping reader extensions (#4631)
- **sessions:** Make row mappers reader extensions (#4679)

### 🧪 Testing

- **gateway:** Signal long-turn queue phases (#4528)
- **channels:** Cover reset provenance projection (#4543)
- **channels:** Prove tui event projection (#4545)
- **gateway:** Pin auth cache read count (#4555)
- **agents:** Synchronize kill cleanup assertions (#4596)
- **channels:** Prove lifecycle event ordering (#4597)
- **config:** Ratchet production configuration authority (#4603)
- **channels:** Prove cross-conversation event isolation (#4610)
- **providers:** Prove sqlite provider add permits live agent assignment (#4617)
- **prompts:** Add todo behavior evaluator (#4581)
- **portal:** Restore routed active-composer coverage (#4613)
- **channels:** Isolate interleaved lifecycle projections (#4627)
- **agent:** Enforce extension contract organization
- **cli:** Bind latest source install to in-tree tool (#4650)
- **ci:** Coordinate powershell pipe-capacity regression (#4652)
- **cli:** Populate main from shallow source checkout (#4653)
- **providers:** Prove live gateway assignment after cli provider add (#4630)
- **prompts:** Summarize repeated behavior evidence (#4642)
- **portal:** Capture routed active composer browser matrix (#4658)
- **channels:** Require extension-owned event projection (#4696)
- **architecture:** Classify static helper candidates (#4703)

### ⚙️ Miscellaneous

- Add dirs.sln to .gitignore
- **deps:** Bump @vue/server-renderer and vue (#4655)
- **deps-dev:** Bump source-map-js from 1.2.1 to 1.2.2 (#4656)

### 🔧 CI/Build

- Retire redundant main health probe (#4651)
- **tests:** Surface bounded failure details in actions logs (#4654)

[0.48.0]: https://github.com/sytone/botnexus/compare/v0.47.0...v0.48.0

