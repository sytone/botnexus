---
id: feature-config-management-api
title: "Configuration Management API — Full Config CRUD via Gateway REST + Dynamic Reload"
type: feature
priority: high
status: delivered
created: 2026-04-16
tags: [configuration, api, hot-reload, extensions, dynamic]
---

# Feature: Configuration Management API

**Status:** draft
**Priority:** high
**Created:** 2026-04-16

## Problem

All configuration (providers, gateway settings, extensions) requires manually editing `config.json` and restarting the gateway. The gateway should expose REST APIs for full config CRUD, with changes applied dynamically via the .NET `IOptionsMonitor<T>` pattern. Extensions should also be configurable via API with rich metadata (display names, descriptions, tips, documentation links).

## Requirements

### Must Have
- REST API for all config sections: providers, gateway, agents, extensions, cron
- Changes written to `config.json` and applied dynamically (no restart)
- `IOptionsMonitor<T>` wired with `reloadOnChange: true` across all services
- Extension config model with metadata attributes (display name, description, tips, help URL)

### Should Have
- Config validation before applying (reject invalid, keep running config)
- Config change notifications via activity stream
- Rollback on validation failure
- Extension config schema discovery endpoint (`GET /api/extensions/{id}/config-schema`)

### Nice to Have
- Config diff/history (what changed, when, by whom)
- WebUI config editor driven by schema metadata
- Config import/export endpoints

## Scope

### 1. Dynamic Config Reload (from `improvement-dynamic-config-reload` spec)
- Wire `PlatformConfigLoader.Watch()` in `Program.cs`
- Replace `IOptions<T>` with `IOptionsMonitor<T>` in all services
- Connect `PlatformConfigAgentSource` to file watcher events

### 2. Config REST API
- `GET /api/config` — full config (redacted secrets)
- `GET /api/config/providers` — provider list with status
- `PUT /api/config/providers/{id}` — update provider config
- `GET /api/config/gateway` — gateway settings
- `PUT /api/config/gateway` — update gateway settings
- `GET /api/config/extensions` — extension configs
- `PUT /api/config/extensions/{id}` — update extension config

### 3. Extension Config Metadata
- Attribute-based config decoration:
  ```csharp
  public sealed class AudioTranscriptionOptions
  {
      [ConfigDisplay("Model Path", "Path to the Whisper GGML model file")]
      [ConfigTip("Download from https://huggingface.co/ggerganov/whisper.cpp")]
      public string ModelPath { get; set; } = string.Empty;

      [ConfigDisplay("Language", "Language code for transcription")]
      [ConfigOptions("en", "fr", "de", "es", "ja", "zh")]
      public string Language { get; set; } = "en";

      [ConfigDisplay("Max Concurrency", "Maximum simultaneous transcription operations")]
      [ConfigRange(1, 8)]
      public int MaxConcurrency { get; set; } = 1;
  }
  ```
- Schema endpoint returns JSON Schema with display metadata
- WebUI can render a config form from the schema

## Related Specs
- `improvement-dynamic-config-reload` — IOptionsMonitor wiring (subset of this spec)
- `bug-sqlite-session-lock` — separate concern but often surfaces during config changes
