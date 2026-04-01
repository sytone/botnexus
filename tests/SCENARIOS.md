# BotNexus E2E Scenario Registry

> **Living document** — the single source of truth for "what do we test end-to-end?"
>
> Maintained by **Zapp** (E2E & Simulation Engineer).
> Last updated: 2026-04-03

---

## Coverage Summary

| Category | Total | ✅ Covered | 🔲 Planned | ⚠️ Partial |
|---|---|---|---|---|
| Multi-Agent Simulation | 8 | 8 | 0 | 0 |
| Agent Workspace & Memory | 10 | 10 | 0 | 0 |
| Dynamic Extension Loading | 8 | 8 | 0 | 0 |
| Deployment Lifecycle | 10 | 10 | 0 | 0 |
| Provider Integration | 7 | 7 | 0 | 0 |
| Channel Integration | 4 | 4 | 0 | 0 |
| Security & Auth | 5 | 5 | 0 | 0 |
| Observability | 4 | 4 | 0 | 0 |
| Cron & Scheduling | 8 | 8 | 0 | 0 |
| **TOTAL** | **64** | **64** | **0** | **0** |

**Coverage: 100% covered. Zero planned. Zero partial.**

---

## 1. Multi-Agent Simulation

E2E tests running 5 agents (Nova, Quill, Bolt, Echo, Sage) through in-process Gateway via `WebApplicationFactory`.

---

### SC-MAS-001: Single-Agent Q&A

- **Category:** Multi-Agent Simulation
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/SingleAgentQaTests.cs`
- **Description:** A single agent receives a question and returns a contextually relevant response.
- **Steps:**
  1. Send "What are the best pizzas?" to Nova via mock-web channel.
  2. Verify response contains "pizza" and at least one known recommendation.
  3. Send location-specific question ("What pizzas should I try in California?").
  4. Verify response contains location-relevant results.

---

### SC-MAS-002: Session State Persistence

- **Category:** Multi-Agent Simulation
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/SessionStateTests.cs`
- **Description:** An agent retains information across multiple turns within the same session.
- **Steps:**
  1. Tell Quill to remember "margherita, pepperoni, hawaiian".
  2. Verify Quill confirms the save.
  3. Ask Quill to recall favorites.
  4. Verify all three items are present in the response.

---

### SC-MAS-003: Cross-Agent Handoff

- **Category:** Multi-Agent Simulation
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/CrossAgentHandoffTests.cs`
- **Description:** Output from one agent is consumed by another agent in a handoff pattern.
- **Steps:**
  1. Ask Nova for California pizza recommendations; capture response.
  2. Pass Nova's response to Quill for saving.
  3. Ask Quill to show notes; verify references to original Nova content.

---

### SC-MAS-004: Multi-Channel Routing

- **Category:** Multi-Agent Simulation
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/MultiChannelRoutingTests.cs`
- **Description:** The same agent processes identical requests from different channels, with responses correctly routed back to each originating channel.
- **Steps:**
  1. Send identical pizza question to Nova via mock-web and mock-api simultaneously.
  2. Wait for responses on both channels.
  3. Verify both contain relevant content.
  4. Verify each response is routed to its originating channel.

---

### SC-MAS-005: Agent Chain (Bolt → Sage → Quill)

- **Category:** Multi-Agent Simulation
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/AgentChainTests.cs`
- **Description:** A multi-step chain where one agent's output feeds the next: compute → summarize → persist.
- **Steps:**
  1. Send math question to Bolt ("What is 15 * 4?"); verify "60".
  2. Send Bolt's result to Sage for summarization; verify "Summary:" format.
  3. Send Sage's summary to Quill for saving; verify "saved".
  4. Ask Quill to show notes; verify chain output is preserved.

---

### SC-MAS-006: Message Integrity

- **Category:** Multi-Agent Simulation
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/MessageIntegrityTests.cs`
- **Description:** Messages with complex formatting, Unicode, and special characters survive the full send→agent→response pipeline without corruption.
- **Steps:**
  1. Send formatted text (bold, italic, code, lists) to Echo via web channel; verify exact reproduction.
  2. Send same via API channel; verify exact reproduction.
  3. Send Unicode (emojis, accented chars, angle brackets); verify exact reproduction.

---

### SC-MAS-007: Concurrent Agents

- **Category:** Multi-Agent Simulation
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/ConcurrentAgentTests.cs`
- **Description:** Multiple agents handle simultaneous requests without cross-talk or response mixing.
- **Steps:**
  1. Send pizza question to Nova and math question to Bolt simultaneously.
  2. Verify Nova's response contains pizza content and NOT math results.
  3. Verify Bolt's response contains math results and NOT pizza content.
  4. Verify responses routed to correct chat IDs.

---

### SC-MAS-008: Agent Routing by Name

- **Category:** Multi-Agent Simulation
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/AgentRoutingTests.cs`
- **Description:** Messages addressed to a specific agent are handled by that agent and no other.
- **Steps:**
  1. Send pizza question addressed to Nova; verify Nova handles it.
  2. Send math question addressed to Bolt; verify Bolt handles it.
  3. Send message to Echo; verify exact echo.
  4. Send to Sage; verify summarization format.
  5. Send to Quill; verify note-saving behavior.

---

## 2. Agent Workspace & Memory

