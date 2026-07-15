---
title: "Release v0.26.0"
description: "Release notes for BotNexus v0.26.0"
date: "2026-07-14"
---

# Release v0.26.0

> **Released:** 2026-07-14
>
> **Full diff:** [v0.25.0...v0.26.0](https://github.com/sytone/botnexus/compare/v0.25.0...v0.26.0)

## [0.26.0] - 2026-07-14

### ✨ Features

- **api:** Sparse fieldsets via ?fields= (#1782) (#1974)
- **tools:** Add field-selection to conversation tool (#1783) (#1975)
- **cli:** Add agent template import (#1978)
- **channels:** Add per-command approval hook to shared slash-command core (#1980)
- **api:** Add read-only sub-agent observability endpoint (#1986)
- **agent365:** Route OTel telemetry to Agent 365 observability via direct OTLP (#1991)
- **mobile:** Mirror desktop slash-command palette on mobile chat (#1993)
- **prompts:** Add cross-agent fabrication trip-wire to tool-use enforcement (#1996)

### 🐛 Bug Fixes

- **portal:** Replace placeholder PWA icons and fix favicon glyph (#1973)
- **portal:** Populate session debug system prompt from live handle (#1981)
- **auto-title:** Log the silent no-persist guards in GenerateAndSaveAsync (#1979) (#1982)
- **signalr:** Enforce per-method least-privilege scope on hub control methods (#1987)
- **exectool:** Block endpoint-redirection env vars in ValidateEnvKey (#1990)
- **gateway:** Enforce browser-origin allow-list on dev-mode auth path (#1943)
- **gateway:** Default titling model to gpt-5.6-luna and extract title from reasoning blocks (#1995)

### 📖 Documentation

- Daily documentation grooming 2026-07-14 (skill-review cron + v0.25.0 release page) (#1988)
- **agent365:** Fix 3 dead links in observability page (#2000)

### 🔨 Refactor

- **agents:** Make AGENTS.md discovery pull-based via get_agent_files tool (#1977)

### 🧪 Testing

- **arch:** Enforce src-tests mirror and no root-level test projects (#1976)
- **portal:** Dispatch pin-button clicks via InvokeAsync to fix CI flake (#1984)
- **integration:** Define seam-test convention and adopt config-save round-trip (#1985)
- **gateway:** Backfill unit-test mirror gaps for src projects (#1997)

### ⚙️ Miscellaneous

- **tests:** Delete ghost dirs and register solution orphans (#1992)

[0.26.0]: https://github.com/sytone/botnexus/compare/v0.25.0...v0.26.0

