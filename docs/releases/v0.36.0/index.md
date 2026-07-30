---
title: "Release v0.36.0"
description: "Release notes for BotNexus v0.36.0"
date: "2026-07-25"
---

# Release v0.36.0

> **Released:** 2026-07-25
>
> **Full diff:** [v0.35.0...v0.36.0](https://github.com/sytone/botnexus/compare/v0.35.0...v0.36.0)

## [0.36.0] - 2026-07-25

### ✨ Features

- **memory:** Disable qmd extension by default (#2274)
- **cli:** Reconcile persistent agent folders during doctor (#2279)
- **portal:** Render tools nav section from tools api (#2281)
- **gateway:** Make sub-agent workspace root configurable (#2282)
- **portal:** Add tools iframe host route with sandbox and embed-refusal fallback (#2283)
- **portal:** Add sidebar nav ordering model with server-side overrides (#2298)
- **#2300:** Add write-once ConversationSource across domain, persistence and DTOs (#2308)
- **portal:** Add start-conversation orchestration with persisted model override (#2314)
- **#2304:** Add immutable client ConversationState.Source and render projections (#2315)
- **#2235:** Add portal tools add/edit/remove management ui (#2341)
- **#2338:** Give sub-agent runs their own conversation id (#2342)

### 🐛 Bug Fixes

- **portal:** Make active view route-owned for deep-link and history survival (#2275)
- **gateway:** Recursively validate annotations across the config object graph (#2276)
- **gateway:** Make agent create/edit a lossless platform config round trip (#2278)
- **gateway:** Make conversation metadata and binding updates transactional (#2280)
- **#2284:** Use external GitHub URLs for AGENTS.md links breaking docs deploy (#2287)
- **security:** Redact aws secret access key value (#2291)
- **#2293:** Latch route-selection guard to stop Home.razor recursion loop (#2295)
- **#2294:** Deliver paperclip file attachments to agent message payload (#2296)
- **#2248:** Derive read-only from selection source and immutable agent kind, decouple roster from session-type mutation (#2299)
- **security:** Add GitLab token-prefix redaction patterns (#2297)
- **gateway:** Abort ask_user cleanly on argument validation failure (#2155)
- **#2324:** Add assign affordance for conversation sections (#2332)
- **#2345:** Allowlist the zero-dependency wire assembly in wasm fence (#2346)
- **#2333:** Skip vanished entries when building workspace tree (#2336)

### 📖 Documentation

- **#219:** Add REST API reference index and grounded controller pages (#2272)
- **architecture:** Add arc42 overview, C4 diagrams, and seed ADRs (#2288)
- **development:** Add debugging guide for gateway, extensions, and webui (#2289)
- Daily documentation grooming 2026-07-24 (#2292)
- Standardise pr body and squash-commit templates (#2313)

### 🔨 Refactor

- **#2316:** Enforce write-once immutability on domain identity properties (#2319)
- **#2310:** Add single conversation-creation seam (#2321)
- **#2300:** Delete virtual-session inference and fence conversation provenance (#2328)
- **#2322:** Normalize ask_user into a channel-agnostic gateway seam (#2334)

### 🧪 Testing

- **extensions:** Add gateway boot smoke gate with full extension set deployed (#2277)
- **#2249:** Architecture + seam guardrails enforcing single-writer view selection (#2307)

### ⚙️ Miscellaneous

- **#2311:** Add legacy shim audit telemetry and lifecycle convention (#2337)

### 🔧 CI/Build

- **#2329:** Add guard preventing server-side dependencies in Blazor WASM payload (#2335)
- **deps-dev:** Bump postcss from 8.5.15 to 8.5.23 (#2339)

[0.36.0]: https://github.com/sytone/botnexus/compare/v0.35.0...v0.36.0

