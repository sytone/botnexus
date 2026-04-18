# DDD Refactoring - Research

Supporting analysis for the [design spec](design-spec.md). Contains the codebase survey, domain-to-code mapping, duplication analysis, and findings that informed the refactoring plan.

---

## Codebase Structure

### Solution layout (25 projects + 18 test projects)

**Core:**
- `BotNexus.Agent.Core` - agent loop runner, tool execution, message conversion, streaming
- `BotNexus.Gateway.Abstractions` - shared contracts (models, interfaces, hooks, security)
- `BotNexus.Gateway` - gateway implementation (agents, routing, sessions, config, isolation, tools)
- `BotNexus.Gateway.Api` - ASP.NET API layer (controllers, hubs, middleware)
- `BotNexus.Gateway.Sessions` - session store implementations (InMemory, File, SQLite)

**Channels:**
- `BotNexus.Channels.Core` - base adapter, channel manager
- `BotNexus.Channels.Telegram` - Telegram bot adapter
- `BotNexus.Channels.Tui` - terminal UI adapter

**Providers:**
- `BotNexus.Agent.Providers.Core` - LLM abstraction (models, streaming, context)
- `BotNexus.Agent.Providers.Anthropic` - Claude provider
- `BotNexus.Agent.Providers.Copilot` - GitHub Copilot provider
- `BotNexus.Agent.Providers.OpenAI` - OpenAI completions + responses
- `BotNexus.Agent.Providers.OpenAICompat` - generic OpenAI-compatible endpoints

**Tools:**
- `BotNexus.Tools` - core file/shell tools (read, write, edit, grep, glob, ls, shell)
- `BotNexus.Memory` - semantic memory (SQLite store, search, indexing)

**Extensions:**
- `BotNexus.Extensions.Mcp` - MCP server integration
- `BotNexus.Extensions.McpInvoke` - MCP tool invocation
- `BotNexus.Extensions.Skills` - skill loading system
- `BotNexus.Extensions.ExecTool` - exec/process tools
- `BotNexus.Extensions.WebTools` - web fetch/search
- `BotNexus.Extensions.ProcessTool` - background process management

**Other:**
- `BotNexus.Cron` - cron scheduler, job store, tools
- `BotNexus.Cli` - CLI commands (agent, config, init, validate)
- `BotNexus.CodingAgent` - standalone coding agent (separate from gateway)
- `BotNexus.WebUI` - Blazor web UI

---

## Key Contract Analysis

### GatewaySession (the main session type)

**Location**: `Gateway.Abstractions/Models/GatewaySession.cs`

**Current properties:**
- SessionId, AgentId, ChannelType, CallerId
- CreatedAt, UpdatedAt, Status, ExpiresAt
- History (List<SessionEntry>), Metadata
- NextSequenceId, StreamEventLog, ReplayBuffer

**Missing from domain model:**
- No Participants (only CallerId string)
- No IsInteractive flag
- No SessionType discrimination
- No "Sealed" status (uses "Closed")
- Replay buffer and streaming state mixed in with domain state

**Thread safety**: Has internal `_historyLock` and `_replayBuffer` - infrastructure concerns baked into the domain type.

### SessionStatus enum

```csharp
Active, Suspended, Expired, Closed
```

Domain model says: Active, Suspended, Sealed. "Expired" maps to a retention policy action (could trigger Sealed). "Closed" should be renamed to "Sealed".

### AgentDescriptor

**Location**: `Gateway.Abstractions/Models/AgentDescriptor.cs`

Maps well to the domain Agent Descriptor. Has all the right properties but they're flat (not grouped). No structured Identity type - identity comes from loading IDENTITY.md as a system prompt file.

### AgentInstance

**Location**: `Gateway.Abstractions/Models/AgentInstance.cs`

Clean. InstanceId, AgentId, SessionId, Status, CreatedAt, LastActiveAt, IsolationStrategy. Aligns with domain model.

### IChannelAdapter

**Location**: `Gateway.Abstractions/Channels/IChannelAdapter.cs`

Well-designed interface with capability flags (SupportsStreaming, SupportsSteering, etc.). The problem isn't the interface - it's that CronChannelAdapter implements it when cron isn't a channel.

### CronChannelAdapter

**Location**: `Gateway.Api/Hubs/CronChannelAdapter.cs`

Implements `IChannelAdapter` to create sessions from cron jobs. This works but violates the domain model's distinction between channels (external communication) and internal triggers.

### DefaultSubAgentManager

