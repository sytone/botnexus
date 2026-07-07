---
title: "Release v0.18.0"
description: "Release notes for BotNexus v0.18.0"
date: "2026-07-06"
---

# Release v0.18.0

> **Released:** 2026-07-06
>
> **Full diff:** [v0.17.0...v0.18.0](https://github.com/sytone/botnexus/compare/v0.17.0...v0.18.0)

## [0.18.0] - 2026-07-06

### ✨ Features

- **channels:** Render agent posts with their stamped assistant role (#1768)
- **portal:** Cache-control headers for Blazor static assets (#1779)
- **gateway:** Enable brotli/gzip response compression for dynamic api responses (#1784)
- **portal:** Ship a service worker for the mobile pwa (#1785)

### 🐛 Bug Fixes

- **sessions:** Fence stale post-run session writes after delete/reset mid-run (#1767)
- **copilot:** Bound OAuth token-exchange response reads (OOM-DoS hardening) (#1774)
- **gateway:** Lock streaming-turn auto-title wiring with a regression test and observable no-fire diagnostics (#1777)
- **portal:** Register service worker so the PWA is installable in Edge (#1778)

### 📖 Documentation

- Daily documentation grooming 2026-07-05 (conversation speak_as + v0.17.0 release page) (#1771)

### 🧪 Testing

- **subagents:** Make retention/eviction assertions wait for the real retirement stamp (#1770)

[0.18.0]: https://github.com/sytone/botnexus/compare/v0.17.0...v0.18.0

