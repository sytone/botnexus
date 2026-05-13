---
id: improvement-dynamic-config-reload
title: "Dynamic Configuration Reload — Hot-Reload Without Gateway Restart"
type: improvement
priority: high
status: in-progress
created: 2026-04-16
updated: 2026-07-28
tags: [configuration, hot-reload, options-pattern, dotnet, platform]
---

# Improvement: Dynamic Configuration Reload

**Status:** in-progress (Phase 1 largely done — Watch() wired, core services use IOptionsMonitor; ~15 services still use static IOptions)
**Priority:** high
**Created:** 2026-04-16

## Problem

Configuration changes to `config.json` require a full gateway restart. The .NET configuration framework supports `IOptionsMonitor<T>` and `reloadOnChange` for dynamic reload, but the platform doesn't use these correctly. `PlatformConfigLoader.Watch()` exists but is never called, and most services use `IOptions<T>` (static singleton) instead of `IOptionsMonitor<T>`.

## Current State

### What works (dynamic)
| Component | Pattern | Reloads? |
|-----------|---------|----------|
| CronScheduler | `IOptionsMonitor<CronOptions>` | ✅ |
| DefaultMessageRouter | `IOptionsMonitor<GatewayOptions>` | ✅ |
| ConfigModelFilter | `IOptionsMonitor<PlatformConfig>` | ✅ |

### What doesn't work (static at startup)
| Component | Pattern | Reloads? |
|-----------|---------|----------|
| PlatformConfigAgentSource | `IOptions<PlatformConfig>` | ❌ |
| AgentConversationService | `IOptions<GatewayOptions>` | ❌ |
| DefaultSubAgentManager | `IOptions<GatewayOptions>` | ❌ |
| DefaultAgentCommunicator | `IOptions<GatewayOptions>` | ❌ |
| GatewayHost | `IOptionsMonitor<CompactionOptions>` | ✅ (fixed) |
| GatewayHub | Needs verification | ❓ |
| SessionCleanupService | `IOptions<SessionCleanupOptions>` | ❌ |
| ApiKeyGatewayAuthHandler | Direct `PlatformConfig` injection | ❌ |
| Extension loader | Startup-only discovery | ❌ |

### Dead infrastructure
- `PlatformConfigLoader.Watch()` — implemented with debouncing, never called
- `PlatformConfigWatcher` — fires `ConfigChanged` event, nobody starts it
- `PlatformConfigAgentSource` — subscribes to `ConfigChanged` event that never fires

## Requirements

### Must Have
- Config file changes detected automatically (no restart)
- Agent definitions (add/remove/update) applied without restart
- Provider configuration (API keys, models) applied without restart
- Gateway options (compaction, cleanup, sub-agents) applied without restart

### Should Have
- API key / auth identity changes applied without restart
- Cron job config changes detected from config.json (not just database)
- Extension hot-loading (discover new extensions without restart)

### Nice to Have
- Config change notifications via activity stream (so WebUI can show "Config reloaded")
- Validation before applying changes (reject invalid config, keep running config)

## Proposed Fix

### Phase 1: Wire File Watcher + Fix Options Pattern
1. Call `PlatformConfigLoader.Watch()` in `Program.cs` after configuration registration
2. Register `PlatformConfig` with `IOptionsMonitor<T>` support (bind to `IConfiguration` with `reloadOnChange`)
3. Replace `IOptions<T>` with `IOptionsMonitor<T>` in all gateway services
4. Services read `_options.CurrentValue` instead of `_options.Value`

### Phase 2: Dynamic Agent Registry
5. Connect `PlatformConfigAgentSource` to live config changes (Watch event now fires)
6. `AgentConfigurationHostedService` already handles updates — verify it works end-to-end
7. Agent handles for removed agents are stopped and cleaned up

### Phase 3: Dynamic Auth + Extensions
8. Make `ApiKeyGatewayAuthHandler` rebuild identity map on config change
9. Add extension directory watcher for hot-loading new extensions

## Files to Change

### Phase 1 (Options pattern)
- `src/gateway/BotNexus.Gateway.Api/Program.cs` — start config watcher
- `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs` — registration changes
- All services using `IOptions<T>` → `IOptionsMonitor<T>`

### Phase 2 (Agent registry)
- `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigAgentSource.cs` — verify Watch works
- `src/gateway/BotNexus.Gateway/Agents/DefaultAgentRegistry.cs` — verify update propagation

### Phase 3 (Auth + extensions)
- `src/gateway/BotNexus.Gateway/Security/ApiKeyGatewayAuthHandler.cs`
- `src/gateway/BotNexus.Gateway/Extensions/AssemblyLoadContextExtensionLoader.cs`