**Location**: `Gateway/Agents/DefaultSubAgentManager.cs`

Key finding - the identity theft:
```csharp
var childAgentId = request.ParentAgentId;  // line ~65
var handle = await _supervisor.GetOrCreateAsync(childAgentId, childSessionId, ct);
```

The sub-agent runs as the parent agent. This means:
- Same system prompt, same tools, same access
- Indistinguishable in logs and session history
- Security audit can't tell parent from child

The domain model says workers should get archetype-based identities.

### DefaultMessageRouter

**Location**: `Gateway/Routing/DefaultMessageRouter.cs`

Priority chain: explicit target -> session binding -> default agent. Clean and simple. No changes needed for the domain model alignment.

### ISessionStore

**Location**: `Gateway.Abstractions/Sessions/ISessionStore.cs`

Methods: Get, GetOrCreate, Save, Delete, Archive, List, ListByChannel.

Missing: No way to query sessions where an agent is a participant (not owner). This is needed for Existence dual-lookup.

### PlatformConfig

**Location**: `Gateway/Configuration/PlatformConfig.cs`

Heavy duplication - every setting exists at root level AND under `Gateway` with `Get*()` methods. AgentDefinitionConfig, ProviderConfig, ChannelConfig, CronConfig all defined here. This is a configuration concern, not a domain concern, but the duplication adds noise.

---

## Duplication & Refactor Opportunities

**Key constraint**: BotNexus.Gateway must NEVER depend on BotNexus.CodingAgent. They are peer products. When deduplicating shared logic, extract it DOWN into common libraries (BotNexus.Domain, BotNexus.Agent.Core, BotNexus.Agent.Providers.Core, etc.) that both can reference.

### 1. Duplicate SystemPromptBuilder (HIGH)

Two completely separate system prompt builders exist:
- `BotNexus.Gateway/Agents/SystemPromptBuilder.cs` (572 lines) - gateway agents
- `BotNexus.CodingAgent/SystemPromptBuilder.cs` (283 lines) - coding agent

These share the same *concept* (build a system prompt from context) but have diverged into separate implementations with different parameter types (`SystemPromptParams` vs `SystemPromptContext`), different context file types (`ContextFile` vs `PromptContextFile`), and different section builders.

**Refactor opportunity**: Extract shared prompt building primitives DOWN into a new `BotNexus.Prompts` library. AgentCore should stay lean and generic - it is the foundation that Gateway and CodingAgent build upon, not a place for shared prompt logic. Both gateway and coding agent compose from this shared layer with their own product-specific sections. Neither product references the other.

### 2. Duplicate SessionManager (HIGH)

Two completely separate session management systems:
- `BotNexus.Gateway.Sessions/` (FileSessionStore, SqliteSessionStore, InMemorySessionStore) - gateway sessions using `GatewaySession`
- `BotNexus.CodingAgent/Session/SessionManager.cs` (759 lines) - coding agent sessions using JSONL files with `SessionInfo`

These are solving the same problem (persist conversation history) with different data models and storage formats. The coding agent's `SessionManager` has branching/compaction features that the gateway stores don't, but the core CRUD is duplicated.

**Refactor opportunity**: If the coding agent is being brought under the gateway umbrella, unify on the gateway's `ISessionStore` abstraction. If they remain separate products, extract shared session primitives (JSONL format, common types) into a common library. The gateway must never take a dependency on CodingAgent - shared logic goes into common libraries that both reference.

### 3. NormalizeChannelKey - triplicated (MEDIUM)

The exact same helper method appears in all three session store implementations:
- `FileSessionStore.NormalizeChannelKey()`
- `InMemorySessionStore.NormalizeChannelKey()`  
- `SqliteSessionStore.NormalizeChannelKey()`

**Refactor opportunity**: Eliminated entirely by introducing a `ChannelKey` value object (see Primitive Obsession section). The value object normalizes at construction - no helper needed anywhere.

### 4. NormalizeToolCallId - duplicated (MEDIUM)

Tool call ID normalization exists in two providers:
- `OpenAICompletionsProvider.NormalizeToolCallId()` - normalizes to 40 chars, strips non-alphanumeric
- `AnthropicMessageConverter.NormalizeToolCallId()` - normalizes to 64 chars, strips non-alphanumeric

Similar logic, different max lengths. Both use `NonAlphanumericRegex`.

**Refactor opportunity**: Introduce a `ToolCallId` value object that normalizes at construction with a provider-specific max length. Or use a scoped extension method. The providers share this concern (making tool call IDs safe for each API) but solve it independently with static helpers.