Workspace initialization, context building, and memory tool lifecycle.

---

### SC-AWM-001: Workspace Initialization

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/AgentWorkspaceTests.cs`
- **Description:** First-run workspace setup creates the expected directory structure and stub files.
- **Steps:**
  1. Call `InitializeAsync` on a new workspace path.
  2. Verify directories created: workspace root, memory/, memory/daily/.
  3. Verify stub files created: SOUL.md, IDENTITY.md, USER.md, MEMORY.md, HEARTBEAT.md.
  4. Verify AGENTS.md and TOOLS.md are NOT created (auto-generated at runtime).
  5. Verify stubs contain helpful placeholder content.
  6. Re-initialize; verify existing files are not overwritten.

---

### SC-AWM-002: Context Builder Loads Workspace Files

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/AgentContextBuilderTests.cs`
- **Description:** The context builder incorporates SOUL, IDENTITY, and USER workspace files into the system prompt.
- **Steps:**
  1. Place SOUL.md, IDENTITY.md, USER.md in workspace.
  2. Build system prompt via `BuildSystemPromptAsync`.
  3. Verify each file's content appears in the generated prompt.
  4. Remove optional files; verify prompt still builds without errors.

---

### SC-AWM-003: MEMORY.md Loaded into Context

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/AgentContextBuilderTests.cs`
- **Description:** When auto-load memory is enabled, MEMORY.md content is included in the system prompt.
- **Steps:**
  1. Enable `AutoLoadMemory` in agent config.
  2. Write content to MEMORY.md in workspace.
  3. Build system prompt.
  4. Verify MEMORY.md content is present in prompt.

---

### SC-AWM-004: Daily Memory Files (Today + Yesterday)

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/AgentContextBuilderTests.cs`
- **Description:** Context builder loads today's and yesterday's daily memory files but excludes older ones.
- **Steps:**
  1. Create daily memory files for today, yesterday, and 3 days ago.
  2. Build system prompt.
  3. Verify today's and yesterday's content appear.
  4. Verify older file's content does NOT appear.

---

### SC-AWM-005: Memory Tools Available and Functional

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/MemoryToolsTests.cs`
- **Description:** Memory tools (search, save, get) are available when memory is enabled and produce correct results.
- **Steps:**
  1. Verify tool definitions are correct for all three memory tools.
  2. Save content via `memory_save` to daily and MEMORY targets.
  3. Search via `memory_search`; verify keyword matching, case-insensitivity, recency ordering, context lines.
  4. Get via `memory_get`; verify file retrieval, date filtering, line range selection.
  5. Verify error handling for empty queries and missing files.

---

### SC-AWM-006: Memory Consolidation (Daily → MEMORY.md)

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/MemoryConsolidationE2eTests.cs::MemoryConsolidationE2eTests::Consolidate_WithMultipleDailyFiles_MergesIntoMemoryAndArchives`, `Consolidate_TodaysFile_IsNotProcessed`
- **Description:** Periodic consolidation merges daily memory entries into the persistent MEMORY.md file, summarizing and deduplicating.
- **Steps:**
  1. Accumulate daily memory entries over multiple days.
  2. Trigger consolidation process.
  3. Verify key entries are merged into MEMORY.md.
  4. Verify daily files are archived or cleaned up.

---

### SC-AWM-007: Workspace File Truncation

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/AgentContextBuilderTests.cs`
- **Description:** Workspace files exceeding the configured character limit are truncated with a marker.
- **Steps:**
  1. Write a workspace file exceeding the configured truncation limit.
  2. Build system prompt.
  3. Verify content is truncated to configured limit.
  4. Verify truncation marker is appended.

---

### SC-AWM-008: Auto-Generated AGENTS.md and TOOLS.md

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/AgentContextBuilderTests.cs`
- **Description:** The context builder auto-generates AGENTS.md from agent config and TOOLS.md from the tool registry, injecting them into the system prompt at runtime.
- **Steps:**
  1. Configure multiple agents and register multiple tools.
  2. Build system prompt.
  3. Verify AGENTS.md section lists all configured agents.
  4. Verify TOOLS.md section lists all registered tools in sorted order.

---

### SC-AWM-009: BotNexus Home Directory Initialization

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/HomeDirectoryInitE2eTests.cs::HomeDirectoryInitE2eTests::GatewayStartup_WithCleanHome_CreatesFullDirectoryStructure`, `GatewayStartup_WithCleanHome_CreatesDefaultConfig`, `Initialize_WithBotnexusHome_CreatesPerAgentWorkspaces`
- **Description:** First install creates `~/.botnexus/` directory structure with agents subdirectory and per-agent workspaces. E2E validated with real filesystem via BOTNEXUS_HOME override.
- **Steps:**
  1. Set BOTNEXUS_HOME environment variable.
  2. Start Gateway.
  3. Verify ~/.botnexus/ and agents/ directories created.
  4. Verify per-agent workspace directories initialized.

---

### SC-AWM-010: Memory Store Isolation Between Agents

- **Category:** Agent Workspace & Memory
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/MemoryStoreIsolationE2eTests.cs::MemoryStoreIsolationE2eTests::AgentA_MemoryWrite_IsNotVisibleToAgentB`, `AgentA_DailyMemory_IsIsolatedFromAgentB`, `AgentA_ListKeys_DoesNotIncludeAgentB_Keys`, `AgentA_DeleteKey_DoesNotAffectAgentB_SameKey`, `MEMORY_File_IsIsolatedPerAgent`
- **Description:** Different agents have isolated memory stores — writes by one agent are not visible to another. E2E validated with real filesystem operations via BOTNEXUS_HOME override.
- **Steps:**
  1. Agent A saves a memory entry.
  2. Agent B searches for same keyword.
  3. Verify Agent B does not see Agent A's entry.

