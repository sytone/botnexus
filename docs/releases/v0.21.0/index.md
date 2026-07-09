---
title: "Release v0.21.0"
description: "Release notes for BotNexus v0.21.0"
date: "2026-07-09"
---

# Release v0.21.0

> **Released:** 2026-07-09
>
> **Full diff:** [v0.20.0...v0.21.0](https://github.com/sytone/botnexus/compare/v0.20.0...v0.21.0)

## [0.21.0] - 2026-07-09

### ✨ Features

- **sessions:** Preserve relevant files across compaction summary (#1812)
- **sessions:** Add opt-in render-time secret redactor for transcript export (#1815)
- **portal:** Add windows pwa deep-integration manifest members (#1818)
- **gateway:** Add agent-level thinking and context configuration (#1819)
- **persistence:** Add periodic WAL checkpoint hosted service (#1821)
- **prompts:** Require loading partially-relevant skills before acting (#1834)
- **skills:** Add create/patch quality guidance to skill_manage schema (#1835)
- **skills:** Add linked-file progressive disclosure for loaded skills (#1837)
- **skills:** Add explicit skill tool names for model ergonomics (#1842)
- **gateway:** Add conversation-level model, thinking, and context override (#1820)
- **skills:** Add skill usage telemetry with sqlite persistence (#1846)

### 🐛 Bug Fixes

- **portal:** Use surrogate-safe truncation in blazor preview helpers (#1813)
- **docs:** Stop excluding docs/api from vitepress build (#1817)
- **webtools:** Harden HtmlToText against unterminated tag tails (#1826)
- **mobile:** Liveness-verified hub reset on app resume (#1843)

### 📖 Documentation

- Daily documentation grooming 2026-07-08 (v0.19.0 + v0.20.0 release pages) (#1823)

### 🔨 Refactor

- **gateway:** Extract outbound fan-out delivery into IOutboundResponseDeliverer (#1814)
- **persistence:** Extract shared SqliteConnectionFactory (#1822)

[0.21.0]: https://github.com/sytone/botnexus/compare/v0.20.0...v0.21.0