### 5. NormalizeLineEndings - duplicated (LOW)

`EditTool.NormalizeLineEndings()` and `ReadTool.NormalizeLineEndings()` are identical. Both in the same project (`BotNexus.Tools`).

**Refactor opportunity**: Make this an extension method on `string` in the `BotNexus.Tools` namespace, not a static utility class. Extension methods are the right C# pattern for adding behavior to existing types without wrapping them.

### 6. OpenAI Completions vs Compat Provider (MEDIUM)

`OpenAICompletionsProvider` (1291 lines) and `OpenAICompatProvider` (652 lines) share significant streaming/parsing logic for the OpenAI chat completions format. The compat provider is essentially a simplified version of the completions provider with different endpoint/auth handling.

**Refactor opportunity**: Extract common OpenAI completions streaming/parsing into a shared base or utility class. The compat provider could delegate the response parsing to the same code the main provider uses, with just auth/endpoint differences.

### 7. PlatformConfig legacy duplication (MEDIUM)

Every config property exists twice - at root level and under `Gateway.*`:
```csharp
public string? ListenUrl { get; set; }           // root
public string? GetListenUrl() => Gateway?.ListenUrl ?? ListenUrl;  // resolver
```

13 properties duplicated this way, plus 13 `Get*()` resolver methods.

**Refactor opportunity**: One-time migration to move all settings under the `Gateway` section. Add a config migration step that rewrites old-format configs automatically. Remove the root-level properties and resolvers.

### 8. Gateway.Abstractions is a grab bag (HIGH)

`BotNexus.Gateway.Abstractions` contains:
- Domain models (AgentDescriptor, GatewaySession, SessionEntry, Messages, etc.)
- Service interfaces (IAgentRegistry, ISessionStore, IChannelAdapter, etc.)
- Hook system (IHookDispatcher, IHookHandler, HookEvents)
- Security contracts (ToolPolicy, IGatewayAuthHandler)
- Activity broadcasting (IActivityBroadcaster)
- Configuration interfaces (IConfigPathResolver)
- Extension models (ExtensionModels)
- Isolation interfaces (IIsolationStrategy)

This violates single responsibility. Every project that needs one type has to reference the entire grab bag.

**Refactor opportunity**: As described in the design spec - split into `BotNexus.Domain` (pure types) and `BotNexus.Gateway.Contracts` (gateway-specific interfaces). This is the Phase 1 + Phase 7 work.

### 9. SystemPromptBuilder is a God method (MEDIUM)

`Gateway/Agents/SystemPromptBuilder.Build()` is a 572-line static class that builds the entire system prompt through string concatenation with dozens of conditional sections. It handles:
- Tool detection and capability flags
- Context file ordering and injection
- Skill prompts
- Memory prompts
- Messaging guidance
- Reply tags
- Voice/TTS hints
- Runtime info
- Heartbeat configuration
- Silent reply rules
- Approval workflows
- Cache boundaries

**Refactor opportunity**: Extract each section builder into its own class/strategy (e.g., `ToolSectionBuilder`, `SkillSectionBuilder`, `MessagingSectionBuilder`). Use a pipeline/chain pattern so sections can be composed, reordered, or skipped. This would also make it testable - currently you can only test the full prompt output, not individual sections.

### 10. No shared base for session stores (LOW)

`FileSessionStore`, `SqliteSessionStore`, and `InMemorySessionStore` each implement `ISessionStore` independently with no shared logic. They all:
- Create sessions with the same default state
- Implement the same `NormalizeChannelKey` helper
- Handle the same Active/Suspended/Closed filtering in `ListAsync`

**Refactor opportunity**: An abstract `SessionStoreBase` could provide shared session creation logic, channel key normalization, and common query filters. Each implementation only overrides the storage-specific parts.



---

## Primitive Obsession Analysis

The codebase relies heavily on raw strings for identity, type discrimination, and composite keys. These "stringly-typed" patterns lead to bugs (typos, case mismatches), force normalization helpers everywhere, and make the code harder to reason about.

### 1. Channel Type - raw string everywhere (HIGH)

`ChannelType` is a `string` passed through the entire stack:
- `IChannelAdapter.ChannelType` returns `"telegram"`, `"tui"`, `"signalr"`
- `GatewaySession.ChannelType` stores it
- `InboundMessage.ChannelType` and `OutboundMessage.ChannelType` carry it
- `NormalizeChannelKey()` is triplicated because nobody trusts the string's casing