---

## 3. Dynamic Extension Loading

Extension discovery, assembly loading, and DI registration.

---

### SC-DEL-001: Extensions Load from Configured Folders

- **Category:** Dynamic Extension Loading
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/ExtensionLoadingE2eTests.cs` → `GatewayStart_WithConfiguredExtensions_LoadsChannelsProvidersToolsAndTheyWork`
- **Description:** Gateway loads provider, channel, and tool extensions from configured folder paths at startup.
- **Steps:**
  1. Deploy fixture extensions to extension folders.
  2. Configure environment with extension paths.
  3. Start Gateway via WebApplicationFactory.
  4. Verify provider, channel, and tool are all loaded and functional via DI.

---

### SC-DEL-002: Missing Extension Folders Handled Gracefully

- **Category:** Dynamic Extension Loading
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/ExtensionLoadingE2eTests.cs` → `GatewayStart_WithPartialExtensions_LoadsAvailableAndLogsWarnings`
- **Description:** When configured extension folders are missing, Gateway logs warnings but starts successfully with available extensions.
- **Steps:**
  1. Configure extensions with some missing folders.
  2. Start Gateway.
  3. Verify available extensions are loaded.
  4. Verify warning logs contain "Extension folder not found".

---

### SC-DEL-003: Registrar-Based Loading

- **Category:** Dynamic Extension Loading
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/ExtensionLoaderTests.cs` → `AddBotNexusExtensions_RegistrarBasedLoading_*`
- **Description:** Extensions implementing `IExtensionRegistrar` perform custom DI registration with full control over service creation.
- **Steps:**
  1. Deploy extension with `IExtensionRegistrar` implementation.
  2. Load via extension loader.
  3. Verify registrar's `Register` method is called with correct services and config section.
  4. Verify registrar-created services are available in DI.

---

### SC-DEL-004: Convention-Based Loading (No Registrar)

- **Category:** Dynamic Extension Loading
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/ExtensionLoaderTests.cs` → `AddBotNexusExtensions_ConventionBasedLoading_*`
- **Description:** Extensions without a registrar are auto-discovered by scanning for known interface implementations (ITool, IChannel, ILlmProvider).
- **Steps:**
  1. Deploy extension implementing ITool/IChannel without a registrar.
  2. Load via extension loader.
  3. Verify implementations are auto-registered in DI via `ActivatorUtilities`.

---

### SC-DEL-005: Extension Security (Path Traversal Rejected)

- **Category:** Dynamic Extension Loading
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/ExtensionLoaderTests.cs` → `AddBotNexusExtensions_PathTraversalAttempt_IsRejected`, `AddBotNexusExtensions_JunctionEscapingRoot_IsRejected`
- **Description:** Extension paths containing traversal sequences (../) or junction/symlink escapes are rejected to prevent loading assemblies from outside the extensions root.
- **Steps:**
  1. Configure extension path with "../../../" traversal.
  2. Attempt to load; verify rejection.
  3. Configure junction/symlink escaping root directory.
  4. Attempt to load; verify rejection.

---

### SC-DEL-006: Provider Loaded and Available via ProviderRegistry

- **Category:** Dynamic Extension Loading
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/ExtensionLoadingE2eTests.cs` → `ProviderSelection_ByModelName_UsesProviderRegistry`
- **Description:** Dynamically loaded providers are registered in the ProviderRegistry and selectable by model name.
- **Steps:**
  1. Deploy two provider fixtures (alpha, beta).
  2. Configure agent to use beta-model.
  3. Start Gateway; send message.
  4. Verify response uses beta provider.

---

### SC-DEL-007: Channel Loaded and Visible in /api/channels

- **Category:** Dynamic Extension Loading
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/ExtensionLoadingE2eTests.cs` → `ChannelApi_IncludesDynamicallyLoadedChannels`
- **Description:** Dynamically loaded channels appear in the /api/channels endpoint alongside built-in channels.
- **Steps:**
  1. Deploy fixture channel extension.
  2. Configure as enabled.
  3. Start Gateway; GET /api/channels.
  4. Verify both "websocket" and "fixture-channel" are listed.

---

### SC-DEL-008: Tool Loaded and Invocable by Agent

- **Category:** Dynamic Extension Loading
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/ExtensionLoadingE2eTests.cs` → `EndToEndMessageFlow_WebSocketToGatewayToAgentWithDynamicProviderToolCallToResponse`
- **Description:** A dynamically loaded tool is invocable by an agent during conversation, with tool results fed back through the LLM loop.
- **Steps:**
  1. Deploy provider and tool fixture extensions.
  2. Start Gateway; connect WebSocket.
  3. Send message triggering tool use.
  4. Verify agent executes tool and returns provider-processed tool result.

---

## 4. Deployment Lifecycle

