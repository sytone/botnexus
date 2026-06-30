---
title: "Release v0.14.0"
description: "Release notes for BotNexus v0.14.0"
date: "2026-06-29"
---

# Release v0.14.0

> **Released:** 2026-06-29
>
> **Full diff:** [v0.13.0...v0.14.0](https://github.com/sytone/botnexus/compare/v0.13.0...v0.14.0)

## [0.14.0] - 2026-06-29

### ✨ Features

- **config:** Add GET /api/config/schema reflection endpoint (#1680)
- **domain:** Add optional SpeakAs role override to InboundMessage (#1686)
- **provider:** Classify provider 401/403 as actionable ProviderAuthenticationException (#1689)
- **security:** Emit security events from exec approval boundary (#1693)
- **portal:** Add generic SchemaForm renderer in Core (#1714)
- **portal:** Add no-messages and load-error empty states to message view (#1697) (#1718)

### 🐛 Bug Fixes

- **blazor-client:** Use O(1) id->index map in HandleToolEnd instead of O(n) FindIndex (#1679)
- **#1681:** Degrade multi-bot Telegram outbound to allow-list routing instead of throwing (#1682)
- **cron:** Wire StreamSetupTimeoutMs so cloud compaction stalls fail fast (#1687)
- **#1698:** Strip leaked invoke/tool_use XML from assistant text in AssistantTextSanitizer (#1699)
- **mobile:** Show date on older chat messages (#1700)
- **gateway:** Wire AssistantTextSanitizer into delivery so leaked tool-call XML is stripped (#1698) (#1708)
- **titling:** Loosen auto-title guard so it can fire on a later turn (#1711)
- **#1602:** Add core.bare guard, versioned hooks, and config hygiene (#1712)
- **gateway:** Fold compaction summary into system prompt on resume so context survives (#1713)
- **agent:** Recover leaked invoke/tool_use XML into executable tool calls (#1709) (#1715)
- **conversations:** Make active-session reset best-effort on DELETE (#1719)
- **security:** Bound non-Copilot provider streaming reads (#1720)

### 📖 Documentation

- Daily documentation grooming 2026-06-28 (#1684)

### ⚡ Performance

- **persistence:** Prepare bulk-insert command once instead of rebuilding per row (#1683)
- **api:** List sessions via transcript-free summary read (#1716) (#1717)

### 🧪 Testing

- **#1602:** Fully isolate UpdateCommand git fixture from host repo (#1721)

[0.14.0]: https://github.com/sytone/botnexus/compare/v0.13.0...v0.14.0