This is the textbook case: when you need a normalization helper every time you compare a value, it should be a value object.

**Fix**: `ChannelKey` value object with built-in normalization, equality, and `ToString()`. Constructed once at the adapter boundary, passed as a typed object everywhere else. The triplicated `NormalizeChannelKey()` disappears entirely.

### 2. Agent ID / Session ID - raw strings with conventions (HIGH)

`AgentId` and `SessionId` are plain strings, but the code encodes structure into them:
- `$"{agentId}::{sessionId}"` as composite keys in `DefaultAgentSupervisor.MakeKey()`
- `$"{request.ParentSessionId}::subagent::{uniqueId}"` for sub-agent sessions
- `$"{sourceAgentId}::cross::{targetAgentId}::{Guid.NewGuid():N}"` for cross-agent sessions
- `sessionId.Contains("::subagent::")` used to detect sub-agent sessions at runtime

When you parse structure out of a string ID, the string IS an object pretending to be a string.

**Fix**: 
- `AgentId` value object (wraps string, provides equality, validation)
- `SessionId` value object with typed construction:
  - `SessionId.Create()` for new sessions
  - `SessionId.ForSubAgent(parentSessionId, uniqueId)` 
  - `SessionId.ForCrossAgent(sourceAgentId, targetAgentId)`
- `AgentSessionKey` value object replacing the `$"{agentId}::{sessionId}"` pattern

### 3. Session Role - magic strings (MEDIUM)

`SessionEntry.Role` is `string` with values `"user"`, `"assistant"`, `"system"`, `"tool"`. These are compared with string literals throughout:
- `Role = "user"` in GatewayHost, ChatController
- `Role = "assistant"` in GatewayHost, StreamingSessionHelper
- `Role = "system"` in LlmSessionCompactor
- `Role = "tool"` in StreamingSessionHelper

**Fix**: `MessageRole` smart enum (User, Assistant, System, Tool). The `SessionEntry.Role` becomes typed, string comparisons become equality checks. Smart enum is extensible - extensions can register new roles without modifying core code.

### 4. Isolation Strategy - stringly-typed (MEDIUM)

`AgentDescriptor.IsolationStrategy` is `string` defaulting to `"in-process"`. The code manually maps it:
- `"in-process"`, `"sandbox"`, `"container"`, `"remote"` as string constants
- Each strategy class declares `public string Name => "in-process"` etc.
- Config files use these magic strings

**Fix**: `ExecutionStrategy` smart enum (InProcess, Sandbox, Container, Remote) as defined in the domain model. String parsing happens once at config load. New strategies can be registered by extensions without changing core code.

### 5. Session Access Level - stringly-typed (LOW)

`AgentDescriptor.SessionAccessLevel` is `string` ("own", "allowlist", "all"). The code manually parses it with a switch expression in `InProcessIsolationStrategy`.

There's already a `SessionAccessLevel` type defined somewhere in the code, but the descriptor uses a raw string. This means the parsing happens at consumption time rather than definition time.

**Fix**: Use a `SessionAccessLevel` smart enum directly on `AgentDescriptor`. Parse once at config load. Extensible if new access levels are needed.

### 6. Conversation ID / Sender ID - opaque strings (MEDIUM)

`InboundMessage` has `SenderId` and `ConversationId` as raw strings. These are channel-specific (Telegram chat ID, SignalR connection ID, etc.) but there's no type safety - you could accidentally pass a SenderId where a ConversationId is expected.

**Fix**: `SenderId` and `ConversationId` value objects. They're still strings underneath, but the type system prevents mix-ups.

### 7. Tool names - raw strings (LOW)

Tool names are strings everywhere (`ToolIds`, `ToolNames`, etc.). The SystemPromptBuilder does extensive normalization:
- `rawToolNames.Select(t => t?.Trim())` 
- `normalizedTools = rawToolNames.Select(t => t.ToLowerInvariant()).ToHashSet()`
- `canonicalByNormalized` lookup dictionary

**Fix**: `ToolName` value object with built-in normalization and case-insensitive equality.

### Summary Table