Real-process deployment scenarios — start, stop, restart, configuration, persistence. These require process-level testing (`dotnet run`), not in-process `WebApplicationFactory`.

---

### SC-DPL-001: First Install — Home Directory Created

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/FirstInstallTests.cs`
- **Description:** On first launch, Gateway creates `~/.botnexus/` directory structure if it doesn't exist.
- **Steps:**
  1. Set BOTNEXUS_HOME to a clean temp directory.
  2. Start Gateway process.
  3. Verify `~/.botnexus/` directory exists with expected structure.
  4. Stop Gateway.

---

### SC-DPL-002: Clean Gateway Start — Health/Ready 200

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/CleanStartTests.cs`
- **Description:** A freshly started Gateway returns 200 from /health and /ready endpoints.
- **Steps:**
  1. Start Gateway process with minimal config.
  2. Poll /health until 200 or timeout.
  3. Verify /ready returns 200.
  4. Stop Gateway.

---

### SC-DPL-003: Configure Agents via config.json

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/AgentConfigurationTests.cs`
- **Description:** Agent configuration in config file is reflected in running Gateway behavior.
- **Steps:**
  1. Create config with 2 named agents.
  2. Start Gateway.
  3. Send message to each agent; verify correct behavior.
  4. Stop Gateway.

---

### SC-DPL-004: Graceful Stop — Sessions Persisted

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/GracefulShutdownTests.cs`
- **Description:** Graceful Gateway shutdown persists all active sessions to storage.
- **Steps:**
  1. Start Gateway; conduct conversation creating session state.
  2. Send SIGTERM / stop gracefully.
  3. Verify session files exist on disk.

---

### SC-DPL-005: Restart — Sessions Restored

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/RestartPersistenceTests.cs`
- **Description:** After restart, previously persisted sessions are restored and conversation can continue.
- **Steps:**
  1. Start Gateway; create session state; stop gracefully.
  2. Restart Gateway.
  3. Continue conversation; verify prior context is retained.

---

### SC-DPL-006: Add Extension → Restart → Loaded

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/ExtensionAddTests.cs`
- **Description:** Adding an extension folder and restarting causes the new extension to be loaded.
- **Steps:**
  1. Start Gateway with no extensions; verify clean state.
  2. Stop Gateway.
  3. Deploy extension to extensions folder; update config.
  4. Restart Gateway.
  5. Verify new extension is loaded and functional.

---

### SC-DPL-007: Remove Extension → Restart → Gone

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/ExtensionRemoveTests.cs`
- **Description:** Removing an extension folder and restarting causes the extension to no longer be available.
- **Steps:**
  1. Start Gateway with extension loaded.
  2. Stop Gateway.
  3. Remove extension folder; update config.
  4. Restart Gateway.
  5. Verify extension is no longer available.

---

### SC-DPL-008: Config Change → Restart → Applied

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/ConfigChangeTests.cs`
- **Description:** Configuration changes take effect after Gateway restart.
- **Steps:**
  1. Start Gateway with initial config.
  2. Stop Gateway; modify config (e.g., change default agent).
  3. Restart Gateway.
  4. Verify new configuration is active.

---

### SC-DPL-009: Health/Ready Probes During Startup

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/HealthDuringStartupTests.cs`
- **Description:** Health and ready probes behave correctly during the startup sequence — ready should return non-200 until initialization completes.
- **Steps:**
  1. Start Gateway.
  2. Immediately poll /health and /ready.
  3. Verify /health returns 200 quickly (liveness).
  4. Verify /ready transitions from not-ready to ready.

---

### SC-DPL-010: Concurrent Message Handling Under Load

- **Category:** Deployment Lifecycle
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Deployment/Tests/ConcurrentHandlingTests.cs`
- **Description:** Gateway handles multiple concurrent messages without failures, deadlocks, or lost messages.
- **Steps:**
  1. Start Gateway with a configured agent.
  2. Send N concurrent messages (e.g., 50).
  3. Verify all N responses received.
  4. Verify no errors in logs.

---

## 5. Provider Integration

LLM provider connectivity, authentication, and model routing.

---

### SC-PRV-001: Copilot OAuth Device Code Flow Initiation

- **Category:** Provider Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/CopilotProviderTests.cs` → `DeviceCodeFlow_PollsUntilTokenReturned`
- **Description:** Copilot provider initiates OAuth device code flow and polls until token is returned.
- **Steps:**
  1. Mock OAuth device code endpoint.
  2. Trigger authentication.
  3. Verify polling until token received.

---

### SC-PRV-002: Copilot Chat Completion (Mocked)

- **Category:** Provider Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/CopilotProviderTests.cs` → `ChatAsync_ReturnsCompletionPayload`
- **Description:** Copilot provider sends chat completion request and correctly parses the response.
- **Steps:**
  1. Mock Copilot API completion endpoint.
  2. Send chat request.
  3. Verify response parsed correctly.

---

### SC-PRV-003: Copilot Streaming (Mocked)

- **Category:** Provider Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/CopilotProviderTests.cs` → `ChatStreamAsync_ParsesSseChunks`
- **Description:** Copilot provider streams SSE chunks and yields them as async enumerable.
- **Steps:**
  1. Mock streaming SSE endpoint.
  2. Call `ChatStreamAsync`.
  3. Verify chunks are parsed and yielded correctly.

---

### SC-PRV-004: Copilot Tool Calling (Mocked)

- **Category:** Provider Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/CopilotProviderTests.cs` → `ChatAsync_MapsToolCalls`
- **Description:** Copilot provider correctly parses and maps tool calls from API response.
- **Steps:**
  1. Mock API response with tool call payload.
  2. Send chat request.
  3. Verify tool calls are correctly parsed with names and arguments.

