# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.10] - 2026-05-13

### ✨ Features

- **gateway:** Inject memory prompt guidance from config (#190)
- **webui:** Manage locations from configuration UI (#205)
- **blazor-client:** Support URL-routed portal state (#209)
- **cli:** Show applied commit subjects on update (#213)

### 🐛 Bug Fixes

- **gateway:** Harden SignalR extension dispatcher activation (#186)
- Preserve latest conversation history on refresh (#187)
- Hydrate latest Quill history after refresh (#189)
- Route steering to active session and conversation (#191)
- **cron:** Surface scheduled runs and model overrides (#193)
- **cron:** Stable per-job conversation — all runs of a job share one conversation (#195)
- Enable conversation cleanup and sidebar scrolling (#197)
- Archive old cron conversation cleanup ids (#200)
- Keep sub-agent output in originating conversation (#201)
- Archive old cron conversation cleanup ids (#202)
- **blazor:** Guard chat enter interop after refresh (#203)
- Stabilize cron conversations and sidebar scrolling (#204)
- Enforce warnings as errors (#206)
- **cli:** Deploy freshest extension build outputs (#207)
- **conversations:** Keep deleted cron conversations hidden (#212)

### 📖 Documentation

- Enforce worktree policy for all code changes (#210)

### 🔨 Refactor

- **tests:** Mirror source project structure (#188)
- **domain:** Remove GetOrCreateDefaultAsync — replace with explicit named conversations (#196)
- **config:** Unify runtime config options (#208)

### ⚙️ Miscellaneous

- **squad:** Optimize team context files (#198)

## [0.1.9] - 2026-05-08

### ✨ Features

- **gateway:** Align memory authoring with OpenClaw model (#179)

### 🐛 Bug Fixes

- **webtool:** Invalidate cached Copilot MCP client on 400/401 and retry once (#161)
- **webtool:** Add structured logging to WebSearchTool and CopilotMcpSearchProvider (#162)
- **gateway:** Steer returns session ID; router reuses Expired sessions instead of creating new (#164)
- **portal:** Toggle CSS, archive conversation, steering feedback (#166)
- **portal:** Archived conversations reappear; sidebar not scrollable (#167)
- **portal:** Duplicate messages on default conversation; history not loading (#168)
- **crossworld:** Rename CrossWorldRelayRequest.ChannelAddress to ConversationId (#175)
- **cli:** Improve update git pull cancellation handling (#181)

### 📖 Documentation

- **architecture:** Gateway flow diagrams; refactor(domain): BindingId strong type (#170)
- **planning:** Preserve OpenClaw memory alignment planning branch (#182)
- **.squad:** Merge conversation project refactor session (#183)

### 🔨 Refactor

- **gateway:** Conversation-first routing — ConversationId on InboundMessage (#169)
- **domain:** ChannelAddress and ThreadId strong types; fix StaleChannelConnectionException (#174)
- **gateway:** Extract conversation stores into gateway conversations project (#178)
- **gateway:** Route conversations through dispatcher (#180)

### ⚙️ Miscellaneous

- Remove personal identifiers from tests and docs (#159)
- **squad:** Preserve tool timeout session state (#184)

## [0.1.8] - 2026-05-05

### 🐛 Bug Fixes

- **cli:** Correct update step order — pull → stop → build → deploy → start (#157)

### 🔧 CI/Build

- **release:** Add dynamic run-name with version to publish workflow (#156)
- Remove unsupported updateWorkflowRun call from Release workflow (#158)

## [0.1.7] - 2026-05-05

### ✨ Features

- **portal:** Multi-conversation support — route SendMessageToConversation via threadId (#155)

### 🔧 CI/Build

- Optimize workflow triggers to reduce unnecessary Actions runs (#154)

## [0.1.6] - 2026-05-05

### 🐛 Bug Fixes

- **signalr:** Use agentId as stable ChannelAddress, archive stale connection-id conversations (#153)

## [0.1.5] - 2026-05-05

### 🐛 Bug Fixes

- **cli:** Stop gateway before build to release file locks (#151)

## [0.1.4] - 2026-05-05

### ✨ Features

- **cli:** Rich visual feedback using Spectre.Console (#150)

## [0.1.3] - 2026-05-05

### 🐛 Bug Fixes

- **config:** Support toolIds ["*"] as all-tools wildcard (#141)
- **tests:** Make CancellationDuringStreaming deterministic (#142)
- **sessions:** Migrate orphaned sessions to default conversation on startup (#143)
- **portal:** Filter NO_REPLY sentinel from conversation UI (#144)
- **gateway:** Demote stale SignalR bindings on disconnect and fan-out failure (#145)

### 🔨 Refactor

- **gateway:** Decouple extension tool wiring via IAgentToolContributor (#146)
- **gateway:** Binding-first routing — remove default conversation fallback, add ReattachBindingAsync (#148)

## [0.1.2] - 2026-05-04

### ✨ Features

- **config:** Backup config.json before every write (#116)

### 🐛 Bug Fixes

- **config:** Remove mandatory type field validation for channel entries (#117)
- **config:** Populate JsonElement fields from raw JSON in PostConfigure (#122)
- **gateway:** Stamp BindingId after routing to prevent duplicate Telegram messages (#124)
- **gateway:** Carry OriginatingBinding through processing — fixes ThreadId on direct sends and streaming (#129)
- **portal:** Session reset preserves conversation history (#132)
- **telegram:** Harden inbound message security — allowedUserIds, reject channel posts, disable edited messages by default (#134)
- **sessions:** ResolveByBindingAsync null threadId must only match null-thread bindings (#136)
- **gateway:** Per-address conversation routing (#139)

### 📖 Documentation

- **channels:** Add Telegram configuration and security guide (#135)

### 🔨 Refactor

- **config:** Migrate from PlatformConfigLoader to IConfiguration (#119)
- **config:** Complete IOptionsMonitor migration — remove PlatformConfig singleton (#121)

## [0.1.1] - 2026-05-03

### ✨ Features

- **telegram:** Add botnexus-extension.json manifest so CLI deploys Telegram channel (#108)
- Replace manual changelog with git-cliff + migrate docs from MkDocs to VitePress (#107)
- **telegram:** Bind each Telegram bot to a configured agent (#109)
- **bootstrap:** Meaningful scaffold templates with first-run ritual (#113)

### 🐛 Bug Fixes

- **gateway:** Conversation-scoped session persistence, ThreadId routing, binding-aware fan-out (#106)
- **actions:** Make publish-cli workflow runner-compatible and branch-agnostic
- **ci:** Fix YAML syntax error in publish-cli.yml (#111)

### 📖 Documentation

- Sync user docs with current architecture (#110)

### ⚙️ Miscellaneous

- **workflow:** Select release type and auto-increment version

## [0.1.0] - 2026-05-02

### .squad

- Merge architecture review findings
- Orchestration log, session log, merge inbox decision
- Leela gateway crash fix (2026-04-03T06:55:00Z)

### ✨ Features

- Implement complete BotNexus C# solution
- Add WebSocket endpoint to BotNexus.Gateway
- Add activity stream, REST API endpoints, web UI project, and enhanced WebSocket monitor mode
- Add workflows for Squad project management
- Initialize BotNexus project with agent charters and history
- Update config.json with default model and agent overrides
- **core:** Add IOAuthProvider and IOAuthTokenStore abstractions
- **core:** Add IExtensionRegistrar and BotNexusExtension attribute
- **config:** Refactor configuration models for dynamic extensions
- **gateway:** Implement multi-agent message routing
- **providers:** Integrate ProviderRegistry into DI and agent resolution
- **loader:** Add dynamic extension assembly loader with tests
- **build:** Add MSBuild targets for extension output pipeline
- **tools:** Load tool implementations through extension system
- **providers:** Load LLM providers through dynamic extension system
- **channels:** Load channel implementations through extension system
- **copilot:** Add GitHub Copilot provider with OAuth device code flow
- **auth:** Add API key authentication to Gateway endpoints
- **security:** Add assembly validation and security hardening for extensions
- **observability:** Add health checks, metrics, and structured logging
- **slack:** Add webhook endpoint for Slack Events API
- **webui:** Show loaded extensions in system panel
- **config:** Consolidate configuration to ~/.botnexus/ home directory
- **core:** Add IContextBuilder and IAgentWorkspace interfaces
- **config:** Add agent workspace properties and BotNexusHome agents path
- **agent:** Implement AgentWorkspace for per-agent file management
- **core:** Add IMemoryConsolidator interface
- **tools:** Add memory_search, memory_save, and memory_get agent tools
- **agent:** Implement AgentContextBuilder for workspace-driven context assembly
- **agent:** Register memory tools when EnableMemory is true
- **di:** Register IAgentWorkspace and IContextBuilder in service collection
- **memory:** Implement LLM-based memory consolidation
- **heartbeat:** Integrate memory consolidation trigger
- **core:** Add ICronJob and cron type abstractions
- **config:** Add centralized CronConfig model
- **agent:** Add IAgentRunnerFactory for programmatic runner creation
- **core:** Add ISystemActionRegistry for pluggable system actions
- **cron:** Implement centralized CronService with tick loop and job execution
- **cron:** Implement AgentCronJob with runner factory and channel routing
- **cron:** Implement SystemCronJob with built-in system actions
- **cron:** Implement MaintenanceCronJob for memory consolidation and cleanup
- **cron:** Add CronJobFactory for config-driven job registration
- **cron:** Update CronTool to use new ICronService API
- **cron:** Wire centralized cron service into Gateway DI
- **cron:** Add legacy AgentConfig.CronJobs migration
- **api:** Add REST endpoints for cron job management
- **observability:** Add cron metrics, health check, and activity events
- **logging:** Add file logging to ~/.botnexus/logs/
- **core:** Add IHealthCheckup diagnostic interface
- **diagnostics:** Create BotNexus.Diagnostics project with CheckupRunner
- **cli:** Create BotNexus.Cli dotnet tool with System.CommandLine
- **cli:** Add ConfigFileManager, GatewayClient, and ConsoleOutput utilities
- **diagnostics:** Add configuration checkups
- **diagnostics:** Add security checkups
- **diagnostics:** Add connectivity checkups
- **diagnostics:** Add extensions, permissions, and resources checkups
- **diagnostics:** Add auto-fix capability to doctor checkups
- **api:** Add status, doctor, and shutdown Gateway endpoints
- Enhance configuration management and agent routing
- **cli:** Add backup create/restore/list commands
- **cli:** Add backup command + foolproof BOTNEXUS_HOME test isolation
- **scripts:** Add pack, install, update packaging scripts
- **cli:** Add installable tool bootstrap and native install/update commands
- **cli:** Use %LOCALAPPDATA%\BotNexus as default install path
- **cli:** Track package source in version.json and status output
- **cli:** Add Spectre.Console for rich terminal UI
- **webui:** Add agent selector dropdown to chat
- Add extension version info to API, CLI, and install manifest
- **cli:** Add 'webui' command with dev and open subcommands
- **webui:** Show agent name and channel in session details
- Add ICommandRouter to AgentRunnerFactory and register CommandRouter in service extensions
- Implement command palette for chat input with command suggestions
- Create dev-loop script to streamline build and installation process
- Auto-register internal tools for agent sessions
- **webui:** Add model selector for new and existing sessions
- Make temperature, max tokens, and context window tokens nullable
- **webui:** Redesign tool call display with summary/detail pattern
- Add agent continuation prompting when intent detected without tool calls
- **gateway:** Add REST API endpoints for session hiding and agent CRUD
- **audit:** Add comprehensive logging for config and token operations
- **webui:** Improve agent editor form UX
- Add model listing support to providers and API
- Implement skills system with global and per-agent skill loading
- **webui:** Populate model dropdowns from /api/models
- **webui:** Add system message handling with device auth support
- **auth:** Add auto-reauth and surgical config updates
- **webui:** Add thinking indicator to chat UI
- Broadcast Copilot device auth to WebSocket clients
- Add system message infrastructure
- Add startup auth validation for providers
- **agent:** Add real-time streaming and tool progress events
- **webui:** Add real-time tool progress streaming via WebSocket
- **agent:** Stream tool progress and processing indicators to WebSocket clients
- **providers:** Implement provider response normalization layer
- Add repeated tool call detection with configurable limits
- Add streaming chat chunks with tool call support
- Add model registry for provider architecture
- Add API format handlers for provider architecture
- Complete provider architecture port to BotNexus
- **providers:** Add detailed logging for Anthropic Messages API requests
- **providers:** Add BotNexus.Providers.Core with unified LLM abstractions
- **providers:** Add OpenAI Chat Completions provider
- **providers:** Add Anthropic Messages API provider
- **providers:** Add GitHub Copilot provider with OAuth support
- **providers:** Add OpenAI-compatible provider for local inference
- **providers:** Add OpenAI-compatible provider for local inference
- **quickstart:** Enhance example with tool definition and context handling
- **agent-core:** Scaffold BotNexus.AgentCore project and test project
- **agent-core:** Add core enums (ThinkingLevel, ToolExecutionMode, AgentEventType, AgentStatus)
- **agent-core:** Add AgentMessage hierarchy and AgentToolResult types
- **agent-core:** Add AgentEvent hierarchy (10 event types)
- **agent-core:** Add AgentState, AgentContext, and configuration types
- **agent-core:** Add IAgentTool interface, hook types, and delegates
- **agent-core:** Add MessageConverter for AgentMessage ↔ Message conversion
- **agent-core:** Add ContextConverter for AgentContext → Context bridge
- **agent-core:** Add StreamAccumulator for streaming response processing
- **agent-core:** Add ToolExecutor with sequential and parallel modes
- **agent-core:** Add AgentLoopRunner — core agent loop engine
- **agent-core:** Add PendingMessageQueue for steering and follow-up messages
- **agent-core:** Add Agent class — stateful wrapper with full public API
- **coding-agent:** Scaffold BotNexus.CodingAgent project
- **coding-agent:** Add PathUtils for path resolution and safety
- **coding-agent:** Add ReadTool
- **coding-agent:** Add WriteTool
- **coding-agent:** Add EditTool
- **coding-agent:** Add ShellTool
- **coding-agent:** Add GlobTool
- **coding-agent:** Add CodingAgentConfig for configuration management
- **coding-agent:** Add SystemPromptBuilder for coding-optimized prompts
- **coding-agent:** Add GitUtils and PackageManagerDetector
- **coding-agent:** Add SafetyHooks for pre-tool-call validation
- **coding-agent:** Add AuditHooks for post-tool-call logging
- **coding-agent:** Add SessionManager for session lifecycle
- **coding-agent:** Add CodingAgent factory
- **coding-agent:** Add OutputFormatter for rich terminal output
- **coding-agent:** Add InteractiveLoop — prompt/response REPL
- **coding-agent:** Add ExtensionLoader for assembly-based tool plugins
- **coding-agent:** Add SkillsLoader for AGENTS.md context files
- **coding-agent:** Add CommandParser and wire Program.cs entry point
- **coding-agent:** Add GrepTool for file content search
- **coding-agent:** Add SessionCompactor for context management
- **coding-agent:** Add /login /logout with Copilot OAuth and auth.json persistence
- **providers-core:** Add built-in GitHub Copilot model catalog
- Add token-aware session compaction with LLM summarization
- Expand extension lifecycle with event hooks
- Upgrade system prompt builder to dynamic composition
- Port session tree model with JSONL entries and branching
- Implement OpenAI Responses provider with streaming support
- **providers:** Add context overflow detection utility
- **anthropic:** Port Claude Code OAuth stealth mode
- **openai:** Port auto-detect compat for non-OpenAI providers
- **agent-core:** Add default message converter
- **providers:** Add model registry identity utilities
- **coding-agent:** Add directory listing and context discovery
- **coding-agent:** Add list directory tool
- **coding-agent:** Add thinking level CLI option
- **coding-agent:** Record session model/thinking metadata
- **coding-agent:** Add thinking slash command
- **agent:** Add HasQueuedMessages property
- **agent:** Add runtime queue mode setters
- **providers:** Add direct Anthropic and OpenAI model registrations
- **agent:** Add MaxRetryDelayMs configuration for retry backoff cap
- **coding-agent:** Auto-persist session on assistant message completion
- **providers:** Add tool call argument validation against JSON Schema
- **providers:** Add shortHash utility for tool call ID normalization
- **coding-agent:** List directory entries up to 2 levels deep
- **coding-agent:** Discover context files via ancestor directory walk
- **coding-agent:** Fix enterprise Copilot endpoint + add CLI test matrix
- **coding-agent:** Add model/provider to session header, preserve test sessions
- **coding-agent:** Add --log option for console output mirroring
- **gateway:** Add Gateway Service architecture and project structure
- **gateway:** Add default communicator and auth handler
- **channels:** Add Telegram channel adapter stub
- **channels:** Add TUI channel adapter stub
- **gateway:** Add thinking delta stream events
- **webui:** Phase 2 enrichment — thinking, tools, sessions, agents, activity
- **gateway:** Add agent configuration source contracts
- **gateway:** Add agent descriptor validator
- **gateway:** Add file-based agent configuration loading
- **gateway:** Implement local cross-agent calling
- **webui:** Add error states, loading indicators, and reconnection support
- **gateway:** Add steering and follow-up queuing support
- **gateway:** Add sandbox, container, and remote isolation strategy stubs
- **gateway:** Add platform configuration system (.botnexus/config.json)
- **gateway:** Add config validation endpoint
- **gateway:** Add multi-tenant API key support
- **webui:** Enhance thinking/tool display and steering UX
- **gateway:** Wire provider registration, auth manager, and platform config agent source
- **gateway-abstractions:** Add channel capability flags to IChannelAdapter
- **gateway:** Add session lifecycle with status and cleanup service
- **gateway:** Add agents directory to BotNexus home
- **gateway:** Add config.json file watcher with hot reload
- **webui:** Add thinking toggle, tool inspector, session reconnection, agent selector, activity feed, and steering controls
- **gateway-api:** Add auth middleware to ASP.NET pipeline
- **gateway-api:** Add OpenAPI/Swagger specification
- **gateway:** Enforce MaxConcurrentSessions in agent supervisor
- **gateway:** Validate isolation strategy exists before creating agent
- **gateway-api:** Add session locking for WebSocket connections
- **gateway:** Implement agent workspace manager and context builder
- **cli:** Create BotNexus CLI with config validation commands
- **channels:** Add WebSocket channel adapter integrating with channel pipeline
- **gateway-api:** Add /ws/activity endpoint for activity stream subscriptions
- **channels-tui:** Add console input loop for TUI channel
- **gateway:** Implement cross-agent calling
- **webui:** Enhance session management, UX, and production quality
- **gateway-api:** Add paginated session history endpoint
- **gateway:** Enforce max call chain depth
- **gateway:** Add cross-agent timeout protection
- **gateway:** Support configurable session store selection
- **gateway:** Add websocket reconnect sequencing
- **gateway:** Add session suspend and resume endpoints
- **gateway:** Add session queueing and tui steering
- **webui:** Add processing status bar and tool error display
- **gateway:** Add agent descriptor update endpoint
- **gateway:** Add IExtensionLoader interface and extension models
- **gateway:** Implement AssemblyLoadContext-based extension loader
- **gateway:** Integrate extension loading into Gateway startup
- **gateway:** Add config schema validation and path resolver
- **telegram:** Add Telegram Bot API HTTP client
- **telegram:** Implement long polling and message routing
- **telegram:** Implement send with markdown and streaming support
- **gateway:** Add GET /api/channels endpoint
- **gateway:** Add GET /api/extensions endpoint
- **gateway:** Add session metadata GET/PATCH endpoints
- **webui:** Add channels panel with capability display
- **gateway:** Add config version field for schema evolution
- **webui:** Add extensions panel with loaded extension info
- **gateway:** Add per-client rate limiting middleware
- **gateway:** Add correlation ID middleware
- **gateway:** Add agent health check endpoint
- **gateway:** Add SQLite session store implementation
- **gateway:** Add agent lifecycle events to activity stream
- **gateway-api:** Add Serilog and OpenTelemetry foundation
- **providers:** Add OTel activity spans for provider streaming
- **observability:** Add gateway and agent tracing spans
- **observability:** Add channel and session tracing spans
- **gateway:** Persist API-managed agent configs
- **gateway:** Enrich platform agent/provider config schema
- **gateway:** Support prompt file arrays and workspace templates
- **gateway:** Add layered model filtering
- **gateway:** Port BotNexus system prompt builder
- **gateway:** Regenerate system prompt on session reset
- **memory:** Add core memory store and lifecycle indexing
- **memory:** Wire agent memory config and tool integration
- **webui:** Add agent debug info panel
- **gateway:** WebUI client versioning + server-side client log endpoint
- **gateway:** Auto-version from build timestamp — no manual updates
- **webui:** Replace agent debug modal with full agent page panel
- **webui:** Complete agent config fields + add cron canvas view
- **cron:** Add per-job timezone support
- **webui:** Add timeline view with session dividers
- **gateway:** Add SessionTool for agent session management
- **webui:** Add Stop Gateway button to sidebar footer
- **skills:** Add BotNexus.Skills library with TDD tests
- **skills:** Add SkillTool and wire into gateway context builder
- **webui:** Show notification when agent loads a skill
- **gateway:** Add extension hook system with BeforePromptBuild and tool call hooks
- **gateway:** Add extension hook system with BeforePromptBuild and tool call hooks
- **tools:** Add process tool extension for background process management
- **gateway:** Wire BeforeToolCall/AfterToolCall hooks into agent execution
- **gateway:** Add dangerous tool registry and tool policy system
- **skills:** Add security scanner for skill scripts (ported from OpenClaw)
- **scripts:** Add deploy-extensions.ps1 for runtime deployment
- **mcp:** Add MCP extension with stdio transport and tool bridging
- **mcp:** Add HTTP/SSE transport for remote MCP servers
- **mcp:** Add security integration for MCP tools
- Ran squad nap to cleanup
- **mcp-invoke:** Add invoke_mcp tool extension for skill-driven MCP access
- **web:** Add WebTools extension with CopilotMcp search provider
- Enhance JoinSession with resume detection and session metadata
- Auto-reactivate expired sessions on new message
- Archive sessions on reset instead of deleting
- Add session compaction model, interface, and store support
- Implement LLM-powered session compactor
- Wire session compaction into gateway host and DI
- **gateway:** Add sub-agent spawning abstractions and configuration
- **gateway:** Implement DefaultSubAgentManager with background execution
- **gateway:** Add sub-agent spawn/list/manage tools
- **gateway:** Wire sub-agent DI registration and tool resolution
- **api:** Add sub-agent REST endpoints and WebSocket lifecycle events
- **webui:** Add sub-agent status panel with real-time updates
- **squad:** Sub-Agent Spawning feature delivery complete (Wave 1-4)
- Add deliver-spec prompt for full squad delivery cycles
- **webui:** Add per-session state management for streaming
- **gateway:** Add multi-session subscription foundation
- **webui:** Multi-session client model — zero-server-call switching
- **gateway:** Add agent delay/wait tool for in-session pausing
- **gateway:** Add file watcher tool for change-triggered agent loops
- **api:** Add cross-session channel history endpoint for infinite scrollback
- **webui:** Infinite scrollback with IntersectionObserver
- **webui:** Add floating 'New messages' button when scrolled up
- **gateway:** Add IPathValidator and FileAccessPolicy for per-agent file permissions
- **tools:** Integrate IPathValidator into all file tools
- **gateway:** Add glob pattern support to file access permissions
- **docs:** Add comprehensive domain model documentation for BotNexus
- Add BotNexus.Domain project with value objects and smart enums
- Complete wave2 session model and value object adoption
- Add sub-agent archetype identity to replace parent ID reuse
- **sessions:** Implement existence dual-lookup in all session stores
- **domain:** Add WorldIdentity record type
- **gateway:** Improve startup logging with version and component info
- **gateway:** Add world identity configuration and startup logging
- **api:** Add GET /api/world endpoint
- **webui:** Display world identity in header
- **api:** Add structured log viewer endpoint
- **gateway:** Create BotNexus.Gateway.Contracts project
- **domain:** Add WorldDescriptor, Location, and CrossWorldPermission types
- **domain:** Add ConversationRequest and agent-to-agent session types
- **domain:** Add SoulSession types and TriggerType.Soul
- **gateway:** Implement AgentConversationService for peer agent communication
- **gateway:** Populate WorldDescriptor from config and runtime discovery
- **prompts:** Create BotNexus.Prompts with IPromptSection pipeline
- **api:** Expand /api/world endpoint with full WorldDescriptor
- **gateway:** Implement SoulTrigger with daily session lifecycle
- **tools:** Add agent_converse tool
- **gateway:** Add conversation cycle detection
- **domain:** Add cross-world communication types
- **channels:** Implement CrossWorldChannelAdapter
- **gateway:** Add cross-world message relay and authentication
- **api:** Add cross-world federation endpoints
- **sessions:** Create shared session primitives library
- **gateway:** Implement session visibility filtering by SessionType
- **gateway:** Add sealed channel continuation pruning
- **webui:** Extract api.js — shared API client, channel helpers, version check
- **webui:** Extract ui.js — DOM refs, utilities, status, modals, mobile sidebar
- **webui:** Extract session-store.js — SessionStore, StoreManager, state accessors
- **webui:** Extract hub.js — SignalR connection builder, hubInvoke, reconnect
- **webui:** Extract events.js — SignalR event handlers, sub-agent state
- **webui:** Extract sidebar.js — sessions, agents, channels, config, activity, cron
- **webui:** Extract chat.js — chat canvas, messages, commands, sub-agents
- **webui:** Add module entry point, rename original, update index.html
- **webui:** Add hash-based URL routing for agent channels
- **webui:** Update page title to show current agent and channel
- **webui:** Add sidebar collapse toggle with persistent state
- **gateway:** Inject ISessionStore into DefaultAgentSupervisor for history loading
- **gateway:** Populate AgentExecutionContext.History from session store on agent creation
- **gateway:** Inject prior history into agent initial state in InProcessIsolationStrategy
- **webui:** Add collapsed sidebar rail with section icons
- **webui:** Route client debug events to server log endpoint
- Add start-probe.ps1 launch script for BotNexus.Probe
- **api:** Add git commit hash to /api/version endpoint
- **gateway:** Add FileAccessPolicyConfig to agent configuration
- **gateway:** Map file access policy in agent config sources
- **gateway:** Add world-level file access policy with per-agent override
- **gateway:** Add LocationConfig to gateway configuration
- **gateway:** Implement ILocationResolver with config-based resolution
- **gateway:** Merge config locations into WorldDescriptor
- **gateway:** Resolve @location references in file access policies
- **cli:** Add botnexus locations list|add|update|delete commands
- **cli:** Add botnexus doctor locations health check
- **api:** Add locations CRUD and health check endpoints
- **webui:** Add locations management view with CRUD
- **webui:** Add location health check UI
- Sub-agent sidebar visibility and seal endpoint (Wave 1)
- Read-only sub-agent conversation view and seal tests (Wave 2)
- **subagent-ui:** Wave 3 — edge cases, reactive updates, docs
- **webui:** Add gateway uptime display to sidebar footer
- Add research document on session lifecycle fragmentation
- **commands:** Extension-contributed commands — Waves 1-4
- **config:** World-level extension config defaults with agent-level deep merge
- Add MkDocs Material documentation site infrastructure
- Add memory backfill CLI command and refactor MemoryIndexer
- Add site polish and first-agent tutorial
- Add media pipeline domain types and contracts (Wave 1)
- Implement media pipeline core with handler dispatch and telemetry (Wave 2)
- Add SendMessageWithMedia hub method and Whisper transcription extension (Wave 3a)
- Add WebUI audio recording with MediaRecorder API (Wave 3b)
- Add audio playback and transcription progress indicator in WebUI (Wave 4a)
- Wake idle parent agent on sub-agent completion
- Add typed SubAgentCompletionMessage and completion deduplication
- Add heartbeat config models and system prompt wiring (Wave 1)
- Heartbeat cron provisioning, quiet hours, and HEARTBEAT_OK handling (Wave 2)
- Add includeSystem parameter to cron list for debugging heartbeat jobs
- Process-based integration test harness with JSON scenarios
- Dynamic config reload and config CRUD API
- Config API integration test scenarios + REST action support
- Config management API + dynamic reload + integration tests (7/7 pass)
- Test client mirrors WebUI behavior + file logging
- Tool-blocking + stress scenarios with timing assertions
- Parallel track execution model for integration tests
- Parallel MCP server startup + MCP ping integration test
- Context diagnostics API for LLM context inspection
- **gateway:** Add IEndpointContributor and IApiContributor extension interfaces
- **channels:** Extract SignalR into BotNexus.Channels.SignalR channel extension
- **gateway:** Remove hardcoded SignalR hub mapping from Program.cs (Wave 4)
- **channels:** Add Blazor WASM client for SignalR gateway hub
- **blazor:** Add multi-agent concurrent sessions (Phase 3)
- **blazor:** Phase 4 feature parity — markdown, tools, history, steer
- **blazor:** Host Blazor WASM SPA at /blazor/ from gateway
- **blazor:** Add Restart Gateway button to sidebar footer
- **blazor:** Collapsible agent tree sidebar with channels + sub-agents
- **blazor:** Full feature parity — thinking, sub-agents, audio, commands, config, toggles
- **cli:** Add install and build commands
- **cli:** Add serve command with gateway and probe subcommands
- **cli:** Register install, build, and serve commands in Program.cs
- **tools:** Add configurable shell preference for ShellTool
- **blazor:** Add platform configuration page
- **gateway:** Serve Blazor UI at root and auto-init config on first serve
- **cli:** Add reusable wizard framework with Spectre.Console prompts
- **cli:** Add provider command with setup wizard and OAuth flow
- **cli:** Auto-build on serve and skip test projects in build
- **blazor-client:** Restructure layout with banner, sidebar, and agent dropdown
- **blazor-client:** Move agent dropdown under Chat heading in sidebar
- **blazor-client:** Add config section sub-nav in sidebar
- **cli:** Stream MSBuild output with live progress rendering
- **blazor-client:** Add Agents page and CLI wizard for agent management
- **cli:** Add gateway process manager (Wave 1)
- **cli:** Refactor gateway command with start/stop/status/restart subcommands
- **blazor:** Add read-only sub-agent session viewing
- Enhance dynamic configuration reload and improve CLI gateway management
- **cli:** Expose gateway as top-level command in addition to serve gateway
- **config:** World-level agent defaults with field-level inheritance (#12) (#13)
- **conversations:** Conversation Model — Waves 1-3 (domain, routing, REST API, live tests) (#20)
- **cli:** Add --target to all remaining CLI commands (#56)
- **cli:** Make BotNexus.Cli a publishable dotnet global tool (#59)
- **cli:** Add botnexus update command (#65)
- **blazor:** Refactor config page into dedicated panel components (#74)
- **blazor:** Add FeatureFlagsService backed by localStorage (#77)
- **portal:** Wave 1 — IGatewayRestClient + IPortalLoadService + IsReady gate (#81)
- **blazor-client:** Add client state store (#82)
- **blazor:** LocalStorage conversation history cache (v2 — current architecture) (#98)

### 🐛 Bug Fixes

- **bus:** Remove sync-over-async deadlock hazard in MessageBusExtensions
- **cron:** Update last run properties to use LastRunStartedAt and NextOccurrence
- **config:** Use minimal default config for clean first-run
- **loader:** Treat missing extension folders as warnings not failures
- **health:** Distinguish unconfigured from broken in health checks
- Cross-platform test failures and BOTNEXUS_HOME isolation
- **copilot:** Correct API endpoint path from /v1/chat/completions to /chat/completions
- Install script config update + CopilotProvider test endpoint path
- Install-cli script detection and dev docs to use scripts
- Resolve all build warnings across solution
- **cli:** Handle non-interactive stdin for Spectre.Console prompts
- **copilot:** Clear stale token on 401/403 for re-authentication
- **gateway:** Only show 'default' agent when no named agents configured
- Update tests for agent API and encoding improvements
- **cli:** Detach gateway process from CLI console on start
- **webui:** Tighten chat message spacing and remove whitespace bloat
- **copilot:** Add missing token exchange step for API authentication
- Include tool_calls and tool_call_id in message history
- **session:** Persist agent name and derive timestamps from history
- **webui:** Remove excessive whitespace and handle tool calls in live responses
- **webui:** Eliminate empty assistant bubbles and orphaned streaming indicators
- Enable incremental builds in dev-loop
- Clean stale nupkg files and make install.ps1 resilient to unknown packages
- **gateway:** Correct invalid route pattern for session hide/unhide endpoints
- Remove hardcoded Temperature/MaxTokens defaults for nullable config
- Model resolution - ensure settings reflect configured model
- Remove hardcoded gpt-4o defaults to respect agent-configured models
- Resolve nullable model warnings and update tests for streaming
- Consistency audit - API and docs alignment
- **gateway:** Add WebSocket agent query parameter support and enhanced provider logging
- **copilot:** Handle both JSON string and object formats for tool call arguments
- Merge multiple choices from Copilot API response
- Use absolute URIs in all API format handlers
- Resolve nullability warnings in test code
- **anthropic:** Implement complete tool call support
- **agent-core:** Replace BotNexus.Core/Providers.Base refs with Providers.Core
- **providers-core:** Add Details field to ToolResultMessage
- **coding-agent:** Write default config.json on first run and add Copilot OAuth setup guide
- **coding-agent:** Register built-in API providers at startup
- **coding-agent:** Register built-in models at startup and fix model resolution
- **providers:** Disable OpenAI completions store flag
- **providers:** Include Openai-Intent in copilot dynamic headers
- **providers:** Narrow adaptive thinking model detection
- **providers:** Use bearer auth for copilot token exchange
- **coding-agent:** Align edit tool schema with pi-mono
- **coding-agent:** Rename shell tool to bash
- **agent-core:** Handle thinking stream, tool result lifecycle, and runtime reasoning
- **coding-agent:** Replace reflection-based message conversion
- Resolve provider registration for Copilot models
- Align MessageTransformer thinking and tool-result handling with pi-mono
- Align Anthropic and OpenAI provider behavior with pi-mono
- Prevent ExtensionRunner crash when extension throws
- Resolve mutable dictionary leak and add null guards on providers
- **anthropic:** Remove obsolete fix summaries and analysis documents
- **providers:** Correct Anthropic protocol fidelity
- **coding-agent:** Correct tool truncation, fuzzy edit, BOM, and token estimation
- **providers:** Add OpenAI Responses reasoning, caching, and xhigh clamping
- **agent-core:** Correct event emission, hook ordering, and loop guards
- **providers:** Skip empty content blocks in Anthropic message conversion
- **coding-agent:** Add image support and byte limit to ReadTool
- **coding-agent:** Add context lines, case-insensitive search to GrepTool
- **coding-agent:** Improve compaction, skills validation, and tool safety
- **coding-agent:** Add file mutation queue, glob limits, edit no-change detection
- **providers:** Correct tool strict mode and thinking budget defaults
- **anthropic:** Add ExtraHigh model guard for max effort
- **providers:** Add metadata, empty message skip, cache TTL, maxTokens defaults
- **openai-responses:** Use input messages for system prompt
- **agent-core:** Add handleRunFailure with synthetic error message and agent_end event
- **agent-core:** Restore steering-first queue priority in ContinueAsync
- **agent-core:** Case-sensitive tool lookup and QueueMode default
- **coding-agent:** Auto-trigger compaction in non-interactive mode
- **coding-agent:** Validate compaction cut-point preserves tool pairs
- **coding-agent:** Map SystemAgentMessage in convertToLlm delegate
- **coding-agent:** BOM stripping in EditTool and grep default max
- **anthropic:** Use original reasoning level for adaptive effort mapping
- **docs:** Consistency review — align docs with code after port audit sprint
- **agent:** Preserve assistant content blocks and converter parity
- **agent:** Align continue and lifecycle semantics with pi-mono
- **agent:** Emit parallel tool completions inline
- **providers:** Align core thinking and stream utilities
- **providers:** Align anthropic thinking and stop-reason handling
- **providers:** Restore openai compat and signature round-trips
- **providers:** Remove BaseUrl from ModelsAreEqual comparison
- **agent:** Log swallowed listener exceptions on failure/abort paths
- **providers:** Wire StopReason.Refusal and Sensitive to provider mappers
- **agent:** Defer assistant message state add to MessageEndEvent
- **providers:** Add apiKey fallback to SimpleOptionsHelper
- **agent:** Make TransformContext optional with identity default
- **agent:** Auto-default ConvertToLlm to DefaultMessageConverter
- **tools:** Align byte limits to 50*1024 matching TypeScript
- **tools:** Align line truncation suffix to match TypeScript
- **tools:** Implement proper context-based unified diff in EditTool
- **tools:** Detect and use Git Bash on Windows for ShellTool
- **tools:** Resolve bashEscaped variable scoping in ShellTool
- **consistency:** Align docs and code comments with Phase 4 implementation
- **agent:** Wrap listener dispatch in try/catch for exception safety
- **coding-agent:** Add symlink resolution to path validation
- **agent:** Wrap hook invocations in try/catch for graceful degradation
- **docs:** Post-sprint consistency review — sync docs with P0/P1 code changes
- **agent:** Use case-insensitive tool name lookup
- **anthropic:** Include thought signature in tool_use blocks
- **anthropic:** Support object-typed toolChoice for parallel control
- **coding-agent:** Handle explicit cancellation in ShellTool
- **coding-agent:** Respect .gitignore patterns in skills discovery
- **test:** Align port audit tests with actual implementations
- **coding-agent:** Truncate shell output from tail instead of head
- **coding-agent:** Make shell timeout configurable with 600s default
- **agent:** Re-run context transforms on each retry attempt
- **test:** Resolve test failures from Phase 5 implementation
- **providers-core:** Enforce stream termination and api matching
- **providers-anthropic:** Align thinking and header behavior
- **providers-openai:** Correct completions message and reasoning handling
- **providers-openai:** Align responses payload and header precedence
- **providers-openaicompat:** Add finish reason mappings and tool history guard
- **agent:** Only skip steering poll for steering queue
- **agent:** Keep streaming partials in context timeline
- **coding-agent:** Enforce skill metadata validation rules
- **coding-agent:** Align dynamic system prompt behavior
- **coding-agent:** Add context patterns and piped stdin prompts
- **anthropic:** Omit is_error field when false instead of sending null
- **tests:** Rename mismatched test files and seal test classes
- **gateway:** Address P1 issues from design review
- **gateway:** Persist tool events in streaming session history
- **gateway:** Support runtime default-agent updates via options monitor
- **gateway:** Use ChannelManager for adapter lifecycle and lookup
- **gateway:** Add session-store startup guidance and docs
- **gateway:** Standardize cancellationToken naming in websocket handler
- **gateway:** Document and enforce ConfigureAwait in file session store
- **gateway:** Capture streaming history in WebSocket handler
- **channels:** Align stubs to ChannelAdapterBase
- **telegram:** Register options via DI pattern
- **gateway:** Add thread-safe session history access [P0]
- **gateway:** Handle subscription exceptions in agent streaming [P0]
- **webui:** Replace per-element listeners with event delegation [P0]
- **gateway:** Block path traversal in SystemPromptFile resolution [P0]
- **gateway:** Add recursion guard to cross-agent calls
- **gateway:** Prevent duplicate supervisor create races
- **gateway:** Cap websocket reconnection retries
- **gateway:** Remove sync-context risk in agent config startup
- **gateway:** Register platform config via options pattern
- **gateway:** Improve config error messages
- **gateway:** Consistency fixes from Phase 4 review
- **gateway:** Override model BaseUrl from auth endpoint for enterprise Copilot
- **gateway:** Resolve StreamAsync task leak and auth bypass edge case
- **gateway:** Standardize CancellationToken naming and add XML docs to abstractions
- **docs:** Align API reference, README, and sample-config with actual controllers and config schema
- **webui:** Align WebSocket message handler with gateway protocol
- **channels:** Revert WebSocket DI registration to explicit type overload
- **gateway:** Validate chat input and return proper errors
- **gateway:** Migrate HttpClient to IHttpClientFactory
- **gateway:** Add configurable CORS policy
- **api:** Return 400 on mismatched agentId in PUT endpoint
- **api:** Restrict CORS methods in production
- **gateway:** Close Path.HasExtension auth bypass in middleware
- **gateway:** Sanitize AssemblyPath in extensions endpoint to prevent path leak
- **gateway:** Align SupportsThinking to SupportsThinkingDisplay in channel DTO
- **gateway:** Add stale-entry eviction to rate limiting middleware
- **gateway:** Add caller authorization to session metadata endpoints
- **gateway:** Add providers endpoint and sort models
- Add XML doc comments to suppress CS1591 warnings
- Align Add Agent form JSON with AgentDescriptor API model
- **webui:** Filter chat header models by agent provider to remove duplicates
- **webui:** Fix agent switch confirm dialog UX
- **gateway:** Default agent config to ~/.botnexus/agents/
- **gateway:** Relax provider validation — apiKey/baseUrl not required
- **webui:** Load model list on new chat start
- **webui:** Vendor marked + DOMPurify locally — no external CDN calls
- **webui:** Show agent + channel in chat title
- **tests:** Update provider validation tests for optional apiKey
- **webui:** Defer queued message display until processing starts
- **websocket:** Set control metadata for steer messages
- **webui:** Stop old agent instance on new chat + refresh panel
- **webui:** Eliminate sessions panel flicker on refresh
- **webui:** Hide agent dropdown in active sessions, show only for new chat
- **gateway:** Scope built-in tools per agent workspace
- **gateway:** Graceful cancellation handling + logs to ~/.botnexus/
- **gateway:** Decouple agent work from WebSocket lifetime
- **webui:** Paginate session history — load only recent messages
- **gateway:** Exempt WebSocket + static files from rate limiting
- **gateway:** Move rate limiter after UseWebSockets() in pipeline
- **gateway:** Reset WS reconnect throttle on clean disconnect + WRN log level
- **gateway:** Exempt localhost from WebSocket reconnect throttling
- **gateway:** Keep all session connections alive during session switch
- **webui:** Filter WebSocket messages by current session
- **webui:** Show processing indicator when switching to busy agent
- **gateway:** Hourly log rollover + decouple ChatController from request token
- **gateway:** Fix GatewayHost IHostedService DI registration
- **webui:** Add hubInvoke guard + client-side debug logging
- **gateway:** File logs at Information level, console stays Warning
- **signalr:** JoinSession returns session data + fix race condition
- **webui:** Declare healthCheckInterval — fixes init crash
- **webui:** Prevent joinSession infinite recursion
- Declare gatewayHealthy + eliminate SessionJoined callback loop
- **webui:** Defer joinSession to next tick on Connected/reconnected
- **webui:** HubInvoke was calling itself instead of connection.invoke
- **webui:** Replace join guard with version counter for race-safe switching
- **gateway:** Add diagnostic logging to channel resolution + adapter
- **webui:** ContentDelta property name mismatch — server sends contentDelta not delta
- **webui:** Strip [[reply_to]] tags + finalize prev message on MessageStart
- **webui:** Strip [[reply_to]] from history + prevent empty bubbles
- **gateway:** Pass tool arguments through to stream events
- **cron:** Add execution logging + set NextRunAt at creation time
- **webui:** Cron table uses correct property names + working action buttons
- **gateway:** Remove unused /webui auth bypass and route
- **cron:** Recompute NextRunAt on schedule change and prevent clobber
- **webui:** Remove New Chat section and rename SignalR to Web Chat
- **webui:** Eliminate channel list flicker on poll refresh
- **webui:** Move processing status from header bar to inline chat
- **gateway:** Fix steering never being injected or recorded
- **webui:** Channel display names, selection, and auto-reload
- **webui:** Fix chat canvas only using half viewport height
- **webui:** Pin chat messages to bottom of canvas
- **webui:** Remove stray </div> breaking chat-view layout
- **webui:** Move processing status to chat header
- **skills:** Address Nibbler P1 review findings
- **skills:** Address Adversary-found bugs (prompt injection, BOM, limits, duplicates)
- **skills:** Make skill discovery dynamic instead of static snapshot
- **sessions:** Persist tool_name and tool_call_id in SQLite store
- **gateway:** Replace stale exec/process tool references with bash in system prompt
- **gateway:** Nibbler consistency fixes for extension integration
- **scripts:** Deploy extensions after gateway build
- **gateway:** Fix extension type identity mismatch in AssemblyLoadContext
- **webui:** Rename Stop Gateway to Restart, use OK/Cancel dialog
- Resolve GatewayHost session queue deadlock and testhost hang
- Resolve P0-P2 Skills bugs — prompt injection, thread safety, BOM, limits
- Harden test timing patterns — increase timeouts and replace Thread.Sleep
- Complete System.IO.Abstractions audit — plug remaining raw IO gaps
- Resolve compaction provider mismatch — use configured provider for LLM calls
- Consistency review — sub-agent spawning feature alignment
- Remove unused tools from deliver-spec prompt
- **webui:** Remove redundant 'Agent is thinking' bottom indicator
- **webui:** Align steer button with input field during streaming
- **webui:** Fix session switching during active agent work
- Consistency review — session switching alignment
- **webui:** Prevent message send during session switch race window
- **test:** Fix Playwright E2E test host to use real Kestrel server
- **webui:** Fix session switching bugs caught by Playwright E2E tests
- **webui:** Fix stuck UI after session switch — flag and input recovery
- **webui:** Add safety-net timeout to prevent stuck session switch UI
- **gateway:** Fix SessionWarmupService DI registration — use factory for IHostedService
- **gateway:** Include reason in DelayTool success message
- **gateway:** Handle float/string integer parameters in DelayTool and FileWatcherTool
- **tools:** Handle float/string integer parameters across all tools
- **tools:** Fix remaining integer parsing — SessionTool, SubAgentSpawnTool, ExecTool, WebFetchTool
- **docs:** Correct SubAgentArchetype values in ddd-patterns.md
- **docs:** Correct TriggerType values in ddd-patterns.md
- **docs:** Correct TriggerType values in architecture overview
- **docs:** Remove 'Closed' from Sealed status description
- **docs:** Update design-spec status to reflect Wave 1-4 completion
- **docs:** Add BotNexus.Domain to README project structure
- **hub:** Correct value object conversion in GatewayHub methods
- **signalr:** Fix channel adapter type conversions
- **e2e:** Update test infrastructure for simplified connection model
- **e2e:** Fix scrollback tests for current DOM structure
- **e2e:** Fix session switching tests for channel-centric sidebar
- **e2e:** Fix chat sending tests for new SendMessage signature
- Make start-gateway powershell-compatible
- **scripts:** Guard Add-Type to prevent duplicate type error on re-run
- **signalr:** Include sessionId in all ContentDelta events to prevent cross-session bleed
- **webui:** Use storeManager for session ID in sendMessage
- **webui:** Disable send during session switch to prevent misrouting
- **domain:** Add channel alias resolution to ChannelKey
- **webui:** Make loading indicators per-session
- **webui:** Restore loading state on session switch
- **tests:** Use record with-expression for init-only StreamOptions.CancellationToken
- **api:** Flatten /sessions response to prevent nested session object
- **webui:** Fix channel history API paths and session data handling
- **api:** Include toolName and toolCallId in channel history response
- **webui:** Suppress auto-scroll during history batch rendering
- **webui:** Parse tool names from content as fallback
- **webui:** Apply toggle state after history render
- **webui:** Persist sidebar collapsed state to localStorage
- **webui:** Persist tool/thinking toggle state to localStorage
- **webui:** Use actual message timestamp in history rendering
- Strip [[reply_to_current]] control tags from history API and client rendering
- **webui:** Clear display state immediately on agent switch
- **webui:** Defer activeViewId until session resolved
- **webui:** Verify event handler isolation in all handlers
- **webui:** Left-align sidebar hamburger button
- **webui:** Complete storeManager to channelManager migration
- **scripts:** Add missing web and mcp-invoke extensions to deploy script
- **webui:** Persist last channel context for session restore on reload
- **e2e:** Update all test selectors from #chat-messages to per-channel containers
- **webui:** Fix steer/follow-up, scrollback, streaming, and reset bugs
- **webui:** Add init error logging + deploy script dynamic discovery
- **gateway:** Ensure user message triggers LLM call after history injection
- **gateway:** Fix session visibility to exclude cron sessions
- **gateway:** Show cron sessions as read-only in sidebar + add SessionType to SessionSummary
- **gateway:** Exempt polling endpoints from rate limiter + bump default to 300/min
- **gateway:** Filter tool entries from history injection to prevent LLM rejection
- **webui:** Add event drop logging + version commit hash + rate limit opt-in
- **probe:** Handle nested API response in correlate UI
- **scripts:** Use manifest ID for extension deploy paths
- **webui:** Clear stream state on message end regardless of active channel
- **webui:** Remove '(active)' label — all channels are always live
- **probe:** Fix correlation search to cover all data surfaces
- **probe:** Make correlate log entries expand inline with details
- **docs:** Correct architecture overview — AgentCore/Providers are independent libraries
- **docs:** Align architecture docs with correct dependency and session model
- **docs:** Replace DOM-swap model with per-channel containers in webui-connection
- **docs:** Correct stale references in development docs
- **docs:** Minor consistency fixes in planning and user-guide
- **docs:** Fix broken links to nonexistent architecture docs in training
- **probe:** Fix empty logs in web UI — JS didn't read 'items' from API response
- **docs:** Restore original comprehensive domain model document
- PathUtils.GetGitIgnoredPaths no longer throws on out-of-workspace paths
- Preserve JSON arrays as JsonElement in StreamingJsonParser
- Resolve EditTool double-parse of prepared edits argument
- Use path-safe separator in sub-agent AgentId format
- Persist sidebar section and agent-group collapse state across page refresh
- **subagent-ui:** Consistency review fixes (Nibbler)
- **commands:** DI multi-registration and case-insensitive normalization
- **extensions:** Add Gateway.Contracts + Domain to host assemblies, improve discovery logging
- Deduplicate extensions by ID during discovery to prevent topo-sort crash
- Register MemoryIndexer as hosted service to enable conversation indexing
- Bridge sub-agent lifecycle events to SignalR for real-time web UI updates
- Correct tutorial config format and replace placeholder URLs
- Enable multi-handler media pipeline and add extension manifest
- Add InternalChannelAdapter for sub-agent completion delivery
- Make hub dispatch non-blocking to prevent cross-agent session blocking
- Reactivate sealed sessions on new messages (like expired sessions)
- Integration test harness — process-based with real providers
- Capture Context.ConnectionId before fire-and-forget dispatch
- Non-blocking MCP server startup and memory initialization
- MCP transport auto-detects Streamable HTTP vs legacy SSE
- Guard WhisperTranscriptionHandler.DisposeAsync against missing assembly
- /new creates fresh session instead of reactivating sealed one
- MCP SSE transport hangs on HTTP/2 — force HTTP/1.1
- Stream events misrouted when switching agent tabs
- **blazor:** Use publish output for WASM hosting — fixes fingerprint placeholders
- Blazor SPA serving + non-collectible ALC for endpoint extensions
- Blazor SPA serving via inline middleware + non-collectible ALC
- **blazor:** SessionStatus enum serializes as string for SignalR
- **blazor:** ContentDelta handler expects AgentStreamEvent not ContentDeltaPayload
- **blazor:** AgentStreamEventType enum serializes as string for SignalR
- **blazor:** Add message timestamps and verify chronological ordering
- Deploy script skips projects without manifest — no derived IDs
- Deploy script scrubs stale extension directories after deploy
- **blazor:** History ordering + autoscroll — newest at bottom
- **blazor:** Force-scroll to bottom on session switch
- **blazor:** Defer force-scroll to next animation frame
- **blazor:** Scroll to bottom on session switch via DOM query
- **test:** Resolve flaky ShellTool and GatewayStartup tests
- **build:** Resolve all compiler and NuGet warnings
- **cli:** Always show build output to prevent apparent hang
- **build:** Strip Using items and override OutputType when SkipTests=true
- **build:** Suppress CS2008 warning from empty test assemblies
- **gateway:** Add 'copilot' → 'github-copilot' alias in ModelRegistry
- **gateway:** Invalidate cached agent instances on descriptor change
- **webui:** Restore auto-scroll by reordering OnAfterRenderAsync
- **webui:** Update comments and docs to match autoscroll fix
- **cli:** Enhance gateway PID recycling guard with MainModule validation
- **consistency:** Align docs with code for subagent session view
- **gateway:** Register MemoryStoreTool when memory is enabled
- **gateway:** Sub-agent completion now wakes parent agent session
- **blazor:** Remove stale CSS isolation link and fix enumeration crash
- Prevent security scan from flagging its own grep patterns (#7)
- Remove redundant DeployExtension target from SignalR csproj (#4)
- Cross-platform test compatibility wave 2 (#9)
- **tests:** Cross-platform test compatibility wave 3 (#10)
- **cli:** Make GatewayProcessManager cross-platform (Linux/macOS) (#11)
- Suppress NO_REPLY in SignalR adapter; expose skill path on load (#15)
- **critical:** Tool execution timeout + SQLite session store concurrency (#16)
- Ollama support, mobile UI improvements, session history, Linux fixes (#17)
- **cli:** Thread --target home path into GatewayProcessManager (#42)
- **cli:** Embed git commit SHA into gateway build via SourceRevisionId (#49)
- **config:** Default session and conversation store to SQLite not InMemory (#52)
- **blazor-client:** Fix conversation UI bugs — sidebar, title rename, agent refresh, conversation history (#50)
- **cli:** Set BOTNEXUS_HOME on spawned gateway process (#54)
- **portal:** Clear stale session ID when switching to a new conversation (#57)
- **cli:** Skip building CLI project when invoked via gateway start (#60)
- **portal:** Consolidate new session buttons — one button in chat header (#62)
- **portal:** Ensure conversation history loads on first page load (#66)
- **portal:** Show tool results correctly when loading conversation history (#68)
- **blazor-client:** Reliable conversation history load on first page visit (#73)
- **gateway:** Three bugs - conversationId in sessions list, inbound conversation routing, cron config test (#85)
- **gateway:** Probe-round3 — title validation, archive filtering, sealed session guard (#87)
- **cli:** Check StopAsync result and port availability in update command (#88)
- **portal:** Subscribe all components to IClientStateStore.OnChanged (#89)
- **cli:** Change InitCommand default listenUrl to 0.0.0.0:5005 (#96)
- **e2e:** Fix test selectors, agent, and add history persistence tests (#97)
- **portal:** Streaming and session state scoped to conversation not agent (#103)
- **ci:** Restore contents: write on release job to unblock git push (#105)

### 📖 Documentation

- **ai-team:** Merge implementation plan and team directives
- **ai-team:** Log Sprint 1 completion — all 7 foundation items done
- **ai-team:** Log Sprint 2 completion — dynamic loading fully wired
- **config:** Add comprehensive configuration guide
- **extensions:** Add extension development guide
- **architecture:** Add system architecture overview
- **ai-team:** Log Sprint 4 completion — all sprints done, 192 tests passing
- Align all documentation and code comments with current architecture
- **ai-team:** Log consistency audit and Nibbler onboarding
- **testing:** Create E2E scenario registry
- **workspace:** Add workspace and memory model documentation
- **ai-team:** Log Sprint 5 completion — workspace, memory, deployment tests done
- **cron:** Add cron and scheduling documentation
- Update Fry history with cron observability deliverables
- Align cron documentation and code comments
- **zapp:** Update history with 100% scenario coverage sprint
- **ai-team:** Log full session completion — 71 items, 395 tests, 100% scenario coverage
- **getting-started:** Add comprehensive getting started guide
- **ai-team:** Log getting started guide and Kif onboarding
- **getting-started:** Update guide to reflect first-run fixes
- **getting-started:** Add WebUI setup and usage section
- **ai-team:** Log Sprint 7 completion — CLI, doctor, hot reload done
- **scenarios:** Add CLI, doctor, and hot reload scenarios
- Align documentation with Sprint 7 CLI, doctor, and hot reload
- **.squad:** Merge Hermes test fixes + critical directives
- **getting-started:** Add extension deployment step for provider setup
- Split getting-started into release and dev guides
- Add pack step to dev workflow in getting-started guide
- Update leela history with internal tools review notes
- Record parallel publish fix in Bender's history
- Update Farnsworth history with nullable generation settings learning
- **squad:** Orchestration log for sprint 4 parallel ui and config
- Update Leela history with CLI agent add fix
- Update Leela investigation log with test validation results
- Add agent continuation prompting to history and decisions
- **squad:** Cross-agent updates for tool UI and multi-turn work (2026-04-03T04:50Z)
- **squad:** Leela investigation of token deletion and audit logging decision
- Comprehensive documentation sweep for 12 new features
- Update Bender history with model resolution fix
- Add skills system architecture and implementation summary
- Add comprehensive Skills system documentation
- **.squad:** Skills sprint completion — agent orchestration and team updates
- Update Leela history and create auth decision document
- Document agentic streaming architecture decision
- **bender:** Document streaming gateway integration
- Scribe post-sprint orchestration (2026-04-03T20:23:07Z)
- Add disabledSkills to API reference examples
- **squad:** Document multi-turn investigation findings and WebSocket routing fix
- **squad:** Build failure retrospective and prevention rules
- **squad:** Multi-turn tool call bug fix retrospective
- Add Responses API migration and loop detection documentation
- Document Pi-style provider architecture with model-aware routing
- Verify AgentLoop and Gateway integration with Pi provider architecture
- Post-sprint consistency review for Pi provider architecture
- Update Nibbler history with Pi provider review session
- Pi comparison sprint orchestration logs
- **providers:** Add comprehensive README for provider system
- **agent-core:** Add multi-sprint plan for pi-mono agent port
- **agent-core:** Enrich XML documentation to match pi-mono quality
- **agent-core:** Add comprehensive README
- **coding-agent:** Add comprehensive README
- **squad:** Archive complete + CodingAgent build log
- **audit:** AgentCore vs pi-agent-core alignment report
- **audit:** Providers.Core vs pi-ai alignment report
- **audit:** CodingAgent vs pi-coding-agent alignment report
- Merge port audit decisions — Farnsworth, Bender, Leela re-audits
- Add training documentation structure and overview
- Add provider system and streaming training docs
- Add agent loop and tool execution training docs
- Add coding agent and build-your-own training guides
- Update training docs to reflect Wave 1-2 code changes
- Log Wave 4 final results and close audit session
- Add training documentation for agent architecture
- Scribe orchestration log (P0 sprint completion)
- **squad:** Port audit sprint retro and now.md update
- **training:** Update training documentation for agent and provider architecture
- **squad:** Scribe log for port audit sprint 2
- **squad:** Record bender learnings
- Phase 3 training documentation — context discovery, thinking levels, custom agent, tool dev
- Post-sprint 3 consistency review — fix 22 discrepancies across 7 files
- Post-sprint 3 consistency review report and history update
- **squad:** Phase 3 port audit retrospective — findings, now.md update, learnings
- **audit:** Port audit findings for pi-mono vs BotNexus
- **training:** Add architecture deep-dive and provider development guide
- Fix stale tool names, parameter names, and type references across training docs and READMEs
- Update Nibbler learnings from consistency review
- **retro:** Port audit retrospective
- **agent:** Document error message sync contract between state and message
- **providers:** Add XML doc comments to StopReason enum values
- **training:** Add provider architecture training documentation
- **training:** Add agent event system training documentation
- **training:** Add tool security model training documentation
- **training:** Add building a coding agent training documentation
- **training:** Update README with focused deep-dive documentation links
- Orchestration logs and session log for port-audit remediation sprint
- Post-sprint consistency review for P0/P1 fixes
- **training:** Update tool name lookup from case-sensitive to case-insensitive
- Post-audit consistency review
- Log phase5 audit orchestration
- Update coding agent docs for Phase 5
- Agent core context transform changes
- Update provider docs for Phase 5
- Update building your own agent for Phase 5
- Create Phase 5 migration guide
- **training:** Update CodingAgent.CreateAsync signatures and built-in tools
- Consistency review — mark 16 fixed findings, align training docs and READMEs with code
- Log consistency review ceremony results
- **review:** Gateway Service architecture design review — grade A-
- **scribe:** Merge gateway reviews into decisions log
- **webui:** Update Fry history and add WebUI build decision
- **squad:** Update Bender history with P1 fix learnings
- **scribe:** Log Gateway P1 sprint + merge decisions
- **review:** Gateway P1 sprint design review
- **scribe:** Merge decision inbox into decisions.md
- **squad:** Update now.md and agent histories post P1 sprint
- **fry:** Record Phase 2 WebUI enrichment learnings
- **gateway:** Add example agent configuration
- **squad:** Log Phase 2 reviews and finalize sprint
- **squad:** Update agent histories for P0 remediation sprint
- **squad:** Update agent histories for Phase 2 architecture gaps
- **squad:** Phase 3 sprint design review — grade B+
- **squad:** Finalize Phase 3 sprint — update now.md and histories
- **hermes:** Record live integration learnings
- **squad:** Capture gateway phase4 runtime learnings
- **gateway:** Capture config and auth learnings
- **gateway:** Add gateway module README and update root docs
- **fry:** Update history with WebUI enhancements sprint
- **gateway:** Add module READMEs for abstractions, sessions, and channels
- **squad:** Update kif history with module README delivery
- **gateway:** Update README with phase 5 features and auth/lifecycle/workspace docs
- Add dev loop and deployment documentation
- Phase 5 consistency review — 0 P0, 3 P1, 6 P2, 3 P3
- **channels-tui:** Update README to reflect implemented input loop
- Add developer guide and update API reference
- **squad:** Merge gateway-phase6-batch1 decisions + inbox cleanup
- **squad:** Update cross-agent history notes for phase6-batch1
- **squad:** Merge leela phase6 design review
- **squad:** Add Phase 6 retro notes and update focus
- Phase 7 gateway gap analysis and sprint plan
- **squad:** Record sprint 7a gateway learnings
- **api:** Add OpenAPI spec generation and export
- **squad:** Record sprint 7a openapi learnings
- **bender:** Record sprint7a runtime learnings
- **squad:** Log Sprint 7A batch 1 orchestration
- **squad:** Add core context summaries to agent history files
- **squad:** Sprint 7A design review
- **squad:** Log Sprint 7A review + retro
- **squad:** Record WebUI protocol verification results in Fry history
- Update dev setup guide for Gateway Service (port 5005, config.json structure, auth flow)
- **review:** Full Gateway design review — Grade A-
- **consistency:** Fix Gateway docs-code alignment gaps post Phase 7+
- **squad:** Phase 8 retro and identity update
- Overhaul dev loop documentation, fix pre-commit hook
- **squad:** Phase 9 Wave 1 — orchestration log, session log, decisions merge
- **squad:** Phase 9 Wave 2 — orchestration log, session log, decisions merge
- **squad:** Phase 9 reviews — design A-, consistency Good
- Add CLI command reference and update getting-started guides
- **consistency:** Fix Phase 10 alignment gaps
- **squad:** Phase 10 — orchestration log, session log, decisions merge
- Add module READMEs for gateway projects and update test coverage
- Update Kif history with XML comments and module READMEs delivery
- **consistency:** Fix Phase 11 alignment gaps
- **scribe:** Wave 3 orchestration logs, session log, cross-agent history updates
- **channels:** Add WebSocket channel README
- **scribe:** Phase 12 Wave 1 orchestration logs, session log, decision merge
- **consistency:** Fix Phase 12 Wave 1 alignment gaps
- **kif:** Record API reference update learnings
- Update fry history with Wave 2 channels/extensions learnings
- **scribe:** Phase 12 Wave 2 orchestration logs, session log, decision merge
- **consistency:** Fix Phase 12 Wave 2 alignment gaps
- Add WebSocket protocol specification
- Add configuration reference guide
- Add developer guide for local development
- **scribe:** Phase 12 Wave 3 orchestration logs, session log, decision merge
- Reorganize documentation structure
- Session log - tool system refactor complete (Bender)
- Add observability architecture guide (Wave 4)
- Memory system architectural review + implementation plan
- **gateway:** Update all references from /webui to root URL
- **skills:** Add Adversary security audit report
- **skills:** Rewrite skills guide to match current implementation
- Add MCP extension architectural design document
- **squad:** Orchestration log + session log + decision merge
- **squad:** Log CopilotMcpSearchProvider session
- Draft sub-agent spawning feature documentation
- Nibbler history — session switching consistency review
- Architecture proposal — multi-session connection model
- Add design spec for agent delay/wait tool
- Design spec for file watcher tool
- Design item — per-agent file system permission model
- Design spec — infinite scrollback history across sessions
- Add per-agent file permission model design spec + review
- Update permission model spec to include glob pattern support
- Add introduction for Agent domain object in BotNexus
- Revise domain model with refined agent concepts
- Add DDD patterns developer reference guide
- Add deferred phases reference for future DDD delivery cycles
- Update architecture docs for session model changes
- Update architecture docs for session model changes
- Record kif learnings from wave 2-3 doc update
- Wave 4 DDD refactoring orchestration & decision merge (2026-04-12T04)
- **squad:** Update nibbler history with DDD consistency review
- Update infinite scrollback spec for DDD types and simplified model
- Update multi-session connection spec for DDD types and SubscribeAll pattern
- Remove duplicate SessionStatus documentation line
- Update SignalR subscribe-all contract
- Mark session switching bug as likely obsolete
- Mark multi-session connection and session visibility as implemented
- Update remaining specs for DDD types and connection model
- Add planning specs assessment after DDD/WebUI refactoring
- **domain:** Add XML docs to all Domain public types
- **contracts:** Add XML docs to Gateway.Contracts interfaces
- **prompts:** Add XML docs to Prompts public API
- **sessions:** Add XML docs to Sessions.Common public API
- **api:** Add XML docs to controllers and hub
- **agentcore:** Add XML docs to AgentCore public API
- **providers:** Add XML docs to Providers.Core abstractions
- **channels:** Add XML docs to Channels.Core abstractions
- **tools:** Add XML docs to Tools public API
- **architecture:** Add message routing, agent execution, and triggers documentation
- **architecture:** Add comprehensive system documentation after DDD refactoring
- **user-guide:** Add getting started guide
- **user-guide:** Add configuration reference
- **user-guide:** Add agent setup and extension development guides
- **user-guide:** Add troubleshooting guide
- **webui:** Document multi-tab localStorage safety in storage.js
- Update session resumption spec with post-DDD review findings
- Log BotNexus.Probe completion — 33 tests passing
- Update session-debug skill to prefer BotNexus Probe CLI
- Merge Probe CLI decision into decisions.md
- Mark file access policy spec as delivered
- Add file access policy and rate limit settings to configuration guide
- Add location management design spec and research
- **architecture:** Consolidate to concise high-level reference
- **architecture:** Add new overview.md
- Add copilot-instructions with BotNexus config location
- Update subagent-ui-visibility spec to align with current codebase
- Add design review for feature-subagent-ui-visibility
- Incorporate design review conditions into spec
- Mark feature-subagent-ui-visibility as delivered
- Update Copilot instructions and add agent document ownership guidelines
- Scribe orchestration log — Extension-Commands Wave 1 design review & delivery
- **commands:** Mark extension-contributed-commands as delivered
- Planning management skill, INDEX.md, archive consolidation, spec updates
- **config:** Mark extension-config-inheritance as delivered
- Add design review for feature-user-documentation
- Migrate existing content for MkDocs Material compatibility
- Add media handler extension guide and audio recording user guide (Wave 4b)
- Add specs for SQLite session lock bug and dynamic config reload
- Add spec for config management API with extension metadata
- Add never-guess-time rule to AGENTS.md
- Add context diagnostics spec — /context command + debug API
- Mark feature-context-diagnostics as delivered
- Phase 1 design review — SignalR extraction + extension loader
- Update blazor-webui spec with full delivery status (Phases 1-4)
- Add AGENTS.md to src/agent/ — no external dependencies rule
- Add AGENTS.md to domain/ and gateway/ with dependency rules
- Add AGENTS.md to common/ with dependency audit findings
- Document gateway hook types — clarify layer boundary with Agent.Core hooks
- **agents:** Add conventional commits guidance to AGENTS.md
- **cli:** Add install, build, and serve to CLI reference
- **getting-started:** Update setup guides to use CLI commands
- **dev-guide:** Update quick start and scripts reference for CLI
- **webui:** Mark legacy WebUI as reference-only
- **tools:** Document tool architecture and fix ProcessTool coupling
- Consolidate developer docs and update WebUI references to Blazor
- **test:** Add AGENTS.md with Shouldly conventions and test infrastructure
- Require XML doc comments on all public API with context-over-code rule
- Add guidance for private member comments in AGENTS.md
- **cli:** Add wizard dev guide, provider command reference, and alias setup
- **squad:** Add Blazor layout restructure decision and Fry history update
- **build:** Add MSBuild conventions section to AGENTS.md
- **planning:** Design review for bug-blazor-autoscroll
- **planning:** Design review for gateway detached process
- **squad:** Record Wave 1 gateway process manager completion
- **squad:** Document Wave 2 gateway command refactor completion
- **squad:** Document Wave 3 test suite completion
- **cli:** Document gateway lifecycle management commands
- **squad:** Update Leela history with Blazor subagent design review
- **squad:** Hermes Wave 2 test completion for read-only sub-agent view
- **decisions:** Document read-only banner design decisions
- **webui:** Document read-only sub-agent session viewing
- **history:** Add learning about System.CommandLine command sharing
- **planning:** Fix stale FollowUpAsync references and bug spec status
- **planning:** Mark bug-subagent-completion-wakeup as delivered
- Add no-worktrees directive to copilot instructions
- **planning:** Archive delivered bugs and add new bug specs
- **planning:** Update specs — session-switching done, config-ui partially-delivered (#14)
- **planning:** Conversation topics / omnichannel continuity design spec (#18)
- **design:** Portal load sequence redesign — architecture, interfaces, sequence diagrams (#80)
- **design:** Auto-update feature — gateway self-update from portal UI (#92)

### ⚡ Performance

- Add xunit.runner.json to all test projects + optimize GlobTool gitignore batching
- **test:** Refactor E2E tests to share Kestrel server + Playwright browser
- **test:** Share Kestrel server + Playwright browser across E2E tests

### 🔨 Refactor

- **memory:** Migrate MemoryStore to agent workspace path structure
- **agent:** Wire AgentLoop to use IContextBuilder for context assembly
- **heartbeat:** Migrate HeartbeatService to thin cron adapter
- Move channel projects to src/channels/ directory
- Archive old src/ projects to archive/src/
- Archive old test projects to archive/tests/
- Replace static registries with instance-based
- Convert Usage and StreamOptions to immutable records
- Standardize HttpClient injection across all providers
- **anthropic:** Extract message converter, request builder, and stream parser
- **providers:** Standardize JSON construction across providers
- **providers:** Replace SupportsExtraHigh string hack with LlmModel property
- **providers:** Align MessageTransformer normalizer with pi-mono signature
- **gateway:** Introduce IChannelManager abstraction
- Remove obsolete scripts and decision documents for channel stubs and WebUI rebuild
- **gateway:** Extract shared streaming session helper
- **gateway:** Extract SessionReplayBuffer from GatewaySession
- **gateway:** Decompose GatewayWebSocketHandler into focused components
- **cli:** Extract ValidateCommand handler
- **cli:** Extract InitCommand handler
- **cli:** Extract agent command handlers
- **cli:** Extract config command handlers
- **cli:** Slim Program.cs to command registration only
- **gateway:** Move SessionHistoryResponse to Abstractions.Models
- **gateway:** Inject IWebHostEnvironment in auth middleware instead of service locator
- WorkspaceContextBuilder delegates to SystemPromptBuilder
- **webui:** Single persistent WebSocket connection pattern
- **gateway:** Support websocket session switching on single connection
- **gateway:** Replace raw websocket API with SignalR hub
- **skills:** Use BeforePromptBuild hook instead of hardwired context builder
- **gateway:** Decouple extension config from core abstractions
- Adopt System.IO.Abstractions in Tools, Skills, Memory, Cron, ExecTool
- Adopt System.IO.Abstractions in CodingAgent for testable file I/O
- Adopt System.IO.Abstractions in Gateway for testable file I/O
- Extract shared OpenAI streaming logic to Providers.Core
- Eliminate NormalizeLineEndings duplication in Tools
- Clean PlatformConfig legacy root-level duplication
- Decouple cron from IChannelAdapter into IInternalTrigger
- **domain:** Improve AgentId and SessionId value object validation
- **abstractions:** Adopt AgentId and SessionId in gateway contracts
- **sessions:** Adopt typed IDs in session store implementations
- **gateway:** Adopt typed IDs in gateway runtime
- **api:** Adopt typed IDs in API controllers and hubs
- **sessions:** Extract SessionStoreBase with shared query logic
- **sessions:** Add status and null-safe shared filters
- **gateway:** Enhance OpenTelemetry instrumentation
- **prompts:** Extract shared prompt primitives to BotNexus.Prompts
- **gateway:** Move gateway interfaces to Contracts
- **gateway:** Decompose SystemPromptBuilder into section pipeline
- **coding-agent:** Delegate to shared prompt primitives
- **abstractions:** Add TypeForwardedTo for moved types
- **gateway:** Update direct project references to use Contracts
- **domain:** Extract Session domain type from GatewaySession
- **gateway:** Extract GatewaySessionRuntime for infrastructure concerns
- **gateway:** Update GatewaySession to compose Session + Runtime
- **gateway:** Update consumers to use split session types
- **sessions:** Extract JSONL parsing to shared library
- **coding-agent:** Delegate to shared session primitives
- **gateway:** Delegate FileSessionStore to shared primitives
- **webui:** Consolidate global state into SessionStoreManager
- **webui:** Remove deprecated sessionState Map
- **webui:** Remove dead code (loadEarlierMessages, loadOlderSessions)
- **webui:** Simplify event handlers to use single state source
- **hub:** Make SendMessage accept agentId+channelType with auto-session
- **hub:** Deprecate JoinSession and LeaveSession
- **webui:** Remove joinSession and simplify channel switching
- **webui:** Simplify reconnection to SubscribeAll-only
- **tests:** Extract shared MockMcpTransport to common test assets
- **gateway:** Use typed channel and hub identifiers
- **hub:** Remove redundant NormalizeChannelType
- **webui:** Extract storage.js — centralized localStorage management
- **webui:** Make tool/thinking toggles per-channel with clear storage separation
- **webui:** Move sidebar toggle into sidebar with section icon rail
- **webui:** Add ChannelContext and ChannelManager classes
- **webui:** Update index.html for channel-views container
- **webui:** Add CSS for per-channel container elements
- **webui:** Update DOM cache and scroll helpers for channel views
- **webui:** Route all rendering to channel-specific containers
- **webui:** Route events to channel contexts by sessionId
- **webui:** Update app.js event delegation for channel views
- **signalr:** Typed hub contracts and Hub<IGatewayHubClient>
- Gateway.Api has zero SignalR knowledge — fully extension-loaded
- Rename AgentCore and Providers to Agent.Core and Agent.Providers.*
- Move extensions from /extensions/ to src/extensions/ and flatten structure
- Channels become extensions — Channels.Core → Gateway.Channels, channel projects → Extensions.Channels.*
- Move CodingAgent to poc/ — proof of concept, not production code
- Move Cron, Memory, Tools to src/common/
- Move Prompts to src/common/ — core infrastructure, not extension
- Merge Sessions.Common into Gateway.Sessions — single consumer
- Move Cron, Memory, Tools to src/gateway/ — they depend on agent+gateway layers
- Move Prompts to gateway, rename to Gateway.Prompts — eliminate src/common/
- **scripts:** Simplify start-gateway.ps1 to delegate to CLI
- **scripts:** Update dev-loop to use CLI and remove redundant deploy-extensions
- **test:** Replace FluentAssertions with Shouldly (BSD-2-Clause)
- **cli:** Migrate all commands to Spectre.Console output
- **domain:** Rename InboundMessage/OutboundMessage.ConversationId to ChannelAddress (#19)
- **domain:** Rename agent-to-agent exchange types for clarity (#21)
- **cli:** Replace --path/--dev with --source and --target (#22)
- **signalr-blazor:** Replace session manager with event handler (#83)

### 🎨 Styling

- **blazor:** Polish read-only banner — accessibility and design consistency

### 🧪 Testing

- **loader:** Add comprehensive extension loader unit tests
- **e2e:** Add integration tests for dynamic extension loading
- **e2e:** Add multi-agent simulation environment with 5 agents and mock channels
- **agent:** Add comprehensive AgentWorkspace unit tests
- **tools:** Add comprehensive memory tools unit tests
- **agent:** Add comprehensive AgentContextBuilder unit tests
- **workspace:** Add workspace and context builder integration tests
- **memory:** Add comprehensive MemoryConsolidator unit tests
- **deployment:** Add deployment lifecycle E2E tests
- **cron:** Add comprehensive cron system unit tests
- **cron:** Add cron system integration tests
- **cron:** Add cron system E2E tests
- **scenarios:** Implement all remaining E2E scenarios for 100% coverage
- **e2e:** Add getting-started guide validation test
- **diagnostics:** Add unit tests for all health checkups
- **cli:** Add CLI integration tests
- **gateway:** Add config hot reload integration tests
- **cli:** Add backup command integration tests
- Update test mocks for StreamingChatChunk interface
- Add comprehensive tests for Pi-style provider architecture
- **agent-core:** Add test utilities (mock provider, test tools, helpers)
- **agent-core:** Add MessageConverter and ContextConverter tests
- **agent-core:** Add ToolExecutor tests
- **agent-core:** Add Agent class and PendingMessageQueue tests
- **coding-agent:** Add tool unit tests (Read, Write, Edit, Shell, Glob)
- **coding-agent:** Add session and config tests
- **coding-agent:** Add hooks and utility tests
- **coding-agent:** Add GrepTool and SessionCompactor tests
- Add registry isolation and resolution tests
- Add session compaction and extension lifecycle tests
- Add system prompt builder tests
- Add immutable options and provider alignment tests
- Add MessageTransformer and session tree model tests
- Add regression tests for port audit P0 fixes
- **providers:** Add regression tests for port audit P0/P1 fixes
- **agent-core:** Add regression tests for failure handling, queue priority, tool lookup
- **coding-agent:** Add regression tests for compaction, convertToLlm, BOM, grep defaults
- **coding-agent:** Update converter mapping test
- **coding-agent:** Align tool tests with new tool semantics
- **coding-agent:** Update session and loader behavior tests
- **agent:** Align streaming state assertions with MessageEnd add
- **providers:** Align assertions with updated stop and identity rules
- **providers:** Add ModelRegistry equality and StopReason mapping tests
- **agent:** Add streaming state, queue, and config default tests
- **coding-agent:** Add EditTool diff, ShellTool bash, and byte limit tests
- **agent:** Add listener exception safety tests
- **agent:** Add hook exception safety tests
- **agent:** Add retry delay cap tests
- **coding-agent:** Add symlink path rejection tests
- **providers:** Add model registry validation tests
- Add port audit verification tests
- **coding-agent:** Add shell tool truncation and timeout tests
- **providers:** Add tool call validator tests
- **coding-agent:** Add list directory and context discovery tests
- **agent:** Add per-retry transform tests
- **providers:** Add shortHash and normalizer tests
- **agent:** Add follow-up continuation steering poll coverage
- **coding-agent:** Add validation prompt and stdin coverage
- Scaffold gateway test specifications and stubs
- **gateway:** Add real unit tests for core implementations
- **gateway:** Expand and stabilize gateway test coverage
- **gateway:** Expand and stabilize gateway test coverage
- **gateway:** Add GatewayHost dispatch pipeline tests
- **gateway:** Add streaming pipeline integration coverage
- **gateway:** Cover supervisor store auth and channel gaps
- **gateway:** Add copilot integration test infrastructure
- **gateway:** Add configuration subsystem test coverage [P1]
- **gateway:** Add integration tests for cross-agent, steering, isolation, and platform config
- **gateway:** Expand copilot live integration coverage
- **gateway:** Add GatewayAuthManager and integration tests
- **gateway:** Add anticipatory test scaffolding for phase 5 features
- **gateway:** Activate anticipatory tests and add phase 5 integration tests
- **gateway:** Add cross-agent calling tests
- **gateway:** Add Sprint 7A comprehensive tests
- **gateway:** Cover websocket protocol and lifecycle gaps
- **providers:** Add provider conformance test suite
- **gateway:** Add deployment validation and startup tests
- **gateway:** Add config path resolver tests
- **gateway:** Add schema validation tests
- **gateway:** Add config loader edge case tests
- **cli:** Add CLI command handler tests
- **gateway:** Add extension loader tests
- **telegram:** Add Telegram adapter unit tests
- **gateway:** Add Wave 1 coverage — auth bypass, channels, extensions endpoints
- **gateway:** Add Wave 2 coverage — rate limiting, correlation IDs, session metadata, config versioning
- **gateway:** Add Wave 3 coverage — SQLite store, health, lifecycle, metadata auth, eviction
- Add providers and models controller tests
- **gateway:** Add wave 1 correlation id middleware scenarios
- Add OTel diagnostics and Serilog config tests
- Add memory system wave 1 coverage
- Skip deadlocking WebSocket tests after single-connection refactor
- **gateway:** Fix websocket deadlock coverage for single-connection flow
- Exclude WebApplicationFactory tests that hang test runner
- Fix integration tests — no more hangs, 470 passed
- **gateway:** Add comprehensive SignalR hub integration tests
- **cron:** Add tests for stale NextRunAt, timezone, and clobber fixes
- **cron:** Add TDD corner-case tests for scheduler and tool
- **gateway:** Add SessionTool tests for all access tiers
- **skills:** Add comprehensive SkillTool tests
- **skills:** Add additional coverage from Adversary report
- **mcp:** Expand MCP extension test coverage
- Add security and adversarial tests for CodingAgent, AgentCore, and MCP
- Expand ProcessTool (9→50+) and Memory (24→50+) test coverage
- Comprehensive E2E integration tests for session resume
- Comprehensive E2E and adversarial tests for session compaction
- **gateway:** Add sub-agent model and manager unit tests
- **gateway:** Add sub-agent tool and integration tests
- **gateway:** Add session switching behavior tests
- **gateway:** Expand SignalR session switching integration tests
- Add Playwright E2E test project for WebUI session switching
- **webui:** Add P0 Playwright E2E tests — 30+ interaction tests
- **webui:** Add P1 Playwright E2E tests — 42 interaction tests across 11 classes
- **webui:** Add P1 Playwright E2E tests — 40+ interaction tests
- **gateway:** Add Phase 1 multi-session tests — warmup + subscription
- **webui:** Update E2E tests for multi-session connection model
- **gateway:** Add DelayTool unit tests
- **gateway:** Add FileWatcherTool unit tests
- **gateway:** Add channel history pagination and ListByChannel tests
- **webui:** Add Playwright E2E tests for infinite scrollback
- **gateway:** Add PathValidator permission model tests
- Add BotNexus.Domain value object and smart enum unit tests
- Add Wave 2 session model and value object adoption tests
- Add Wave 3 sub-agent archetype, cron trigger, and typed ID tests
- Update tests for AgentId and SessionId value object adoption
- Add existence query dual-lookup tests
- Add SessionStoreBase contract tests
- **sessions:** Cover existence dual-lookup query behavior
- Add snapshot tests for current SystemPromptBuilder output
- Add agent-to-agent conversation and cycle detection tests
- Add soul session lifecycle tests
- Add world descriptor tests
- Add prompt section unit tests
- Verify all projects build with split abstractions
- Add GatewaySession behavioral snapshot tests before split
- Add cross-world federation tests
- Verify session split with existing test suite
- Add shared session primitives tests
- Verify session start/resume with typed IDs
- Add session visibility rule tests
- Update hub tests for simplified connection model
- **domain:** Add ConversationRequest and CrossWorldPermission coverage
- **gateway:** Add SoulTrigger and agent registry contract tests
- Add send-during-switch guard test
- Add channel alias resolution tests
- **integration:** Add multi-agent session isolation tests
- **hub:** Add SendMessage routing and auto-creation tests
- Comprehensive test coverage improvements across 10 projects
- Add session resumption context bridge tests
- Add file access policy configuration tests
- Add location registry configuration and resolver tests
- Add location reference resolution tests
- Add CLI location command tests
- Add MediaPipeline unit tests (Wave 2)
- Add sub-agent completion wake-up tests
- Add heartbeat service tests (Wave 3)
- Add InternalChannelAdapter tests
- Add multi-agent concurrency integration test harness
- Multi-MCP server loading scenario — 3 servers, all inject tools
- Context diagnostics integration scenario + session path substitution
- Extract shared SignalR channel registration helper for integration tests
- **cli:** Update assertions for Spectre.Console output and github-copilot default
- **blazor-client:** Update HomePageTests and add MainLayoutTests for new layout
- **webui:** Add verification report for autoscroll fix
- **cli:** Add comprehensive test suite for GatewayProcessManager and HttpHealthChecker
- **blazor:** Add unit tests for read-only sub-agent session view
- **blazor-client:** Restore component test coverage for Wave 3 architecture (#84)
- **probe:** Probe round 2 — 15 new tests across 3 surfaces (#86)
- **e2e:** Add BotNexus.E2ETests with Playwright (#93)

### ⚙️ Miscellaneous

- **squad:** Update Zapp history with scenario registry learnings
- **.squad:** Backup CLI implementation, test isolation pattern
- Add commit instructions to all agent charters
- Update Bender history with model logging learnings
- **.squad:** Merge decision inbox and update leela history
- **scribe:** Sprint orchestration & decision log
- **git:** Add pre-commit hook installer script
- Remove unused MCP configuration file
- Scribe orchestration log for pi port sprint
- Register new provider and test projects in solution
- Register new provider and test projects in solution
- **squad:** Update agent history for provider architecture work
- **.squad:** Merge decision inbox & session logs
- Merge squad decision inbox into decisions.md
- **.squad:** Merge audit reports into decisions
- **.squad:** Sprint 4 consolidation — decisions merged, orchestration logs created
- **squad:** Record provider alignment learnings
- **squad:** Record P0 safety fix learnings
- **squad:** Update hermes learnings
- **squad:** Add port audit remediation retrospective learnings
- Merge port-audit sprint decisions and orchestration logs
- **squad:** Phase 5 port audit retrospective
- **squad:** Log port audit session
- **squad:** Port audit retrospective
- Merge gateway architecture decision
- **scribe:** Log Phase 4 Wave 1 delivery — orchestration logs, session log, decision merge, agent updates
- **scripts:** Add dev scripts, sample config, and workflow docs
- **squad:** Update team history and decisions for gateway sprint
- **squad:** Log batch 1 session and merge decisions
- **squad:** Log batch 2 session and merge decisions
- **squad:** Update focus to Phase 5 complete
- **scripts:** Harden dev-loop and platform config validation
- **squad:** Record gateway P1 decisions and learnings
- Phase 11 Wave 1 orchestration & session logs
- Phase 11 Wave 2 cross-agent history updates
- Update kif history with Wave 3 learnings
- Update now.md for Phase 12 complete
- **.squad:** Clean up temporary commit message file
- Update squad state — history, decisions, OTel proposal merged
- Update squad state — directives and decisions
- Remove unused package.json + fix error response in GatewayHost
- Add XML doc comments to SignalR hub/adapter, suppress CS1591
- Capture agent page UX directive
- **gateway:** Final extension wiring cleanup
- Remove /extensions/ from gitignore
- Add MCP extension to deploy script
- Add System.IO.Abstractions NuGet packages for testable file I/O
- Archive tool permission model spec — done
- Save session state — agent histories and working files
- Update farnsworth history and phase docs after Wave 3
- **squad:** Record farnsworth wave4 learnings
- Mark DDD refactoring spec as delivered
- **squad:** Update now.md for DDD refactoring delivery
- Mark DDD refactoring spec as done — all phases complete
- Archive completed planning specs to docs/planning/archive
- Mark bug-session-switching-ui spec as delivered
- **webui:** Remove legacy app.js — replaced by new WebUI
- **probe:** Build and run start-probe in Release configuration
- Set feature-subagent-ui-visibility status to in-progress
- **squad:** Update now.md — extension commands delivered
- **squad:** Update now.md — config inheritance delivered
- Mark feature-user-documentation as delivered
- Mark bug-exec-process-disconnect and bug-session-lifecycle-fragmentation as done
- Mark bug-steering-delivery-latency as done
- Mark feature-media-pipeline as delivered
- Mark improvement-subagent-completion-handling as delivered
- Mark improvement-heartbeat-service as delivered
- Mark bug-internal-channel-adapter-missing as delivered
- Mark bug-cross-agent-session-blocking as delivered
- Mark feature-config-management-api as delivered
- Post-delivery consistency fixes for feature-blazor-webui Phase 1
- Post-refactor consistency fixes — stale namespaces, docs, spec status
- Standardize extension manifests + add AGENTS.md rules
- Strip scroll debug logging — autoscroll confirmed working
- Add .txt and .log to gitignore with negation rules
- **webui:** Remove legacy static WebUI and all references
- Update .gitignore to include all files in the site directory
- **.squad:** Log bug-blazor-autoscroll session completion
- **consistency:** Complete design spec success criteria and clarify PID lifecycle comments
- **squad:** Update agent histories and deliver-spec prompt after gateway delivery
- **.squad:** Log gateway detached process delivery session
- **planning:** Add planning specs from worktree sessions
- **planning:** Begin delivery of feature-blazor-subagent-session-view
- **planning:** Mark feature-blazor-subagent-session-view as delivered
- **.squad:** Merge feature-blazor-subagent-session-view delivery artifacts
- **planning:** Mark feature-blazor-subagent-session-view as done
- **merge:** Resolve conflicts merging feature-blazor-subagent-session-view
- **squad:** Log wave 1 design review and reproducing tests
- Align Squad skills and templates with published 0.9.1 (#5)
- **squad:** Add botnexus-issue-workflow skill (#39)
- **scripts:** Revert CLI gateway start delegation, handle deploy directly (#40)

### 🔧 CI/Build

- **squad-heartbeat:** Change schedule from every 30 minutes to every hour (#38)
- **publish:** Switch NuGet push to OIDC-based authentication via NuGet/login@v1 (#104)

### 🛡️ Security

- Guard Swagger, add daily secret scanning workflow, fix gitignore (#2)

### Fix

- Populate model dropdown when opening existing sessions
- Add circuit breaker for repeated blocked tool calls
- Remove unused variable anyToolBlocked

### Scribe

- Full team sprint log — 2026-04-03T07:31:24Z
- Session logs and orchestration records
- Orchestration logs, session log, and decision merge
- Document multi-turn tool calling fix (2026-04-03T22:10:00Z)
- Post-spawn orchestration for Phase 3 design review
- Merge decision inbox → decisions.md
- Phase 3 Wave 1 orchestration logs + decision merge
- Log orchestration for Fry (WebUI fixes) and Bender (models endpoint)
- Log Wave 2 Session Model Orchestration

### Squad

- Session switching bug fully delivered with Playwright E2E

### WebUI

- Add markdown rendering and fix streaming appearance

### Audit

- Deep functional re-audit of AgentCore vs pi-mono agent-loop
- Deep functional re-audit of CodingAgent vs pi-mono
- Deep functional re-audit of providers vs pi-mono

### Build

- Centralize common properties and enable central package management
- **domain:** Upgrade BotNexus.Domain to net10.0

### Debug

- Add diagnostic logging for agent loop and copilot responses
- Add tool setup logging and /tools debug endpoint
- Fix tools endpoint to use supervisor GetHandle
- Extension loading warnings for discovery diagnostics
- Add file logging to bootstrap logger for startup diagnostics
- Log full tool list on handle creation
- Add console logging to scrollActiveToBottom + increase timeout

### Planning

- Add 7 specs from Blazor UI shakedown, archive 2 completed items

### Proposal

- 3-layer provider/model configuration filtering
- Unified config + agent directory architecture
- Cron infrastructure architecture (OpenClaw reference)

### Review

- **nibbler:** Phase 2 sprint consistency review — Good
- **nibbler:** Phase 3 consistency fixes
- **nibbler:** Phase 4 Wave 1 consistency findings — 0 P0, 2 P1 fixed, 5 P2
- Phase 11 design review — grade A-, 0 P0, 6 P1, 5 P2

### Scribe

- Log Hermes gateway validation

### Squad

- Orchestration and session logs for Leela pack.ps1 fix
- Session log for loop alignment & UI fix
- Log session-switching design review orchestration, decisions, and session metadata

[0.1.10]: https://github.com/sytone/botnexus/compare/v0.1.9...v0.1.10
[0.1.9]: https://github.com/sytone/botnexus/compare/v0.1.8...v0.1.9
[0.1.8]: https://github.com/sytone/botnexus/compare/v0.1.7...v0.1.8
[0.1.7]: https://github.com/sytone/botnexus/compare/v0.1.6...v0.1.7
[0.1.6]: https://github.com/sytone/botnexus/compare/v0.1.5...v0.1.6
[0.1.5]: https://github.com/sytone/botnexus/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/sytone/botnexus/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/sytone/botnexus/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/sytone/botnexus/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/sytone/botnexus/compare/v0.1.0...v0.1.1

