# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform built in C#/.NET
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-12 Complete.** Build green, 337+ tests passing. Bender leads runtime: agent execution, session lifecycle, channel dispatch, plugin hosting.

**Key Delivered Systems:**
- Telegram Bot API (long polling, streaming edits, markdown/thinking/tool formatting)
- WebSocket Channel + Activity Stream (`IStreamEventChannelAdapter`)
- Session lifecycle: suspend/resume, bounded queues, GatewayWebSocketHandler (sequence tracking, replay)
- MCP Server graceful initialization (per-server try/catch, skip failed)
- Cross-agent session scoping (deterministic session IDs, recursion prevention)
- Sub-agent spawning (DefaultSubAgentManager, tools, timeout/recursion/ownership enforcement)
- Phase 11 extension loading (AssemblyLoadContextExtensionLoader, manifest discovery)
- Gateway dispatcher rewire (IConversationDispatcher integration in GatewayHost + Hub)
- Conversation project extraction (stores + router → BotNexus.Gateway.Conversations)
- Conversation archive/close recoverability (sealed sessions, channel binding preservation)
- Tool timeout configuration (metadata-bag propagation from config → descriptor → runtime)
- CLI update git-pull cancellation (kill process tree, exit 130, drain stdout/stderr)

## Learnings

- MCP servers are optional dependencies — session succeeds even if one fails auth. Wrap per-server init in try/catch.
- `InternalChannelAdapter` must implement `IStreamEventChannelAdapter` for full event delivery; without it, non-delta lifecycle events drop.
- `DispatchAsync` is the correct wake mechanism for sub-agent completions — serialized via session queue. Never branch on `IsRunning`.
- CronTrigger creates new ephemeral sessions; the only mechanism for waking existing sessions is DispatchAsync.
- CLI System.CommandLine: `Command` objects cannot be added to two parents — call `.Build()` twice.
- When `UseShellExecute = true` (detached Windows processes), environment variables cannot be set — use CLI args instead.
- SignalR GatewayHub activates lazily; missing `IConversationDispatcher` hides until runtime even if extension load succeeds.
- Conversation cleanup is archive/close semantics, not hard-delete. Archived conversations reopen on next inbound binding activity.
- Conversation history pagination must anchor from newest entries (offset from tail), not oldest.
- Extension-loading hosts need dispatcher fallback for lazy hub activation.
- `GetOrCreateDefaultAsync` retired — use ListAsync for discovery, SaveAsync for state transitions.
- Phase 2 dispatch layer: `IConversationDispatcher` owns inbound resolution/session binding; Hub/Host become pure transport relays.
- Old cron sidebar IDs (`cron-session:{sessionId}`) can outlive conversation links; cleanup should still return 204, sealing any existing session while leaving history intact.
- `GET /api/conversations/{id}/history` must treat `cron-session:{sessionId}` as a virtual projection: resolve directly from session history and return empty 200 when the session is already gone so portal startup cannot fail on stale cron rows.
- During mainline merges, keep virtual cron helpers (`GetVirtualCronHistoryAsync`, tail-based paging) when reconciling `ConversationsController` to avoid reintroducing stale-cron 404 regressions.
- 2026-05-12: Locations API now treats database location values as secrets: all responses return '[connection string configured]' with HasConfiguredSecret=true, while create/update still persist request connection strings and blank database updates preserve existing secrets.
- 2026-05-12: CLI `locations list` now routes display values through `ResolveSafeDisplayPath` so database locations always render `(redacted)` and never print connection-string fragments, while filesystem/API locations continue showing configured values.
- 2026-05-14: Gateway API must never reference `BotNexus.Extensions.*` assemblies directly; agent-change broadcasts now flow through `IAgentChangeNotifier` (`src\gateway\BotNexus.Gateway.Contracts\Agents\IAgentChangeNotifier.cs`) with SignalR implementation in `src\extensions\BotNexus.Extensions.Channels.SignalR\SignalRAgentChangeNotifier.cs`.
- 2026-05-14: Boundary regression protection lives in `tests\gateway\BotNexus.Gateway.Tests\Architecture\GatewayProjectDependencyBoundaryTests.cs`, which scans `src\gateway\**\*.csproj` and fails on extension project/package/library references.
- 2026-05-14: Gateway boundary refactor APPROVED by Leela (commits 1a8a8863, 8f7a4a21) — dependency cleanly severed, contract is transport-neutral, no over-abstraction, guard test adequate.

- 2026-05-15: Effective Config API — Implemented GET /api/config (effective state) and GET /api/config/raw (raw state) in ConfigController; updated GatewayStartupAndConfigurationTests. Commit 41cc4e8c — API now returns defaults (e.g., cron.enabled=true, tickIntervalSeconds=60) alongside user config in effective view.