---

### SC-PRV-005: Token Caching and Refresh

- **Category:** Provider Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/CopilotProviderTests.cs` → `ChatAsync_CachesOAuthTokenAcrossCalls`, `ExpiredToken_TriggersReauthentication`
- **Description:** OAuth token is cached across API calls and re-authentication is triggered when the token expires.
- **Steps:**
  1. Authenticate and make API call; verify token cached.
  2. Make second call; verify no re-authentication.
  3. Expire token; make another call; verify re-authentication triggered.

---

### SC-PRV-006: Provider Selection by Model Name

- **Category:** Provider Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/ExtensionLoadingE2eTests.cs` → `ProviderSelection_ByModelName_UsesProviderRegistry`
- **Description:** Agent selects the correct provider based on model name from the ProviderRegistry.
- **Steps:**
  1. Register multiple providers (alpha, beta).
  2. Configure agent with beta-model.
  3. Send message.
  4. Verify beta provider is used.

---

### SC-PRV-007: Multiple Providers Registered Simultaneously

- **Category:** Provider Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/MultiProviderE2eTests.cs::MultiProviderE2eTests::MultipleProviders_RegisteredAndRetrievable_ByCaseInsensitiveName`, `MultipleProviders_InGateway_AgentsRouteToCorrectProvider`, `DifferentProviders_ReturnDifferentResponses`
- **Description:** Multiple providers can be registered and retrieved from the ProviderRegistry concurrently. E2E validated with WebApplicationFactory and multiple provider instances.
- **Steps:**
  1. Register multiple providers.
  2. Retrieve each by name; verify correct provider returned.
  3. Verify case-insensitive lookup works.
  4. Load multiple provider extensions and route different agents to different providers.

---

## 6. Channel Integration

Channel connectivity, message routing, and protocol-specific behavior.

---

### SC-CHN-001: WebSocket Channel Message Flow

- **Category:** Channel Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/WebSocketChannelTests.cs`, `tests/BotNexus.Tests.Integration/Tests/ExtensionLoadingE2eTests.cs` → `EndToEndMessageFlow_*`
- **Description:** WebSocket channel correctly handles connections, routes messages to the correct connection, and supports streaming deltas.
- **Steps:**
  1. Start WebSocket channel.
  2. Add connection; verify reader returned.
  3. Send message; verify JSON written to correct connection.
  4. Send delta; verify delta JSON written to correct connection.
  5. Remove connection; verify reader completed.
  6. (Integration) Full E2E: WebSocket → Gateway → Agent → Tool → Response.

---

### SC-CHN-002: Slack Webhook Endpoint

- **Category:** Channel Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/SlackWebhookE2eTests.cs::SlackWebhookE2eTests::SlackWebhook_UrlVerification_ReturnsChallengeViaGateway`, `SlackWebhook_ValidEventCallback_Returns200`, `SlackWebhook_InvalidSignature_Returns401ViaGateway`, `SlackWebhook_EventCallback_PublishesToMessageBus`
- **Description:** Slack webhook handler validates signatures, responds to URL verification challenges, and publishes inbound messages. E2E validated through full Gateway pipeline via WebApplicationFactory.
- **Steps:**
  1. Send URL verification event; verify challenge response.
  2. Send event callback with valid signature; verify inbound message published.
  3. Send event with invalid signature; verify rejection.
  4. Full E2E through Gateway Slack endpoint.

---

### SC-CHN-003: Multi-Channel Same-Message Routing

- **Category:** Channel Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/MultiChannelRoutingTests.cs`
- **Description:** The same message sent via different channels produces correct responses on each channel without mixing.
- **Steps:**
  1. Send identical message via mock-web and mock-api channels.
  2. Verify both channels receive responses.
  3. Verify responses are routed to correct originating channels.

---

### SC-CHN-004: Channel-Specific Config (Enable/Disable, Allow Lists)

- **Category:** Channel Integration
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/ChannelConfigE2eTests.cs::ChannelConfigE2eTests::Channel_WithEmptyAllowList_AcceptsAllSenders`, `Channel_WithAllowList_RejectsUnlistedSenders`, `Channel_AllowList_BlocksMessagePublishing`, `Channel_StartStop_TogglesIsRunning`, `Channel_DisabledByNotStarting_IsNotRunning`
- **Description:** Channels can be enabled/disabled via configuration, and allow lists restrict which users can interact. Validated with BaseChannel subclass, real MessageBus, and allow-list enforcement.
- **Steps:**
  1. Configure channel as disabled; verify it is not started.
  2. Configure channel with allow list; send message from allowed user; verify accepted.
  3. Send message from non-allowed user; verify rejected.

---

## 7. Security & Auth

Authentication, authorization, and security boundary enforcement.

---

### SC-SEC-001: API Key Auth on REST Endpoints

- **Category:** Security & Auth
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/GatewayApiKeyAuthTests.cs` → `ApiEndpoints_AllowRequest_WithValidHeaderApiKey`, `ApiEndpoints_ReturnUnauthorized_WhenApiKeyMissing`
- **Description:** REST endpoints require valid API key in X-Api-Key header when configured; return 401 with error message when missing.
- **Steps:**
  1. Configure API key "test-api-key".
  2. Send request with valid key; verify 200.
  3. Send request without key; verify 401 with error body.

