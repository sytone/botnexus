---
title: "Release v0.1.9"
description: "Release notes for BotNexus v0.1.9"
date: "2026-05-08"
---

# Release v0.1.9

> **Released:** 2026-05-08
>
> **Full diff:** [v0.1.8...v0.1.9](https://github.com/sytone/botnexus/compare/v0.1.8...v0.1.9)

## [0.1.9] - 2026-05-08

### ✨ Features

- **gateway:** Align memory authoring with OpenClaw model (#179)

### 🐛 Bug Fixes

- **webtool:** Invalidate cached Copilot MCP client on 400/401 and retry once (#161)
- **webtool:** Add structured logging to WebSearchTool and CopilotMcpSearchProvider (#162)
- **gateway:** Steer returns session ID; router reuses Expired sessions instead of creating new (#164)
- **portal:** Toggle CSS, archive conversation, steering feedback (#166)
- **portal:** Archived conversations reappear; sidebar not scrollable (#167)
- **portal:** Duplicate messages on default conversation; history not loading (#168)
- **crossworld:** Rename CrossWorldRelayRequest.ChannelAddress to ConversationId (#175)
- **cli:** Improve update git pull cancellation handling (#181)

### 📖 Documentation

- **architecture:** Gateway flow diagrams; refactor(domain): BindingId strong type (#170)
- **planning:** Preserve OpenClaw memory alignment planning branch (#182)
- **.squad:** Merge conversation project refactor session (#183)

### 🔨 Refactor

- **gateway:** Conversation-first routing — ConversationId on InboundMessage (#169)
- **domain:** ChannelAddress and ThreadId strong types; fix StaleChannelConnectionException (#174)
- **gateway:** Extract conversation stores into gateway conversations project (#178)
- **gateway:** Route conversations through dispatcher (#180)

### ⚙️ Miscellaneous

- Remove personal identifiers from tests and docs (#159)
- **squad:** Preserve tool timeout session state (#184)

[0.1.9]: https://github.com/sytone/botnexus/compare/v0.1.8...v0.1.9

