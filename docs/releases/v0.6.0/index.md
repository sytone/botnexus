---
title: "Release v0.6.0"
description: "Release notes for BotNexus v0.6.0"
date: "2026-06-15"
---

# Release v0.6.0

> **Released:** 2026-06-15
>
> **Full diff:** [v0.5.0...v0.6.0](https://github.com/sytone/botnexus/compare/v0.5.0...v0.6.0)

## [0.6.0] - 2026-06-15

### ✨ Features

- **portal:** Show agent description in panel header with id on hover (#1457)
- **gateway:** Add RunStarted/RunEnded stream events for authoritative run-active signal (#1458)
- **portal:** Add Follow Up control and fix steer-button flicker between tool runs (#1459)
- **prompts:** Add anti-narration trip-wire to tool-use enforcement (#1463)
- **conversations:** Persist per-conversation todo state on the conversation row (#1472)
- **tools:** Add per-conversation todo tool (#1473)
- **prompts:** Re-inject conversation todo state into the system prompt each turn (#1474)
- **portal:** Add archive conversation action to the mobile overflow menu (#1476)
- **prompts:** Couple todo done-transition to a same-turn tool result (#1477)
- **portal:** Add per-conversation Todo panel with live SignalR updates (#1479)

### 🐛 Bug Fixes

- **conversations:** Populate participant roster in File and SQLite summaries (#1442)
- **docs:** Ignore dead links to srcExclude'd training pages in vitepress build (#1444)
- **config:** Accept Kestrel binding wildcards in gateway.listenUrl validation (#1445)
- **config:** Stop hydrating a default listenUrl into config.json (#1448)
- **persistence:** Set SQLite busy_timeout on every connection across stores (#1451)
- **mobile:** Always show Canvas menu item in mobile portal (#1452)
- **providers:** Retry transient HTTP 421 and transport faults on a fresh connection (#1454)
- **persistence:** Set SQLite busy_timeout on SqliteConversationStore connections (#1455)
- **gateway:** Make session compaction resilient to transient summary failures (#1456)
- **search:** Parse Web IQ webResults shape in MicrosoftAiSearchProvider (#1460)
- **portal:** Seed agent description into client state from REST on initial load (#1462)
- **portal:** Route steer/abort/compact to the displayed conversation's session (#1471)
- **portal:** Render user messages as Markdown like assistant messages (#1478)

### 📖 Documentation

- Add GitHub Models provider page and fix training dead-link check (#1449)

### 🔨 Refactor

- **providers:** Extract Completions ConvertMessages into *MessageConverter (#1443)
- **gateway:** Extract MapAgentEvent pure function from StreamCoreAsync (#1446)
- **providers:** Extract ParseSseStream into ResponsesStreamParser (#1447)
- **providers:** Promote shared Responses stream primitives to Core (#1453)
- **providers:** Collapse the four providers to thin shells over Core engines (#1461)

[0.6.0]: https://github.com/sytone/botnexus/compare/v0.5.0...v0.6.0