---

### SC-SEC-002: API Key Auth on WebSocket

- **Category:** Security & Auth
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/GatewayApiKeyAuthTests.cs` → `WebSocketEndpoint_AcceptsApiKeyFromQuery`
- **Description:** WebSocket endpoint accepts API key via query parameter.
- **Steps:**
  1. Configure API key.
  2. Connect to /ws?apiKey=test-api-key.
  3. Verify connection accepted (400 returned for non-WebSocket request — confirms key was accepted before upgrade check).

---

### SC-SEC-003: Health Endpoint Unauthenticated

- **Category:** Security & Auth
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/GatewayApiKeyAuthTests.cs` → `HealthEndpoint_BypassesAuthentication`, `ReadyEndpoint_BypassesAuthentication`
- **Description:** /health and /ready endpoints bypass authentication even when API key is configured.
- **Steps:**
  1. Configure API key.
  2. Send request to /health without key; verify 200.
  3. Send request to /ready without key; verify NOT 401.

---

### SC-SEC-004: Dev Mode (No Key Configured)

- **Category:** Security & Auth
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/GatewayApiKeyAuthTests.cs` → `ApiEndpoints_AllowUnauthenticatedRequest_WhenApiKeyNotConfigured`
- **Description:** When no API key is configured (dev mode), all endpoints allow unauthenticated access.
- **Steps:**
  1. Start Gateway with no API key configured.
  2. Send request to /api/channels without auth header.
  3. Verify 200 returned.

---

### SC-SEC-005: Extension Assembly Validation

- **Category:** Security & Auth
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Unit/Tests/ExtensionLoaderTests.cs` → `*_InvalidAssembly_*`, `*_RequireSignedAssemblies_*`, `*_MaxAssembliesPerExtension_*`, `*_AssemblyLoadContextIsolation_*`
- **Description:** Extension loading validates assemblies — rejects invalid/corrupted assemblies, enforces signing requirements, limits assembly count, and isolates extensions in separate AssemblyLoadContexts.
- **Steps:**
  1. Load invalid assembly; verify graceful rejection.
  2. Enable RequireSignedAssemblies; load unsigned; verify rejection.
  3. Exceed max assemblies per extension; verify enforcement.
  4. Load extension; verify types are isolated in separate AssemblyLoadContext.

---

## 8. Observability

Health checks, readiness probes, correlation IDs, and metrics.

---

### SC-OBS-001: /health Returns Meaningful Checks

- **Category:** Observability
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/GatewayApiKeyAuthTests.cs` → `HealthEndpoint_BypassesAuthentication`
- **Description:** /health endpoint returns 200 with a body containing health check results.
- **Steps:**
  1. Start Gateway.
  2. GET /health.
  3. Verify 200 with "checks" property in response body.

---

### SC-OBS-002: /ready Returns Readiness Status

- **Category:** Observability
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/GatewayApiKeyAuthTests.cs` → `ReadyEndpoint_BypassesAuthentication`
- **Description:** /ready endpoint returns readiness status indicating Gateway is ready to serve traffic.
- **Steps:**
  1. Start Gateway.
  2. GET /ready.
  3. Verify non-401 response (readiness or initializing).

---

### SC-OBS-003: Correlation IDs Flow Through Pipeline

- **Category:** Observability
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/CorrelationIdE2eTests.cs::CorrelationIdE2eTests::EnsureCorrelationId_GeneratesNewId_WhenMissing`, `EnsureCorrelationId_PreservesExistingId_WhenPresent`, `EnsureCorrelationId_CalledMultipleTimes_ReturnsConsistentId`, `GetCorrelationId_ReturnsNull_WhenNoCorrelationId`, `GetCorrelationId_HandlesNonStringValue`, `CorrelationId_SurvivesMessageRecordClone`, `CorrelationId_IsUniquePerMessage`, `CorrelationId_FlowsThroughMetadataDictionary`
- **Description:** Each request generates or propagates a correlation ID that flows through the entire request pipeline (middleware → agent loop → tool calls → response). Validated via InboundMessage metadata extensions.
- **Steps:**
  1. Send request with X-Correlation-ID header.
  2. Verify correlation ID appears in logs.
  3. Verify correlation ID is returned in response headers.
  4. Verify correlation ID flows to tool call context.

---

### SC-OBS-004: Metrics Emitted

- **Category:** Observability
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.Integration/Tests/MetricsE2eTests.cs::MetricsE2eTests::MessagesProcessed_Counter_EmitsWithChannelTag`, `ToolCallsExecuted_Counter_EmitsWithToolTag`, `ProviderLatency_Histogram_EmitsWithProviderTag`, `ExtensionsLoaded_Gauge_ReflectsCurrentCount`, `CronJobsExecuted_Counter_EmitsWithJobTag`, `CronJobsFailed_Counter_EmitsWithJobTag`, `CronJobDuration_Histogram_EmitsWithJobTag`, `CronJobsSkipped_Counter_EmitsWithJobAndReasonTags`
- **Description:** Gateway emits metrics for key operational signals: message counts, tool call counts, latency, extension load counts, and cron job counters. Validated via MeterListener capturing System.Diagnostics.Metrics instruments.
- **Steps:**
  1. Start Gateway with metrics endpoint enabled.
  2. Send messages, trigger tool calls.
  3. Scrape metrics endpoint.
  4. Verify counters for messages, tool calls, latency histograms, and extension counts.

