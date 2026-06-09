---
title: "Release v0.2.2"
description: "Release notes for BotNexus v0.2.2"
date: "2026-06-09"
---

# Release v0.2.2

> **Released:** 2026-06-09
>
> **Full diff:** [v0.2.1...v0.2.2](https://github.com/sytone/botnexus/compare/v0.2.1...v0.2.2)

## [0.2.2] - 2026-06-09

### ✨ Features

- **copilot:** Parse copilot_usage billing snapshot and emit Activity tags (Phase 5B, #810) (#980)
- **portal:** Add missing data-testid attributes to key UI elements (#984)
- **portal:** Group conversations in sidebar with collapsible scheduled section (#987)
- **portal:** Add descriptive tool call summaries with emoji and context in chat panel (#992)
- **portal:** Add ARIA dialog attributes and Escape key handling to overlay panels (#994)
- **prompts:** Add SectionId to IPromptSection and register tool-enforcement section (#1007)
- **e2e:** Add TCP-level readiness probe to E2E test harness (#961)
- **prompts:** Add shell-efficiency and skills-guidance prompt sections (#1009)
- **prompts:** Add model-guidance prompt section with per-family detection (#1010)
- **prompts:** Add IPromptOverrideResolver and FilePromptOverrideResolver for section overrides (#1012)
- **portal:** Render compaction boundaries as styled separators in conversation history (#1016)
- **tools:** Add platform debug tool for read-only session and runtime inspection (#1018)
- **portal:** Add PWA support with manifest, service worker, and offline caching (#1021)
- **portal:** Add steering queue panel with per-conversation pending display (#1022)
- **cli:** Add global --target option for multi-instance support (#1027)
- **gateway:** Add IExtensionStateStore with SQLite persistence (#1028)
- **cron:** Add command action type for shell-based cron jobs (#1047)
- **gateway:** Add memory pressure diagnostics with threshold monitoring and REST endpoint (#1062)
- **provider:** Apply system_and_3 multi-breakpoint cache strategy for Anthropic (#905)
- **provider:** Add GitHub Models inference provider with free-tier model catalog (#914)
- **agents:** Add workspace sharing and path grants for sub-agent isolation (#1026)

### 🐛 Bug Fixes

- **portal:** Add JsonStringEnumConverter to AskUserInputType for SignalR serialization (#979)
- **portal:** Preserve streamed text during history reconciliation and reconnect (#981)
- **gateway:** Prevent cron session reuse by interactive channels (#986)
- **portal:** Wrap burger toggle test clicks in InvokeAsync for async handler (#989)
- **portal:** Add confirmation dialog to mobile new session action (#990)
- **gateway:** Detect and clean up abandoned tool calls before dispatching new user messages (#991)
- **streaming:** Synthesize failed tool results for orphan tool calls and skip whitespace-only content (#1011)
- **portal:** Wrap update badge test clicks in InvokeAsync for async handler (#1015)
- **portal:** Show agent dropdown on narrow desktop viewport (#1017)
- **tests:** Isolate ProviderCommandTests from static AnsiConsole writer disposal (#1031)
- **tests:** Use delta assertion in MCP warmup cache test for parallel safety (#1032)
- **docs:** Upgrade vitepress to 2.x to resolve vite and esbuild security advisories (#1035)
- **tools:** Add 5-second regex timeout to GrepTool to prevent ReDoS (#1045)
- **provider:** Change Copilot limits dict to JsonElement for mixed-type values (#1048)
- **gateway:** Wire ToolEnforcement, ShellEfficiency, SkillsGuidance, and ModelGuidance sections into prompt pipeline (#1053)
- **tests:** Serialize AnsiConsole-dependent test classes to prevent static writer race (#1051)
- **tools:** Use ArgumentList for shell invocation to eliminate double-parse escaping (#1055)
- **tools:** Handle surrogate pairs in EditTool fuzzy text normalization (#1063)
- **streaming:** Persist tool starts immediately and add provider stall watchdog (#1058)
- **portal:** Wire SessionDebugPanel into MainLayout debug button (#1060)
- **gateway:** Debounce agent config reload notifications to prevent reload storm (#1076)
- **gateway:** Add liveness watchdog and activity tracking for deadlock detection (#1078)

### 📖 Documentation

- **gateway:** Correct stale comments referencing deleted phases and obsolete infrastructure (#1033)
- **training:** Fix stale BotNexus.Providers.* namespace references (#1034)
- **cli:** Document .NET 10 SDK requirement in README and NuGet package (#1036)
- Add provider pages, debug tool extension, update prompt pipeline and sidebar structure (#1037)
- **tools:** Add shell execution feature guide and update configuration docs (#1056)
- Add extension pages, SignalR channel docs, and CLI --target option (#1064)
- **channels:** Remove stale WebSocket terminology and align with SignalR architecture (#1079)

### 🔨 Refactor

- **gateway:** Remove dead session metadata conversationStatus shadow writes (#1020)

[0.2.2]: https://github.com/sytone/botnexus/compare/v0.2.1...v0.2.2

