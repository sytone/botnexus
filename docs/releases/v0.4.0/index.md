---
title: "Release v0.4.0"
description: "Release notes for BotNexus v0.4.0"
date: "2026-06-13"
---

# Release v0.4.0

> **Released:** 2026-06-13
>
> **Full diff:** [v0.3.0...v0.4.0](https://github.com/sytone/botnexus/compare/v0.3.0...v0.4.0)

## [0.4.0] - 2026-06-13

### ✨ Features

- **prompts:** Wrap system prompt sections in XML tags and consolidate tool enforcement (#1280)
- **extensions:** Add per-agent store scoping for QMD knowledge base (#1281)
- **sessions:** Add tool result trimming with age+size hybrid strategy (#1285)
- **gateway:** Add world event bus with subscription model and in-memory implementation (#1286)
- **extensions:** Enforce configurable row limit on DataStore query action (#1290)
- **tools:** Return response metadata in WebFetchTool output (#1295)
- **gateway:** Expose agent exchange budget diagnostics via REST endpoint (#1299)
- **tools:** Add per-call count parameter to WebSearchTool (#1302)
- **tools:** Add status filter to ConversationTool list action (#1303)
- **agent:** Add RunMetrics to AgentEndEvent for token and turn tracking (#1306)
- **gateway:** Add active loop tracking to diagnostics for capacity monitoring (#1307)
- **cli:** Add conversation management subcommands for list, inspect, and archive (#1311)
- **sessions:** Add markdown transcript export endpoint for session history (#1312)
- **gateway:** Add audit logging for conversation mutations (#1317)
- **extensions:** Integrate shared memory stores as QMD knowledge collections (#1318)
- **sessions:** Add delete action to SessionTool for expired and sealed sessions (#1327)
- **cli:** Add debug memory subcommand for agent memory diagnostics (#1328)
- **cron:** Enforce per-job execution timeout to prevent runaway jobs (#1291)
- **prompts:** Expose session identity in the agent runtime line (#1380)

### 🐛 Bug Fixes

- **gateway:** Add input length validation on conversation title, purpose, and instructions (#1315)
- **gateway:** Rename PlatformConfig.Version to PlatformVersion to avoid DOTNET_VERSION collision (#1283)
- **gateway:** Separate config and data directories to support read-only mounts (#1279)
- **gateway:** Make IConversationChangeNotifier optional to prevent DI crash in minimal configs (#1284)
- **tools:** Respect exchange access policy in ListAgentsTool CanConverse field (#1294)
- **tools:** Validate apiProvider against registered providers in agent creation and update (#1298)
- **gateway:** Skip fan-out for non-deliverable channel types like cron (#1321)
- **gateway:** Increase liveness watchdog thresholds to reduce false FATAL alerts (#1322)
- **extensions:** Reject multi-statement SQL in DataStore query action (#1331)
- **extensions:** Re-validate redirect targets to close WebFetch SSRF bypass (#1332)
- **extensions:** Evict exited processes from ProcessManager registry (#1335)
- **extensions:** Evict exited PIDs from ExecTool background-process registry (#1336)
- **tools:** Tolerate non-integer and out-of-range count in web search (#1339)
- **tools:** Reject multi-statement SQL in platform_debug raw_sql action (#1340)
- **tools:** Clamp agent_converse maxTurns to a configurable ceiling (#1342)
- **agents:** Clamp sub-agent spawn maxTurns and timeoutSeconds to configurable ceilings (#1346)
- **extensions:** Cap audio payload size in Whisper transcription handler (#1347)
- **tools:** Cap canvas set_state key length and value size (#1349)
- **memory:** Clamp memory_get limit and memory_search topK to configured ceilings (#1353)
- **tools:** Bound process output tail to a configurable ceiling (#1354)
- **gateway:** Harden Docker startup resilience for minimal and misconfigured environments (#1323)
- **tools:** Clamp shell command timeout to a configurable ceiling (#1355)
- **ci:** Add BotNexus.CodingAgent.Tests to solution so 224 tests run in CI (#1357)
- **tools:** Clamp grep limit to a configurable ceiling (#1359)
- **cron:** Tolerate non-integer and out-of-range limit in cron history (#1364)
- **extensions:** Tolerate non-integer and out-of-range limit in knowledge search (#1374)
- **gateway:** Enforce caller authorization on sub-agent kill endpoint (#1375)
- **extensions:** Reject multi-statement SQL in DataStore where-clause for delete, update, and count (#1418)
- **gateway:** Make ConversationRetentionHostedService change-notifier dependency optional via IEnumerable (#1376)
- **telegram:** Add secure webhook receiver with secret-token validation (#1409)
- **portal:** Tolerate non-integer delay seconds in tool-call description renderer (#1412)
- **extensions:** Tolerate non-integer and out-of-range limit in platform_debug pagination (#1419)

### 📖 Documentation

- Daily documentation grooming 2026-06-12 (#1310)
- Document conversation CLI command and fix debug gateway reference (#1361)

### ⚡ Performance

- **persistence:** Avoid full conversation hydrate for canvas-state existence checks (#1395)
- **persistence:** Scope FileSessionStore.ListByConversationAsync to matching sidecars (#1398)
- **providers:** Cache partial snapshot and tool-arg parse in OpenAIStreamProcessor (#1401)
- **sessions:** Compute GetStatsAsync with SQL aggregates instead of full hydration (#1416)

### 🔨 Refactor

- **skills:** Move SkillsController into the Skills extension as minimal API endpoints (#1287)
- **cli:** Collapse five duplicated git subprocess helpers into one RunGitAsync (#1396)
- **memory:** Extract shared filter-clause builder and bound the LIKE fallback (#1397)
- **config:** Split ConfigPathResolver.TryResolveToken into ResolveMember and ResolveIndex (#1399)
- **portal:** Consolidate stream-state reset into a single ConversationStreamState.Reset() (#1400)
- **gateway:** Extract shared RunExchangeLoopAsync for local and cross-world exchanges (#1402)
- **providers:** Move DetectCompat/CompatProfiles into Core CompatResolver (#1410)
- **api:** Extract conversation history assembly into a service (#1414)
- **gateway:** Extract conversation/session resolution from ProcessAsync (#1417)

### 🧪 Testing

- **cli:** Eliminate flaky CI failures from shared global test state (#1413)
- **cron:** Pin MissedRunDetection no-missed-runs test to a fixed time (#1415)

[0.4.0]: https://github.com/sytone/botnexus/compare/v0.3.0...v0.4.0