---

## 9. Cron & Scheduling

E2E tests for the cron system covering the full lifecycle: config → startup → scheduled execution → channel output. Uses `WebApplicationFactory` with cron enabled, mock channels, and deterministic LLM provider.

---

### SC-CRN-001: Jobs Registered at Startup

- **Category:** Cron & Scheduling
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/CronTests.cs`
- **Description:** Gateway starts with cron jobs configured → all jobs (agent, system, maintenance, legacy-migrated) are registered and visible via GET /api/cron.
- **Steps:**
  1. Start Gateway with 4 central cron jobs + 1 legacy AgentConfig cron job.
  2. GET /api/cron.
  3. Verify all 5 expected jobs appear with correct names, schedules, and enabled flags.

---

### SC-CRN-002: Agent Cron Job Fires and Routes to Channel

- **Category:** Cron & Scheduling
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/CronTests.cs`
- **Description:** Agent cron job fires → prompt sent through agent → response routed to mock channel. Full pipeline: CronService trigger → AgentRunner → LLM → session history → OutputChannel routing.
- **Steps:**
  1. POST /api/cron/nova-briefing/trigger.
  2. Wait for mock-web channel to receive the routed response.
  3. Verify response is non-empty, channel is "mock-web", metadata contains source=cron.
  4. Verify execution recorded in history with success=true.

---

### SC-CRN-003: System Cron Job Executes Action

- **Category:** Cron & Scheduling
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/CronTests.cs`
- **Description:** System cron job fires → action executed → result recorded in history. The health-audit action runs HealthCheckService and reports status.
- **Steps:**
  1. POST /api/cron/system:health-audit/trigger.
  2. Poll GET /api/cron/system:health-audit until history appears.
  3. Verify execution success=true and output contains "health-audit".

---

### SC-CRN-004: Maintenance Cron Job Consolidates Memory

- **Category:** Cron & Scheduling
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/CronTests.cs`
- **Description:** Maintenance cron job fires → memory consolidation triggered for configured agents. MockMemoryConsolidator verifies the call was made.
- **Steps:**
  1. POST /api/cron/maintenance:consolidate-memory/trigger.
  2. Poll history until execution appears.
  3. Verify success=true and output references the "nova" agent.
  4. Assert MockMemoryConsolidator.ConsolidatedAgents contains "nova".

---

### SC-CRN-005: Manual Trigger Executes Immediately

- **Category:** Cron & Scheduling
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/CronTests.cs`
- **Description:** Manual trigger via POST /api/cron/{name}/trigger → job executes immediately outside its schedule. Validates response shape and history recording.
- **Steps:**
  1. POST /api/cron/system:check-updates/trigger.
  2. Verify response contains triggered=true and correct jobName.
  3. Poll history to confirm execution was recorded.

---

### SC-CRN-006: Enable/Disable via API

- **Category:** Cron & Scheduling
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/CronTests.cs`
- **Description:** Enable/disable via PUT /api/cron/{name}/enable → disabled jobs reflect correct state. Tests the full cycle: disable → verify → re-enable → verify.
- **Steps:**
  1. PUT /api/cron/system:check-updates/enable with {enabled: false}.
  2. Verify response shows enabled=false.
  3. GET /api/cron/system:check-updates → confirm disabled.
  4. PUT enable with {enabled: true} → verify re-enabled.
  5. GET again → confirm enabled=true.

---

### SC-CRN-007: Execution History with Correct Fields

