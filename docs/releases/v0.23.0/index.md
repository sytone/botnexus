---
title: "Release v0.23.0"
description: "Release notes for BotNexus v0.23.0"
date: "2026-07-10"
---

# Release v0.23.0

> **Released:** 2026-07-10
>
> **Full diff:** [v0.22.0...v0.23.0](https://github.com/sytone/botnexus/compare/v0.22.0...v0.23.0)

## [0.23.0] - 2026-07-10

### ✨ Features

- **agent365:** Add Channels.Agent365 adapter for M365 Agents SDK (Register tier) (#1890)
- **portal:** Add desktop Home / Activity dashboard (#1891)
- **portal:** Add archive-confirm setting and fix settings cog rendering (#1895)
- **config:** Add sidebar section navigation to the platform config UI (#1896)
- **config:** Dynamic + static option sources for provider select widgets (#1898)
- **telemetry:** Add platform hot-path metrics (#1899)
- **gateway:** Add minidump-on-crash and last-chance fault handler (#1908)
- **telemetry:** Add extension SDK telemetry seam (#1920)
- **telemetry:** Add metrics read/scrape endpoint (#1923)

### 🐛 Bug Fixes

- **tests:** Pass typed ConversationId to ResolveInboundAsync in routing scenarios (#1874)
- **copilot:** Omit peer OAuth error_description from exception message (#1886)
- **portal:** Key ChatPanel message loop to stop streamed-message garble (#1897)
- **canvas:** Pin ConversationCanvasController route to api/conversations (#1902)
- **cli:** Discover all registered databases in debug db command (#1905)
- **auto-title:** Title live portal/streaming and agent-initiated conversations (#1907)
- **cli:** Use --no-local git clone for install to avoid object-store copy timeout (#1911)
- **docs:** Ignore repo scripts/ links in vitepress dead-link check (#1912)
- **conversations:** Tolerate concurrent delete during list enumeration (#1915)
- **config:** Bind SectionKey as expression so config sections actually filter (#1917)
- **portal:** Send credentials with PWA manifest fetch behind auth proxy (#1921)

### 📖 Documentation

- Daily documentation grooming 2026-07-10 (v0.22.0 release page + signalr-mobile-keepalive sidebar) (#1883)
- **channels:** Add managed-identity Service Bus deployment example (#1904)

### 🔨 Refactor

- **telemetry:** Split OTel-free primitives into Gateway.Telemetry.Abstractions (#1873)
- **persistence:** Unify SqliteConversationStore column migrations into race-tolerant EnsureColumnAsync (#1887)

### 🧪 Testing

- **cli:** Assert --no-local precedes the -- terminator in clone args (#1913)

### ⚙️ Miscellaneous

- **repo:** Pre-authorize testhost.exe firewall rules before running tests (#1906)

[0.23.0]: https://github.com/sytone/botnexus/compare/v0.22.0...v0.23.0

