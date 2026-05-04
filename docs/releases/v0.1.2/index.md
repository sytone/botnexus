---
title: "Release v0.1.2"
description: "Release notes for BotNexus v0.1.2"
date: "2026-05-04"
---

# Release v0.1.2

> **Released:** 2026-05-04
>
> **Full diff:** [v0.1.1...v0.1.2](https://github.com/sytone/botnexus/compare/v0.1.1...v0.1.2)

## [0.1.2] - 2026-05-04

### ✨ Features

- **config:** Backup config.json before every write (#116)

### 🐛 Bug Fixes

- **config:** Remove mandatory type field validation for channel entries (#117)
- **config:** Populate JsonElement fields from raw JSON in PostConfigure (#122)
- **gateway:** Stamp BindingId after routing to prevent duplicate Telegram messages (#124)
- **gateway:** Carry OriginatingBinding through processing — fixes ThreadId on direct sends and streaming (#129)
- **portal:** Session reset preserves conversation history (#132)
- **telegram:** Harden inbound message security — allowedUserIds, reject channel posts, disable edited messages by default (#134)
- **sessions:** ResolveByBindingAsync null threadId must only match null-thread bindings (#136)
- **gateway:** Per-address conversation routing (#139)

### 📖 Documentation

- **channels:** Add Telegram configuration and security guide (#135)

### 🔨 Refactor

- **config:** Migrate from PlatformConfigLoader to IConfiguration (#119)
- **config:** Complete IOptionsMonitor migration — remove PlatformConfig singleton (#121)

[0.1.2]: https://github.com/sytone/botnexus/compare/v0.1.1...v0.1.2