- **Category:** Cron & Scheduling
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/CronTests.cs`
- **Description:** GET /api/cron/history returns execution records with all required fields: jobName, correlationId, startedAt, completedAt, success.
- **Steps:**
  1. Trigger a job to ensure history exists.
  2. GET /api/cron/history?limit=50.
  3. Verify entries are non-empty.
  4. Find a health-audit entry; validate all fields are present and non-empty.

---

### SC-CRN-008: Legacy AgentConfig.CronJobs Migration

- **Category:** Cron & Scheduling
- **Status:** ✅ Covered
- **Test location:** `tests/BotNexus.Tests.E2E/Tests/CronTests.cs`
- **Description:** Legacy AgentConfig.CronJobs migration → old per-agent config converted to central cron jobs. The deprecated echo agent CronJobs list is migrated at startup.
- **Steps:**
  1. Configure echo agent with legacy CronJobs[0] in config.
  2. Start Gateway (CronJobFactory performs migration).
  3. GET /api/cron → verify "echo" job exists with correct schedule and enabled=true.

---

## Appendix: Test File Index

Quick reference mapping test files to the scenarios they cover.

| Test File | Scenarios |
|---|---|
| `tests/BotNexus.Tests.E2E/Tests/SingleAgentQaTests.cs` | SC-MAS-001 |
| `tests/BotNexus.Tests.E2E/Tests/SessionStateTests.cs` | SC-MAS-002 |
| `tests/BotNexus.Tests.E2E/Tests/CrossAgentHandoffTests.cs` | SC-MAS-003 |
| `tests/BotNexus.Tests.E2E/Tests/MultiChannelRoutingTests.cs` | SC-MAS-004, SC-CHN-003 |
| `tests/BotNexus.Tests.E2E/Tests/AgentChainTests.cs` | SC-MAS-005 |
| `tests/BotNexus.Tests.E2E/Tests/MessageIntegrityTests.cs` | SC-MAS-006 |
| `tests/BotNexus.Tests.E2E/Tests/ConcurrentAgentTests.cs` | SC-MAS-007 |
| `tests/BotNexus.Tests.E2E/Tests/AgentRoutingTests.cs` | SC-MAS-008 |
| `tests/BotNexus.Tests.Unit/Tests/AgentWorkspaceTests.cs` | SC-AWM-001 |
| `tests/BotNexus.Tests.Unit/Tests/AgentContextBuilderTests.cs` | SC-AWM-002, SC-AWM-003, SC-AWM-004, SC-AWM-007, SC-AWM-008 |
| `tests/BotNexus.Tests.Unit/Tests/MemoryToolsTests.cs` | SC-AWM-005 |
| `tests/BotNexus.Tests.Integration/Tests/MemoryConsolidationE2eTests.cs` | SC-AWM-006 |
| `tests/BotNexus.Tests.Unit/Tests/BotNexusHomeTests.cs` | SC-AWM-009 (unit) |
| `tests/BotNexus.Tests.Integration/Tests/HomeDirectoryInitE2eTests.cs` | SC-AWM-009 (E2E) |
| `tests/BotNexus.Tests.Unit/Tests/MemoryStoreTests.cs` | SC-AWM-010 (unit) |
| `tests/BotNexus.Tests.Integration/Tests/MemoryStoreIsolationE2eTests.cs` | SC-AWM-010 (E2E) |
| `tests/BotNexus.Tests.Integration/Tests/ExtensionLoadingE2eTests.cs` | SC-DEL-001, SC-DEL-002, SC-DEL-006, SC-DEL-007, SC-DEL-008 |
| `tests/BotNexus.Tests.Unit/Tests/ExtensionLoaderTests.cs` | SC-DEL-003, SC-DEL-004, SC-DEL-005 |
| `tests/BotNexus.Tests.Unit/Tests/CopilotProviderTests.cs` | SC-PRV-001, SC-PRV-002, SC-PRV-003, SC-PRV-004, SC-PRV-005 |
| `tests/BotNexus.Tests.Unit/Tests/ProviderRetryTests.cs` | SC-PRV-007 (unit) |
| `tests/BotNexus.Tests.Integration/Tests/MultiProviderE2eTests.cs` | SC-PRV-007 (E2E) |
| `tests/BotNexus.Tests.Unit/Tests/WebSocketChannelTests.cs` | SC-CHN-001 |
| `tests/BotNexus.Tests.Unit/Tests/SlackWebhookHandlerTests.cs` | SC-CHN-002 (unit) |
| `tests/BotNexus.Tests.Integration/Tests/SlackWebhookE2eTests.cs` | SC-CHN-002 (E2E) |
| `tests/BotNexus.Tests.Integration/Tests/ChannelConfigE2eTests.cs` | SC-CHN-004 |
| `tests/BotNexus.Tests.Integration/Tests/GatewayApiKeyAuthTests.cs` | SC-SEC-001, SC-SEC-002, SC-SEC-003, SC-SEC-004, SC-OBS-001, SC-OBS-002 |
| `tests/BotNexus.Tests.Integration/Tests/CorrelationIdE2eTests.cs` | SC-OBS-003 |
| `tests/BotNexus.Tests.Integration/Tests/MetricsE2eTests.cs` | SC-OBS-004 |
| `tests/BotNexus.Tests.Deployment/Tests/FirstInstallTests.cs` | SC-DPL-001 |
| `tests/BotNexus.Tests.Deployment/Tests/CleanStartTests.cs` | SC-DPL-002 |
| `tests/BotNexus.Tests.Deployment/Tests/AgentConfigurationTests.cs` | SC-DPL-003 |
| `tests/BotNexus.Tests.Deployment/Tests/GracefulShutdownTests.cs` | SC-DPL-004 |
| `tests/BotNexus.Tests.Deployment/Tests/RestartPersistenceTests.cs` | SC-DPL-005 |
| `tests/BotNexus.Tests.Deployment/Tests/ExtensionAddTests.cs` | SC-DPL-006 |
| `tests/BotNexus.Tests.Deployment/Tests/ExtensionRemoveTests.cs` | SC-DPL-007 |
| `tests/BotNexus.Tests.Deployment/Tests/ConfigChangeTests.cs` | SC-DPL-008 |
| `tests/BotNexus.Tests.Deployment/Tests/HealthDuringStartupTests.cs` | SC-DPL-009 |
| `tests/BotNexus.Tests.Deployment/Tests/ConcurrentHandlingTests.cs` | SC-DPL-010 |
| `tests/BotNexus.Tests.E2E/Tests/CronTests.cs` | SC-CRN-001, SC-CRN-002, SC-CRN-003, SC-CRN-004, SC-CRN-005, SC-CRN-006, SC-CRN-007, SC-CRN-008 |
