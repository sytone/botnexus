---
title: "Release v0.1.13"
description: "Release notes for BotNexus v0.1.13"
date: "2026-05-21"
---

# Release v0.1.13

> **Released:** 2026-05-21
>
> **Full diff:** [v0.1.12...v0.1.13](https://github.com/sytone/botnexus/compare/v0.1.12...v0.1.13)

## [0.1.13] - 2026-05-21

### ✨ Features

- Implement WORLD.md world-level shared agent instructions (#249) (#259)
- **canvas:** Add canvas tool event flow and portal tab UI (#278)
- Strip ANSI escape sequences from shell/exec/process tool output (#294) (#296)
- **mobile:** Initial mobile Blazor WASM client (#304)
- **scripts:** Cross-platform watchdog installer with cron and systemd support (#317)
- **blazor:** Extract shared services into BlazorClient.Core Razor Class Library (#316) (#318)
- **telegram:** Switch to MarkdownV2 parse mode for proper markdown rendering (#328)
- **security:** Implement ExecApprovalManager with hardened approval tokens (#330)
- **channels:** Enable image send and receive support for all channels (#319) (#327)
- **security:** Redact secrets at write time and enforce sub-agent spawn policy (#333)
- **mobile:** Render markdown in assistant messages using shared JS renderer (#339)
- **workspace:** Delete and inline text editing (#348)
- **#359:** Conversation list auto-refresh via SignalR (#361)
- **#312:** HeartbeatAction + HeartbeatTrigger + SessionType.Heartbeat (Phase 1) (#365)
- **#312:** Phase 2 - transcript pruning + updatedAt preservation on HEARTBEAT_OK (#369)
- **#312:** Phase 3 - ActiveHoursConfig + smart cron expression generation (#373)
- **#312:** Phase 4 - HEARTBEAT.md emptiness pre-check in HeartbeatAction (#386)
- **workspace:** Scaffold workspace files for existing agents on startup (#388)
- **command:** Add /context slash command with context window usage breakdown (#389)
- **agents:** Implement ask_user agent tool (#301)
- **canvas:** Scope canvas to conversation via ConversationId parameter (#421)
- **cli:** Add cron subcommand -- list, get, delete, run, enable, disable (#426)
- **canvas:** Persist canvas HTML per conversation (#427)
- **memory:** Add pre-compaction memory flush turn (#428)
- **conversation:** Add message action to dispatch to existing conversations (#429)
- **gateway:** Add server default timezone for get_datetime tool fallback (#430)
- **conversations:** Add per-conversation instructions injected into system prompt (#432)
- **agents:** Add list_agents tool for agent discovery (#434)
- **providers:** Add stream setup timeout to abort stalled provider connections (#435)
- **extensions:** Add config schema declaration and enabled flag to extension manifest (#437)
- **scripts:** Add initialization and build scripts for agent setup
- **skills:** Include source path for available skills in list response (#439)
- **portal:** Add client-side preferences system with auto-expanding chat input (#440)
- **portal:** Add agent dashboard homepage with card grid (#438)
- **provider:** Add ProviderLoggingHandler DelegatingHandler for provider HTTP tracing (#454)
- **portal:** Route canvas to active conversation in portal client (#448)
- **memory:** Flush memory on session reset before archiving (#462)
- **portal:** Add extensions config panel with schema display (#463)
- **agents:** Add targetAgentId to spawn_subagent to spawn real registered agents (#464)
- **agents:** Add SubAgentRoles for role-based agent_converse grants (#465)

### 🐛 Bug Fixes

- **cli:** Ship prompt samples as embedded CLI resources (#247)
- Show effective configuration in API and UI (#268)
- Remove redundant agent ID label from chat canvas header (#292) (#293)
- **portal:** Strip ANSI escapes, decode Unicode, and add copy buttons in tool output (#295)
- **mobile:** Make mobile publish non-fatal; fix StaticWebAssetBasePath; guard index.html (#308)
- **mobile:** Derive gateway URL from NavigationManager at runtime (#309)
- **mobile:** Notify state after LoadConversationsAsync so dropdown re-renders (#310)
- **mobile:** Guard HandleStateChanged against ObjectDisposedException (#311)
- **mobile:** Move base href before stylesheets; remove PWA manifest (#313)
- **mobile:** Async OnAgentChanged — reload conversations and clear state on agent switch (#315)
- **workflows:** Fix daily-doc-updater safe_outputs bundle failure (#300)
- **mobile:** Force conversation dropdown re-render on agent switch via @key (#334)
- **mobile:** Store baseUrl in MobileGatewayClient to fix ERR_NAME_NOT_RESOLVED (#336)
- **reports:** Fix false truncation notice; add configurable size limit (#340)
- **scripts:** Restore watchdog recovery flow and script help (#346)
- **cron:** Update tests for API-backed CronConfigPanel (#288) (#347)
- **cron:** Panel polish - section headings, ID badge, fix Mustache comment bleed (#350)
- **#235:** Truncate sub-agent task prompt in spawn notification (#351)
- **#356:** Mobile chat bubble alignment -- case-insensitive role comparison (#357)
- **#358:** Collapse mobile tool call entries; tap to open detail modal (#360)
- **#362:** Flush session entries to store on TurnEnd (Option B) (#364)
- **portal:** Restore sidebar default width and delete icon alignment (#371)
- **subagents:** Fix sub-agent conversation view 404s on session history, workspace, and reports (#352)
- **compaction:** Guard against empty summary before deleting session history (#380)
- **heartbeat:** Re-provision heartbeat cron job when agent registered/updated at runtime (#391)
- **agents:** Remove done heuristic from agent_converse objective detection (#392)
- **cron:** Cron list and manage allow target-agent access (#393)
- **mobile:** Guard mobile chat auto-scroll to active agent/conversation only (#395)
- **conversations:** Update conversation UpdatedAt and notify clients on message activity (#396)
- **portal:** Refresh Workspace and Reports panels on agent turn-end (#397)
- **portal:** Exclude sub-agents from top-level agent sidebar dropdown (#398)
- **conversation:** Trigger agent turn when conversation(action=new, message=...) is called (#399)
- **cron:** Use job name as conversation title and pin conversation id after first run (#405)
- **gateway:** Preserve compaction summary when agent handle is recreated (#408)
- **cron:** Run multiple due jobs concurrently to prevent serial blocking (#410)
- **mobile:** Add URL routing to mobile Chat.razor -- restore agent/conversation on refresh and deep links (#404)
- **signalr:** Update stale session ConversationId on conversation switch (#422)
- **gateway:** Persist user message and crash sentinel before LLM call (#424)
- **portal:** Eliminate steering toggle flicker between tool calls (#431)
- **channels:** Resolve correct adapter for multi-instance same-type channel registrations (#433)
- **gateway:** Discard steering when agent is not running instead of falling through (#436)
- **tests:** Add two-arg IChannelManager.Get mock setup for fan-out tests
- **sessions:** Use long for token count intermediate sum to prevent int32 overflow (#451)
- **mobile:** Remove ApplyRouteSelectionAsync from HandleStateChanged to fix streaming lag (#452)
- **conversations:** Add bind-on-first-use to prevent duplicate portal conversations on reconnect (#443)
- **ci:** Use .NET 10 SDK in publish-cli workflow and add global.json (#457)
- **portal:** Hide desktop agent dropdown on mobile and route to /mobile/ path (#458)
- **portal:** Defer conversation list refresh while agent is streaming (#460)

### 📖 Documentation

- **user-guide:** Document ANSI escape stripping in shell tool output (#306)
- **agents:** Add conventional commit rules for PR titles (#324)
- **readme:** Update project structure, features, and fix stale links (#368)
- Fix stale memory/daily/ path in getting-started-release (#372)

### 🔨 Refactor

- **mobile:** Replace MobileState/MobileGatewayClient with Core services (#338)
- **gateway:** Remove legacy in-core skills prompt pathway (#461)

### 🧪 Testing

- **#349:** Stub BotNexus.splitter.init in WorkspacePanelTests bUnit fixture (#353)
- **gateway:** Replace Task.Delay(20) race with SemaphoreSlim ready-signal for #374 (#394)
- **gateway:** Fix sad-path test assertion for SignalR originator guard (#423)

### ⚙️ Miscellaneous

- Finalize examples layout and remove root agents samples (#277)
- Adding agentic workflows
- **scripts:** Install npm deps in repo init (#450)
- **extensions:** Add configSchema and enabled to all extension manifests (#449)

[0.1.13]: https://github.com/sytone/botnexus/compare/v0.1.12...v0.1.13