| Primitive          | Current Type | Proposed Type                   | Normalization Killed                    | Priority |
| ------------------ | ------------ | ------------------------------- | --------------------------------------- | -------- |
| Channel type       | `string`     | `ChannelKey`                    | NormalizeChannelKey x3                  | HIGH     |
| Agent ID           | `string`     | `AgentId`                       | None specific, but prevents mix-ups     | HIGH     |
| Session ID         | `string`     | `SessionId`                     | MakeKey, Contains("::subagent::")       | HIGH     |
| Agent+Session key  | `string`     | `AgentSessionKey`               | MakeKey() eliminated                    | HIGH     |
| Message role       | `string`     | `MessageRole` smart enum        | String comparisons x10+                 | MEDIUM   |
| Isolation strategy | `string`     | `ExecutionStrategy` smart enum  | Switch parsing                          | MEDIUM   |
| Session access     | `string`     | `SessionAccessLevel` smart enum | Switch parsing                          | LOW      |
| Conversation ID    | `string`     | `ConversationId`                | Prevents SenderId/ConversationId mix-up | MEDIUM   |
| Sender ID          | `string`     | `SenderId`                      | Same as above                           | MEDIUM   |
| Tool name          | `string`     | `ToolName`                      | Normalize + lowercase + trim x3         | LOW      |


---

## Utility & Helper Class Analysis

Static utility and helper classes are a sign of missing domain objects or primitive obsession. Each one below represents logic that should live on the object it operates on.

### Classes that should become value object methods

| Current Utility                                              | What It Does                                               | Should Be                                                                                   |
| ------------------------------------------------------------ | ---------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| `NormalizeChannelKey()` x3                                   | Trims/lowercases channel type string                       | `ChannelKey` value object - normalizes at construction                                      |
| `PathUtils.ResolvePath/SanitizePath/IsUnderRoot` (240 lines) | Validates and normalizes file paths against workspace root | `WorkspacePath` value object - construction enforces containment, invalid paths can't exist |
| `AgentDescriptorValidator.Validate()`                        | Validates AgentDescriptor fields                           | `AgentDescriptor.Validate()` instance method (or validation in constructor)                 |
| `NormalizeToolCallId()` x2                                   | Strips non-alphanumeric, truncates to max length           | `ToolCallId` value object - normalizes at construction with provider-specific max length    |
| `NormalizeLineEndings()` x2                                  | Replaces CRLF/CR with LF                                   | Extension method on `string`, or method on a `FileContent` type                             |

### Classes that should become instance methods or extension methods

| Current Utility                                    | What It Does                                       | Should Be                                                                                                                |
| -------------------------------------------------- | -------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `StreamingSessionHelper.ProcessAndSaveAsync()`     | Processes agent stream events into session history | Instance method on `Session` or a `SessionStreamProcessor` service (not static)                                          |
| `MessageTransformer.TransformMessages()`           | Cross-provider message normalization               | Extension method on `IReadOnlyList<Message>` or method on a `MessagePipeline`                                            |
| `ContextOverflowDetector.IsContextOverflow()`      | Detects context window overflow from error text    | Extension methods: `string.IsContextOverflow()`, `Exception.IsContextOverflow()`, `AssistantMessage.IsContextOverflow()` |
| `SimpleOptionsHelper.BuildBaseOptions()`           | Builds StreamOptions from SimpleStreamOptions      | Factory method on `StreamOptions` itself: `StreamOptions.FromSimple(model, options, apiKey)`                             |
| `SimpleOptionsHelper.AdjustMaxTokensForThinking()` | Calculates thinking budget adjustments             | Method on `ThinkingBudgets` or `StreamOptions`                                                                           |

### Classes that are acceptable as-is

Some static classes are fine because they represent infrastructure concerns or DI registration patterns:

| Class                                   | Reason It's Fine                                                                            |
| --------------------------------------- | ------------------------------------------------------------------------------------------- |
| `*ServiceCollectionExtensions`          | Standard .NET DI registration pattern - these are extension methods on `IServiceCollection` |
| `*Diagnostics` (AgentDiagnostics, etc.) | Static `ActivitySource` holders - standard OpenTelemetry pattern                            |
| `PlatformConfigLoader`                  | Config file I/O - belongs at the infrastructure boundary                                    |
| `PlatformConfigSchema`                  | JSON Schema generation - infrastructure concern                                             |
| `EnvironmentApiKeys`                    | Environment variable reading - infrastructure boundary                                      |
| `CopilotOAuth`                          | OAuth flow helper - external service integration                                            |
| `CompatDetector`                        | Provider feature detection from URL/model - could be on provider but acceptable             |
| `PreConfiguredModels`                   | Static model definitions - reference data                                                   |

### The SystemPromptBuilder special case

`SystemPromptBuilder` (572 lines) is the largest static class. It's not a typical utility - it's really a builder/factory that constructs a complex string output. The fix isn't making it an instance method on some other type. Instead:

