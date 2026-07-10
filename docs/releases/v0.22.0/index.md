---
title: "Release v0.22.0"
description: "Release notes for BotNexus v0.22.0"
date: "2026-07-10"
---

# Release v0.22.0

> **Released:** 2026-07-10
>
> **Full diff:** [v0.21.0...v0.22.0](https://github.com/sytone/botnexus/compare/v0.21.0...v0.22.0)

## [0.22.0] - 2026-07-10

### ✨ Features

- **mobile:** Add auto-retrying reconnect overlay for mobile client (#1857)
- **telemetry:** Add metrics core and OpenTelemetry SDK wiring (#1858)
- **providers:** Dynamic models declare thinking/context capabilities (#1859)
- **mobile:** Full PWA lifecycle handling and manifest hardening (#1860)
- **portal:** Expand agent editor to full AgentDefinitionConfig with clone action (#1865)
- **cron:** Prune noop cron sessions on configurable retention window (#1754) (#1869)
- **telemetry:** Add shared durable usage-telemetry primitive (#1871)

### 🐛 Bug Fixes

- **repo:** Add heartbeat output to pre-commit hook to prevent no-output starvation (#1847)
- **scripts:** Rebase PR branches onto main instead of merging (#1856)
- **gateway:** Keep agent loop alive across pre-compaction memory flush (#1855)
- **cli:** Terminate git clone args with -- to prevent option injection (#1864)
- **mobile:** Tune SignalR keepalive and reconnect for mobile hub path (#1872)

### 📖 Documentation

- Daily documentation grooming 2026-07-09 (v0.21.0 release page) (#1861)

### 🔨 Refactor

- **gateway:** Promote exchange-completion metadata to typed AgentExchangeCompletionState (#1863)
- **gateway:** Lift IConversationRouter.ResolveInboundAsync conversationId to typed ConversationId (#1866)
- **signalr:** Extract InboundMessage factory in GatewayHub (#1868)

### ⚙️ Miscellaneous

- **tests:** Consolidate Conversation.Tests into Conversations.Tests (#1867)

[0.22.0]: https://github.com/sytone/botnexus/compare/v0.21.0...v0.22.0

