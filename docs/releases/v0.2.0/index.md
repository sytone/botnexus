---
title: "Release v0.2.0"
description: "Release notes for BotNexus v0.2.0"
date: "2026-06-06"
---

# Release v0.2.0

> **Released:** 2026-06-06
>
> **Full diff:** [v0.1.15...v0.2.0](https://github.com/sytone/botnexus/compare/v0.1.15...v0.2.0)

## [0.2.0] - 2026-06-06

### ✨ Features

- **gateway:** Introduce IInboundMessageOrchestrator + IInboundMessageProcessor seam (#696) (#701)
- **skills:** Add Skills Explorer portal section with gateway API (#712)
- **skills:** Add SkillManagerTool with create/edit/patch/delete/write_file/remove_file actions (#706) (#724)
- **#743:** Add MessageRole.Notification, TurnInterrupted event, and InterruptedTurnNotificationService (#748)
- **ui:** Remove mic button - audio recording not yet supported (#741)
- **#776:** Include skills world-default in generated config.json (#779)
- **#777:** Add `doctor config` sub-command for guided config migration (#781)
- **#745:** Add webhook domain models, store interfaces, secret helpers, and primitives (#782)
- **#632:** Add create_agent and update_agent as core gateway tools (#775)
- **#745:** Add SqliteWebhookRegistrationStore and SqliteWebhookRunStore (#784)
- **#407:** Add full agent editor panel with sidebar sub-tree and all config fields (#786)
- **#745/#746:** Add WebhooksController, DI wiring, and registration CRUD (#787)
- **#746:** Add inbound webhook endpoint with HMAC auth, async/sync/callback dispatch (#792)
- **gateway:** Add archive action to ConversationTool (#820)
- **gateway:** Fire SignalR push on ConversationTool metadata mutations (#821)
- **cli:** Scaffold heartbeat config by default in init and agent add (#835)
- **agents:** Add optional Order field to AgentDescriptor for consistent agent list sort (#844)
- **gateway:** Heartbeat enabled by default and config normalisation service (#834)
- **portal:** Protect core agent files from deletion in workspace tab (#843)
- **portal:** Render conversation list items as anchor elements for browser-native open in new tab (#858)
- **gateway:** Prepend current datetime to LLM messages when dateTimeInjection is enabled (#884)
- **channels:** Strip embedded thinking/reasoning XML tags from outbound text for channels without thinking display (#885)
- **gateway:** Add InterruptAndSteerAsync contract to IAgentHandle (Phase 1a) (#888)
- **sessions:** Add sub_agent_sessions table to sessions.db schema (#889)
- **skills:** Seed example-skill when global skills directory is first created (#892)
- **gateway:** Auto-archive inactive conversations after configurable retention period (#883)
- **copilot:** Add CopilotMessagesProvider for Phase 1a carve-out (#810) (#886)
- **gateway:** Implement InterruptAndSteerAsync in InProcessAgentHandle (#893)
- **sessions:** Write sub-agent session rows to sessions.db on spawn and completion (#894)
- **gateway:** Wire InterruptAndSteer hub method on GatewayHub (#895)
- **sessions:** Expose sub-agent session history via GET /api/sessions/{id}/subagents/history (#898)
- **gateway:** Expose CacheRetentionMode in AgentDescriptor and wire into InProcessIsolationStrategy (#900)
- **sessions:** Capture LastRenderedSystemPrompt on GatewaySession at dispatch time (#901)
- **portal:** Add Interrupt+Redirect button and InterruptAndSteer hub call (#902)
- **tools:** Add DataStoreTool domain model and DataStoreToolContributor registration (#903)
- **platform:** Introduce BotNexus.Yaml targeted frontmatter parser and migrate SkillParser (#904)
- **copilot:** Route Copilot Claude models via CopilotMessagesProvider (Phase 1b, #810) (#938)
- **gateway:** Auto-generate conversation title from first user+assistant exchange (#906)
- **skills:** Add /skills create and /skills delete slash commands (#907)
- **portal:** Surface cache token counts on assistant messages in chat panel (#910)
- **copilot:** Add CopilotResponsesProvider + CopilotCompletionsProvider (Phase 2a, #810) (#944)
- **copilot:** Route Copilot OpenAI-flavour models via carved-out providers (Phase 2b, #810) (#947)
- **sessions:** Add GET /api/sessions/{sessionId}/debug endpoint (#911)
- **gateway:** Auto-replay interrupted user turns on gateway restart with max-attempt guard (#918)
- **gateway:** Inject AGENTS.md files from repo tree hierarchy into system prompt (#919)
- **mcp:** Add auth provider reference to McpServerConfig for HTTP/SSE token injection (#917)
- **portal:** Add SessionDebugPanel with Overview and Metadata tabs (#920)
- **portal:** Add Debug Mode toggle to portal settings and debug icon in main banner (#921)
- **tools:** Implement SQLite storage backend with schema inference and size limit (#926)
- **mobile:** Add refresh button and auto-reconnect on app resume (#930)
- **portal:** Add System Prompt tab to SessionDebugPanel with copy-to-clipboard (#927)
- **gateway:** Auxiliary compression model and iterative summary for compaction Phase 2 (#946)
- **copilot:** Add provider copilot CLI surface (Phase 4, #810) (#962)

### 🐛 Bug Fixes

- **mobile:** Use relative NavigateTo paths to stay within /mobile/ base (#698)
- **ui:** Remove tool processing status bar from chat panel (#720)
- **canvas:** Remove duplicate /api/ prefix in GetConversationCanvasAsync URL (#733)
- **cli:** Suppress banner when stdout is redirected (#685) (#734)
- **cli:** Scaffold agent workspace on agent add and wizard (#331) (#735)
- **cli:** Agent/agents alias, --display-name/--description/--emoji/--disabled flags, help text clarity (#599) (#737)
- **e2e:** Audit and align all E2E tests to current UI design (#751)
- **#633:** Block direct config.json writes from agent file tools (#774)
- **#383:** Fetch canvas for auto-selected conversation on initial load (#778)
- **#793:** Mobile chat auto-scroll not working (#795)
- **portal:** Remove broken Restart Gateway button (#816)
- **portal:** Suppress NO_REPLY cron turns from conversation history (#817)
- **cron:** Reject out-of-range NextRunAt and CreatedAt timestamps with 400 response (#818)
- **signalr:** Classify pre-handshake WebSocket close as benign in GatewayHub (#819)
- **gateway:** Isolate session transcript save from channel send delivery (#826)
- **portal:** Route TurnEnd event through SignalR to clear streaming state on tool-only turns (#827)
- **portal:** Wrap async agent-switch assertion in WaitForAssertion (#829)
- **cli:** Distinguish auth failure from unreachable in gateway status (#830)
- **gateway:** Replace blocking DispatchAsync with Post in ConversationTool (#832)
- **gateway:** Deduplicate user turns in cross-world relay on cancel-and-retry (#833)
- **cron:** Defer user entry save to after PromptAsync to prevent duplicate message (#836)
- **agents:** Replace Task.Delay timing heuristics with deterministic probes in ToolExecutorTimeoutTests (#837)
- **gateway:** Fall back to ActiveSessionId in GetHistory when session lacks conversation_id (#839)
- **tests:** Make Post_WhenQueueFull_ReturnsFalse deterministic with processorStarted gate (#871)
- **portal:** Clear stale streaming state and reload history on SelectConversation (#840)
- **portal:** Re-apply tab deep-link after agent data loads via store change (#841)
- **gateway:** Emit system stall entry when stream completes with thinking only and no visible text (#857)
- **provider:** Reject out-of-range Copilot OAuth exp claims to prevent bypass or crash (#861)
- **cron:** Fire SignalR notify when CronTrigger reactivates an archived pinned conversation (#866)
- **gateway:** Seal session when CloseAfterResponse forces archive in CrossWorldFederationController (#868)
- **gateway:** Write workspace files as BOM-free UTF-8 to prevent YAML parse failures (#874)
- **skills:** Emit LogWarning when SkillDiscovery skips a skill due to invalid frontmatter (#873)
- **portal:** Surface client-side errors with collapsible detail and gateway reporting (#842)
- **tools:** Block LD_*, DYLD_*, and PATH env var overrides in ExecTool agent input (#862)
- **cron:** Guard ActiveSessionId so cron sessions never evict a human session (#867) (#872)
- **skills:** Support YAML block scalars in SkillParser frontmatter (#880)
- **sessions:** Add continuity regression tests for session compactor (audit #665) (#881)
- **scripts:** Strip history block from diff check to prevent no-op timestamp patches
- **gateway:** Bump conversation UpdatedAt on every completed message turn (#891)
- **gateway:** Detect suspicious startup config and recover from most recent valid backup (#896)
- **mobile:** Bypass service worker cache for top-level navigate requests (#897)
- **gateway:** Send stall notice when agent returns thinking-only response (#899)
- **gateway:** Inject CONTEXT COMPACTION guardrail prefix before compaction summary and update structured template to 5-section spec (#908)
- **gateway:** Wire /compact slash command in BuiltInCommandContributor (#912)
- **gateway:** Add RuntimePinnedTools to DefaultToolPolicyProvider that bypass all deny-list checks (#909)
- **gateway:** Debounce agent config file watcher and filter to definition files only (#943)
- **gateway:** Filter NO_REPLY assistant entries from conversation history API (#913)
- **security:** Block SSRF in WebFetchTool by rejecting private and IMDS addresses (#915)
- **portal:** Remove underline from conversation list anchor links (#948)
- **portal:** Pass active ConversationId to CanvasPanel so canvas renders conversation-scoped HTML (#953)
- **skills:** Change AllowSkillCreation and AllowSkillDeletion defaults to true (#949)
- **mobile:** Preserve active conversation on refresh, render Unicode icons, add error boundary (#958)
- **portal:** Style token usage stats below message bubble with human-readable labels (#954)
- **tests:** Replace unregistered noop tool in TOOL_CALL_SEQUENCE with get_datetime (#932)
- **gateway:** Forward update channel to CLI spawned process (#928)
- **tests:** Add assistant to E2E AgentIds to match botnexus init scaffold (#931)
- **sessions:** Add retry on transient SQLite errors and 503 for session store unavailable (#942)

### 📖 Documentation

- **architecture:** Document channel-binding conventions (#729) (#730)
- **readme:** Replace phantom scripts with real CLI install flow (#738)
- **agents:** Require test-impacted.ps1 before every push, clarify pre-commit hook gap

### 🔨 Refactor

- **signalr:** Wire GatewayHub onto IInboundMessageOrchestrator and drop obsolete JoinSession/LeaveSession (#714) (#715)
- **signalr:** Slim GatewayHub.ResolveOrCreateSessionAsync to pure resolution (#721) (#726)

### 🧪 Testing

- **e2e:** Mobile chat coverage - scroll and error bar regression tests (#722 #723) (#727)
- **e2e:** Full portal coverage expansion - 18 new test files, 86 tests (#796)
- **agents:** Add Copilot wire replay harness + path-leak fence (Phase 0b, #810) (#811)
- **agents:** Add Copilot request-body snapshot harness (Phase 0a, #810) (#812)
- **agents:** Pin direct-Anthropic request snapshots (Phase 0c, #810) (#860)
- **agents:** Pin gateway-level Copilot routing (Phase 0d, #810) (#863)
- **gateway:** Pin pre-carve-out config.json compatibility (Phase 0e, #810) (#875)
- **gateway:** Pin pre-carve-out auth.json compatibility (Phase 0f, #810) (#878)

### ⚙️ Miscellaneous

- **scripts:** Add ci-pr-comment and maintenance-pr-comment templating scripts
- **copilot:** Drop dead helpers and assembly-name suffixes (Phase 3, #810) (#950)
- **gateway:** Remove FileAgentConfigurationSource and FileAgentConfigurationWriter (#956)

### 🔧 CI/Build

- Exclude live-gateway and Playwright tests from full-tests run (#929)

[0.2.0]: https://github.com/sytone/botnexus/compare/v0.1.15...v0.2.0