- Decompose into a **pipeline of section builders** (each a small focused class)
- Each section builder is testable independently
- The top-level builder orchestrates the pipeline
- This is the Strategy/Chain pattern, not "move to an instance method"

### Summary

Of ~45 static classes in the codebase:
- **~10** are utility/helper classes that should be eliminated through value objects and instance methods
- **~15** are DI registration extensions (fine)
- **~8** are diagnostics/infrastructure (fine)
- **~5** are provider-specific builders/parsers (decompose into smaller services)
- **~7** are in `AgentCore/Loop/` (internal implementation - acceptable as static for performance, but could benefit from interfaces for testability)

---

## Test Coverage Analysis

| Project                       | Test Files | Notes                                        |
| ----------------------------- | ---------- | -------------------------------------------- |
| BotNexus.Gateway.Tests        | 92         | Most coverage, mostly config/agent lifecycle |
| BotNexus.AgentCore.Tests      | 19         | Loop runner, tool execution                  |
| BotNexus.Providers.Core.Tests | 19         | Provider abstractions                        |
| BotNexus.Memory.Tests         | 10         | Memory store, search                         |
| BotNexus.Skills.Tests         | 7          | Skill loading                                |
| BotNexus.Cron.Tests           | 5          | Scheduler, job store                         |
| BotNexus.Extensions.*.Tests   | ~30 total  | Extension-specific tests                     |
| BotNexus.Providers.*.Tests    | ~25 total  | Provider implementations                     |

**Gaps:**
- No dedicated session store tests (File, SQLite lifecycle)
- No channel adapter contract tests
- No routing pipeline tests
- No sub-agent lifecycle tests
- No session lifecycle state machine tests
- No integration tests for channel -> router -> session -> agent flow

---

## Isolation Strategy Analysis

**Location**: `Gateway/Isolation/`

Four strategies implemented:
- `InProcessIsolationStrategy` - runs agent in gateway process
- `SandboxIsolationStrategy` - restricted process
- `ContainerIsolationStrategy` - Docker container
- `RemoteIsolationStrategy` - remote HTTP/gRPC proxy

These map to the domain model's "Agent Execution Strategies" under World. Currently they're just isolation implementations - making them part of the World domain object gives them conceptual context.

---

## Extension System

Extensions are loaded dynamically from a configured directory. Each extension has a `botnexus-extension.json` manifest. The gateway uses `AssemblyLoadContextExtensionLoader` to discover and load them.

Skills specifically are handled by `BotNexus.Extensions.Skills` which loads SKILL.md files and makes them available to agents. This aligns with the domain model's Skills concept but lives as an extension rather than a first-class domain concern.

---

## Configuration Flow

1. `PlatformConfig` loaded from `~/.botnexus/config.json`
2. `PlatformConfigAgentSource` reads agent definitions and converts to `AgentDescriptor`
3. `AgentDescriptor` registered in `IAgentRegistry`
4. `SystemPromptBuilder` reads workspace files (AGENTS.md, SOUL.md, IDENTITY.md, etc.) and builds the system prompt
5. `WorkspaceContextBuilder` discovers and reads workspace context files

Identity and Soul are loaded as text files into the system prompt. They have no structured representation in the runtime - the LLM interprets them, but the system doesn't parse them.

---

## Key Refactoring Risks

1. **Session migration**: Adding Participants, IsInteractive, SessionType to existing sessions requires data migration for File and SQLite stores. InMemory is fine (ephemeral).

2. **CronChannelAdapter removal**: Cron currently creates sessions through the channel pipeline. Refactoring to an internal trigger means the session creation path needs to work without a channel adapter.

3. **Sub-agent identity change**: Changing from `childAgentId = parentAgentId` means sub-agents will use a different descriptor or a synthetic one. The supervisor needs to handle this.

4. **Abstractions split**: Many projects reference `BotNexus.Gateway.Abstractions`. Splitting it into Domain + Contracts requires updating every project reference. Use type-forwarding attributes during migration so existing references keep compiling while consumers are updated incrementally.

5. **GatewaySession decomposition**: Separating domain state from infrastructure state means every session consumer needs to be updated. The replay buffer is deeply integrated.

6. **SystemPromptBuilder refactor**: The 572-line static method is tightly coupled to the prompt format. Any decomposition risks breaking the carefully tuned prompt output. Need comprehensive snapshot tests before touching it.

7. **Provider consolidation**: OpenAI Completions and Compat providers share streaming logic but have subtle differences in error handling and feature detection. Merging prematurely could break provider-specific edge cases.
