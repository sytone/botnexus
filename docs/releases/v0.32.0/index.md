---
title: "Release v0.32.0"
description: "Release notes for BotNexus v0.32.0"
date: "2026-07-22"
---

# Release v0.32.0

> **Released:** 2026-07-22
>
> **Full diff:** [v0.31.0...v0.32.0](https://github.com/sytone/botnexus/compare/v0.31.0...v0.32.0)

## [0.32.0] - 2026-07-22

### ✨ Features

- **#1888:** Make agents stat card focus the agent filter (#2187)
- **gateway:** Raise default agent tool timeout to 300s with defaults inheritance (#2191)

### 🐛 Bug Fixes

- **cli:** Stop OAuth provider setup from overwriting baseUrl with Ollama defaults (#2180)
- **portal:** Shrink conversation title so header actions stay visible (#2186)
- **provider:** Strip transport CRLF on GPT-5.6 Copilot WebSocket deltas (#2189)
- **cron:** Persist tool call and result history for agent-prompt runs (#2190)

[0.32.0]: https://github.com/sytone/botnexus/compare/v0.31.0...v0.32.0

