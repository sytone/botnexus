---
title: "Release v0.50.0"
description: "Release notes for BotNexus v0.50.0"
date: "2026-10-12"
---

# Release v0.50.0

> **Released:** 2026-10-12
>
> **Full diff:** [v0.49.1...v0.50.0](https://github.com/sytone/botnexus/compare/v0.49.1...v0.50.0)

## [0.50.0] - 2026-10-12

### ✨ Features

- **cron:** Record planned restart dispositions (#4776)
- **agents:** Preserve neutral evidence and detect outcome cycles (#4806)
- **sessions:** Correlate tool evidence with durable agent runs (#4807)

### 🐛 Bug Fixes

- **agents:** Bound alternating housekeeping loops (#4734)
- **gateway:** Expose unattributed private memory diagnostics (#4750)
- **providers:** Support configured per-model capacity metadata (#4751)
- **sessions:** Preserve missing legacy result evidence (#4752)
- **channels:** Converge host-shared extension dependencies (#4784)
- **search:** Enforce caller scope and hidden-record visibility (#4755)
- **agents:** Enforce peer grants before mirror admission (#4757)
- **security:** Isolate local child process environments (#4760)
- **testing:** Require positive sandbox child identity (#4761)
- **providers:** Enforce the anthropic request cache budget (#4762)
- **portal:** Coordinate mobile markdown render regression (#4764)
- **channels:** Await mobile canvas close event completion (#4766)
- **gateway:** Preserve blocking response cache usage (#4767)
- **gateway:** Expose sqlite allocator memory diagnostics (#4769)
- **gateway:** Expose scoped context budget provenance (#4772)
- **gateway:** Keep compaction budgets scoped and failures visible (#4773)
- **providers:** Bound response cache keys and preserve sub-agent errors (#4775)
- **gateway:** Retire subagents after owned cleanup finishes (#4771)
- **gateway:** Expose last-gc generation diagnostics (#4791)
- **providers:** Reject incomplete anthropic model discovery (#4765)
- **cli:** Validate named copilot provider types (#4724)
- **cli:** Report copilot catalogue activation restart requirement (#4799)
- **ci:** Authenticate coverage uploads and fail closed (#4800)
- **channels:** Converge agent365 host-shared dependencies (#4785)
- **sqlite:** Expose logical connection lifetime observations (#4801)
- **agents:** Return subagent completion through tool results (#4805)
- **gateway:** Reconcile immediate file watcher readiness changes (#4809)
- **portal:** Show separated copilot quota and bounded local usage (#4810)
- **portal:** Preserve workspace navigation during refresh (#4790)
- **diagnostics:** Expose last-gc collection characteristics (#4812)
- **gateway:** Sanitize malformed auth-file diagnostics (#4819)
- **cli:** Make agent exec the sole timeout owner (#4823)
- **gateway:** Pin checked follow-up conversation destinations (#4825)

### 🔨 Refactor

- **persistence:** Make schema migration methods extensions (#4735)
- **github:** Make json projections extension methods (#4754)
- **config:** Make secret merge operations extension methods (#4770)
- **config:** Make patch operations extension methods (#4792)
- **streaming:** Make responses error extraction an extension (#4802)
- **streaming:** Make final block text reading an extension (#4808)
- **streaming:** Make responses usage parsing an extension (#4814)

### 🧪 Testing

- **cron:** Make prompt template fixture paths portable (#4758)
- **gateway:** Coordinate audit deadline store-entry observation (#4774)
- **cli:** Control readiness child lifetime (#4821)
- **portal:** Await encoded selection event completion (#4822)
- **cron:** Isolate ambient home fixtures (#4826)

### 🔧 CI/Build

- **docs:** Validate the documentation site on pull requests (#4768)

[0.50.0]: https://github.com/sytone/botnexus/compare/v0.49.1...v0.50.0

