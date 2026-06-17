---
title: "Release v0.3.0"
description: "Release notes for BotNexus v0.3.0"
date: "2026-06-11"
---

# Release v0.3.0

> **Released:** 2026-06-11
>
> **Full diff:** [v0.2.2...v0.3.0](https://github.com/sytone/botnexus/compare/v0.2.2...v0.3.0)

## [0.3.0] - 2026-06-11

### ✨ Features

- **canvas:** Add canvas state domain model and SQLite persistence (#1073)
- **provider:** Add GitHub Models integration test suite and CI workflow (#1082)
- **gateway:** Add Docker sandbox isolation strategy with lifecycle management (#1083)
- **cli:** Add gateway install/uninstall for OS service integration (#1087)
- **provider:** Split system prompt at cache boundary marker for Anthropic (#1092)
- **canvas:** Add canvas state REST API endpoints for CRUD operations (#1097)
- **providers:** Add claude-opus-4.8 to Copilot built-in models (#1098)
- **gateway:** Add per-agent Docker sandbox configuration and DI registration (#1099)
- **canvas:** Add set_state, get_state, and clear_state actions to canvas tool (#1104)
- **canvas:** Add postMessage bridge for iframe-to-server state persistence (#1115)
- **#1108:** Add Satellite domain model, config schema, and CLI registration command (#1116)
- **gateway:** Add Docker sandbox workspace synchronization (#1118)
- **gateway:** Add satellite authentication and connection tracking (#1120)
- **canvas:** Add SignalR notifications for canvas state changes (#1121)
- **cli:** Add Ollama provider setup and diagnostic subcommands (#1122)
- **memory:** Add memory dreaming cron action for periodic consolidation (#1124)
- **channels:** Wire JWT auth into SignalR hub with claims-derived user identity (#1123)
- **gateway:** Add Docker sandbox tool execution routing and gateway callback (#1128)
- **agents:** Add built-in internal agents for common sub-agent roles (#1130)
- **providers:** Add dynamic model discovery at startup with built-in fallback (#1132)
- **cli:** Add debug sessions subcommand for direct SQLite inspection (#1135)
- **cli:** Add debug logs subcommand for direct log file inspection (#1136)
- **portal:** Expose participant roster and agent attribution for multi-agent conversations (#1137)
- **ci:** Add maintenance automation scripts for PR status, logs, and branch sync (#1141)
- **gateway:** Add threadpool and activity diagnostics REST endpoints (#1142)
- **extensions:** Add update action to DataStoreTool for modifying existing rows (#1163)
- **cron:** Add history action to CronTool for inspecting run outcomes (#1164)
- **extensions:** Add QMD knowledge base extension scaffold with config and backend interface (#1178)
- **cli:** Add debug db subcommand for raw database introspection (#1179)
- **cron:** Add run history retention service to purge old completed records (#1184)
- **gateway:** Add memory statistics REST endpoint for per-agent store diagnostics (#1185)
- **memory:** Add IAgentMemory abstraction with DTOs and factory interface (#1187)
- **portal:** Persist thinking content in session history for reload (#1197)
- **memory:** Add shared memory store configuration and registry with access control (#1207)
- **portal:** Add new conversation button to mobile overflow menu (#1211)
- **extensions:** Add QMD CLI backend and knowledge_search tool (#1215)
- **gateway:** Hydrate config.json with default values from schema contributors on startup (#1217)
- **cli:** Add debug cron subcommand for scheduler diagnostics (#1220)
- **memory:** Wire MemorySaveTool and MemorySearchTool through IAgentMemory abstraction (#1221)
- **cron:** Detect and log missed cron runs on gateway startup (#1225)
- **extensions:** Add count action to DataStoreTool for row counting (#1229)
- **gateway:** Expose standard rate limit headers on every response (#1230)
- **portal:** Add canvas panel to mobile UI as bottom sheet overlay (#1216)
- **gateway:** Add provider health check REST endpoint for API connectivity validation (#1233)
- **gateway:** Add session statistics REST endpoint for aggregate metrics (#1234)
- **webhooks:** Add run retention service to purge old completed webhook runs (#1168)
- **gateway:** Add configurable access policy for agent-to-agent exchanges (#1252)
- **memory:** Add shared store scope to memory search and save tools (#1255)
- **extensions:** Add knowledge_stores and knowledge_get tools to QMD extension (#1256)
- **gateway:** Add conversation budget system with daily caps, loop detection, and cooldown (#1254)
- **cron:** Add agent-converse action for scheduled inter-agent conversations (#1258)
- **extensions:** Add QMD index hosted service with auto-update and health tracking (#1259)
- **memory:** Add learning extraction pipeline with turn classification and knowledge routing (#1260)
- **skills:** Add SHA-256 trust catalog verification for skill script integrity (#1261)
- **cli:** Add debug gateway subcommand for live gateway diagnostics (#1263)
- **search:** Add Microsoft AI Search provider (api.microsoft.ai) (#1270)
- **cli:** Add compaction model doctor checks for expensive and missing summarization models (#1274)
- **memory:** Add shared store promotion during dreaming consolidation (#1276)

### 🐛 Bug Fixes

- **hooks:** Use instance-based HookDispatcher registration to share with extension loader (#1090)
- **portal:** Render settings and debug panels as centered modal overlays (#1089)
- **tests:** Increase debounce test wait margin for slow CI runners (#1086)
- **compaction:** Force compaction when user explicitly requests /compact (#1084)
- **provider:** Filter non-Anthropic thinking signatures in cross-provider replay (#1081)
- **portal:** Map ToolExecutionUpdateEvent to UserInputRequired stream event (#1093)
- **portal:** Move modal panels outside app-shell to escape flex containment (#1095)
- **compaction:** Use PreservedTurns=0 for forced compaction to guarantee summarization (#1096)
- **tests:** Isolate SteeringLoopTests with per-test API provider names (#1102)
- **gateway:** Bypass orchestrator queue for steering to prevent serialization deadlock (#1103)
- **hooks:** Pass AgentDescriptor via BeforePromptBuildEvent to avoid stale DI references (#1105)
- **gateway:** Use GetOrCreateAsync in Steer to eliminate handle lookup race (#1114)
- **compaction:** Surface failure reason in portal and fix adaptive model empty response (#1113)
- **gateway:** Filter non-live entries from auto-title guard condition (#1117)
- **gateway:** Add threadpool watchdog, lock timeout logging, and health endpoint hardening (#1125)
- **tests:** Make GitHub Models integration tests resilient to API degradation (#1131)
- **compaction:** Add configurable timeout and circuit-breaker for hung LLM summarization calls (#1157)
- **security:** Extract SSRF validator as shared utility for reuse across tools (#1158)
- **channels:** Evict stale streaming and error-reply state in Telegram adapter (#1159)
- **gateway:** Resolve skill paths to sandbox-relative locations for Docker isolation (#1160)
- **channels:** Register SignalR hub auth policy in production via IServiceContributor (#1188)
- **webhooks:** Validate callback URLs against SSRF and use IHttpClientFactory (#1167)
- **portal:** Sort built-in agents to bottom of agent dropdown (#1194)
- **portal:** Skip empty message bubble when response is thinking-only (#1195)
- **canvas:** Inject bridge SDK before user scripts to eliminate async timing race (#1196)
- **gateway:** Silently drop thinking-only responses instead of showing confusing stall notice (#1200)
- **portal:** Expand thinking block by default instead of collapsed (#1214)
- **tools:** Reject unknown properties when additionalProperties is false (#1224)
- **tests:** Add 'you' to GenericWindowsAccounts allowlist for docs placeholder (#1241)
- **skills:** Validate symlink resolution before skill file writes (#1242)
- **gateway:** Skip session entry for NO_REPLY responses (#1243)
- **memory:** Add retry logic for transient SQLite and I/O failures on read operations (#1244)
- **providers:** Honor Retry-After header for short rate-limit windows instead of blind backoff (#1245)
- **compaction:** Resolve API key from GatewayAuthManager for summarization calls (#1253)
- **gateway:** Bind gateway.auxiliary.titling as object to stop PlatformConfig crash (#1257)
- **portal:** Include cron-assigned conversations in scheduled sidebar group (#1275)
- **config:** Add JsonConverter for TitlingConfig to handle legacy string format (#1278)

### 📖 Documentation

- Daily documentation grooming 2026-06-10 (#1140)
- **domain:** Fix HIGH and MED stale XML doc comments from post-579 audit (#1144)
- Daily documentation grooming 2026-06-11 (#1226)

### 🔨 Refactor

- **gateway:** Eliminate repeated .From() conversions in AgentsController (#1126)
- **gateway:** Construct typed AgentId and SessionId once at method entry in GatewayHost (#1127)
- **gateway:** Delegate daily memory loading to IAgentMemory.GetPromptContextAsync() (#1267)
- **memory:** Delegate session indexing to IAgentMemory.OnSessionCompleteAsync() (#1269)

### 🧪 Testing

- **agent:** Add comprehensive steering loop tests covering all injection scenarios (#1080)
- **gateway:** Add steering pipeline tests proving queue serialization bug (#1094)
- **cli:** Add unit tests for SatelliteCommand list, register, and remove operations (#1146)
- **security:** Verify SignalR inbound auth rejects writes before authentication (#1169)

[0.3.0]: https://github.com/sytone/botnexus/compare/v0.2.2...v0.3.0
