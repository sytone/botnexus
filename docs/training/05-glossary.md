# Glossary

Quick reference for all key terms used in the BotNexus codebase. Terms are organized alphabetically.

> **See also:** [Architecture Overview](../architecture/overview.md) · [Provider System](01-providers.md) · [Agent Core](02-agent-core.md) · [Coding Agent](03-coding-agent.md) · [Build Your Own Agent](04-building-your-own.md) · [Context File Discovery](06-context-file-discovery.md) · [Thinking Levels](07-thinking-levels.md) · [Tool Development](09-tool-development.md)

---

### AfterToolCallDelegate

Hook invoked after tool execution completes. Can transform the result by returning an `AfterToolCallResult` with overridden `Content`, `Details`, or `IsError` fields.

**Source:** `BotNexus.Agent.Core` — Agent hook delegates
**Training:** [Agent Core — Hooks](02-agent-core.md)

---

### Agent

Stateful wrapper that manages a conversation loop — prompt → LLM → tools → repeat. Enforces single-run concurrency via `SemaphoreSlim`. Exposes `PromptAsync`, `ContinueAsync`, `Steer`, and `FollowUp` APIs for controlling the conversation.

**Source:** `BotNexus.Agent.Core.Agent`
**Training:** [Agent Core](02-agent-core.md) · [Build Your Own Agent](04-building-your-own.md)

---

### AgentContext

Immutable snapshot (`record`) of the current agent state: system prompt, messages, and tools. Passed to the loop and tools so they operate on a consistent view of the world.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — State Management](02-agent-core.md)

---

### AgentEvent

Base type for all lifecycle events emitted during an agent run. Subtypes include `AgentStartEvent`, `TurnStartEvent`, `MessageStartEvent`, `MessageUpdateEvent`, `MessageEndEvent`, `TurnEndEvent`, `AgentEndEvent`, `ToolExecutionStartEvent`, `ToolExecutionUpdateEvent`, and `ToolExecutionEndEvent`.

**Source:** `BotNexus.Agent.Core.Events`
**Training:** [Agent Core — Events](02-agent-core.md)

---

### AgentLoopRunner

Static class that implements the core turn loop: drain steering messages → call the LLM → execute any tool calls → repeat until the LLM stops or an abort is requested.

**Source:** `BotNexus.Agent.Core.Loop`
**Training:** [Agent Core — The Loop](02-agent-core.md)

---

### AgentOptions

Record containing all configuration for creating an `Agent`: model, `LlmClient`, delegates, hooks, generation settings, and queue modes.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core](02-agent-core.md) · [Build Your Own Agent](04-building-your-own.md)

---

### AgentState

Mutable runtime state held by an `Agent`. Includes `SystemPrompt`, `Model`, `ThinkingLevel`, `Tools`, `Messages`, `IsStreaming`, `StreamingMessage`, `PendingToolCalls`, and `ErrorMessage`.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — State Management](02-agent-core.md)

---

### AgentToolResult

Record returned by `IAgentTool.ExecuteAsync`. Contains `Content` (a list of `AgentToolContent`) plus optional `Details` metadata that gets forwarded as tool-result details.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — Tool Execution](02-agent-core.md)

---

### ApiProviderRegistry

Instance-based, thread-safe registry that maps API format strings to `IApiProvider` instances. Backed by `ConcurrentDictionary` for safe concurrent access.

**Source:** `BotNexus.Agent.Providers.Core`
**Training:** [Provider System — Registry](01-providers.md)

---

### AssistantMessage

Provider-level record for LLM responses. Carries `Content` (`ContentBlock[]`), `Api`, `Provider`, `ModelId`, `Usage`, `StopReason`, `ErrorMessage`, `ResponseId`, and `Timestamp`.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System — Message Types](01-providers.md)

---

### AssistantMessageEvent

Base type for all streaming events emitted from an `LlmStream`. Subtypes: `StartEvent`, `TextStartEvent`, `TextDeltaEvent`, `TextEndEvent`, `ThinkingStartEvent`, `ThinkingDeltaEvent`, `ThinkingEndEvent`, `ToolCallStartEvent`, `ToolCallDeltaEvent`, `ToolCallEndEvent`, `DoneEvent`, `ErrorEvent`.

**Source:** `BotNexus.Agent.Providers.Core.Streaming`
**Training:** [Provider System — Streaming](01-providers.md)

---

### AuditHooks

CodingAgent hook that logs tool call timing and results to the session log for diagnostics and observability.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — Hooks](03-coding-agent.md)

---

### BeforeToolCallDelegate

Hook invoked before tool execution. Can block a tool call by returning `BeforeToolCallResult(Block: true)`, preventing the tool from running.

**Source:** `BotNexus.Agent.Core` — Agent hook delegates
**Training:** [Agent Core — Hooks](02-agent-core.md)

---

### CacheRetention

Enum controlling provider-side prompt caching behavior. Values: `Short` and `Long`.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System](01-providers.md)

---

### CodingAgent

Static factory class that creates a fully configured `Agent` with built-in tools, hooks, and system prompt. The primary entry point is `CreateAsync`.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent](03-coding-agent.md) · [Build Your Own Agent](04-building-your-own.md)

---

### CodingAgentConfig

Configuration record for the coding agent. Includes `Model`, `Provider`, `MaxToolIterations`, `MaxContextTokens`, `AllowedCommands`, `BlockedPaths`, and `Custom` settings.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — Configuration](03-coding-agent.md)

---

### ContentBlock

Polymorphic base record for message content. Subtypes: `TextContent`, `ThinkingContent`, `ImageContent`, and `ToolCallContent`. Used throughout both provider and agent layers.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System — Message Types](01-providers.md) · [Architecture Overview](../architecture/overview.md)

---

### Context

Record sent to providers containing the full LLM request payload: `SystemPrompt`, `Messages`, and `Tools`.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System](01-providers.md)

---

### ContextConverter

Transforms an `AgentContext` to a provider `Context` at the LLM call boundary. Bridges the agent layer and provider layer models.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — The Loop](02-agent-core.md)

---

### ConvertToLlmDelegate

Delegate that maps `AgentMessage[]` to provider `Message[]` for the LLM call, enabling custom message transformation before requests are sent.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core](02-agent-core.md)

---

### Extension

An `IExtension` plugin that hooks into the agent lifecycle — tool calls, sessions, compaction, and model requests. Extensions are loaded from DLL assemblies at runtime.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — Extensions](03-coding-agent.md) · [Build Your Own Agent](04-building-your-own.md)

---

### ExtensionLoader

Scans DLL assemblies in `.botnexus-agent/extensions/` and discovers `IExtension` implementations via reflection.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — Extensions](03-coding-agent.md)

---

### ExtensionRunner

Orchestrates lifecycle hooks across all loaded extensions, delegating events in registration order.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — Extensions](03-coding-agent.md)

---

### GetApiKeyDelegate

Delegate for runtime API key resolution. Signature: `(provider, CancellationToken) → string?`. Lets the host supply keys dynamically rather than at configuration time.

**Source:** `BotNexus.Agent.Providers.Core`
**Training:** [Provider System](01-providers.md)

---

### Hook

A callback invoked before or after tool execution for validation, logging, or result transformation. See [BeforeToolCallDelegate](#beforetoolcalldelegate) and [AfterToolCallDelegate](#aftertoolcalldelegate).

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — Hooks](02-agent-core.md)

---

### IAgentTool

Interface for tools the agent can invoke. Requires `PrepareArgumentsAsync` (validate and transform args) and `ExecuteAsync` (run the tool). Properties: `Name`, `Label`, `Definition`. Optional members: `GetPromptSnippet`, `GetPromptGuidelines`.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — Tool Execution](02-agent-core.md) · [Build Your Own Agent](04-building-your-own.md)

---

### IApiProvider

Interface for LLM provider implementations. Property: `Api` (routing key). Methods: `Stream` and `StreamSimple` for sending requests and receiving streamed responses.

**Source:** `BotNexus.Agent.Providers.Core`
**Training:** [Provider System](01-providers.md)

---

### IExtension

Plugin interface for CodingAgent extensions. Methods: `GetTools`, `OnToolCallAsync`, `OnToolResultAsync`, `OnSessionStartAsync`, `OnSessionEndAsync`, `OnCompactionAsync`, `OnModelRequestAsync`.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — Extensions](03-coding-agent.md) · [Build Your Own Agent](04-building-your-own.md)

---

### LlmClient

Instance-based entry point that routes LLM requests to the correct provider. Accepts `ApiProviderRegistry` and `ModelRegistry` via constructor. All LLM calls flow through this class.

**Source:** `BotNexus.Agent.Providers.Core`
**Training:** [Provider System](01-providers.md) · [Architecture Overview](../architecture/overview.md)

---

### LlmModel

Record describing an LLM model: `Id`, `Name`, `Api` (routing key), `Provider`, `BaseUrl`, `Reasoning`, input modalities, `Cost`, `ContextWindow`, `MaxTokens`, `SupportsExtraHighThinking`, optional `Headers` and `Compat`.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System — Model Registry](01-providers.md)

---

### LlmStream

Channel-based `IAsyncEnumerable` of `AssistantMessageEvent`. Providers push events via `Push()`; consumers iterate via `await foreach`. Built on `System.Threading.Channels` for backpressure-aware streaming.

**Source:** `BotNexus.Agent.Providers.Core.Streaming`
**Training:** [Provider System — Streaming](01-providers.md)

---

### Message

Abstract base record for conversation messages. Subtypes: `UserMessage`, `AssistantMessage`, `ToolResultMessage`. Uses `[JsonPolymorphic]` with a `"role"` discriminator for serialization.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System — Message Types](01-providers.md)

---

### MessageConverter

Converts between agent-level messages (`AgentMessage`) and provider-level messages (`Message`). Used at the boundary between AgentCore and Providers.Core.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core](02-agent-core.md)

---

### ModelCost

Record with per-million-token pricing: `Input`, `Output`, `CacheRead`, `CacheWrite`.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System — Model Registry](01-providers.md)

---

### ModelRegistry

Instance-based registry mapping `(provider, modelId)` pairs to `LlmModel` definitions. Backed by `ConcurrentDictionary` for thread-safe lookups.

**Source:** `BotNexus.Agent.Providers.Core`
**Training:** [Provider System — Model Registry](01-providers.md) · [Architecture Overview](../architecture/overview.md)

---

### PendingMessageQueue

Thread-safe queue for steering and follow-up messages injected into the agent loop. Supports `QueueMode.All` (drain everything) and `QueueMode.OneAtATime` (one message per boundary) drain modes.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — Steering](02-agent-core.md)

---

### Provider

An `IApiProvider` implementation that communicates with a specific LLM API (Anthropic, OpenAI, GitHub Copilot, etc.). Each provider translates between the BotNexus streaming protocol and the vendor's wire format.

**Source:** `BotNexus.Providers.*`
**Training:** [Provider System](01-providers.md)

---

### QueueMode

Enum controlling message queue drainage behavior. `All` drains every pending message at once; `OneAtATime` drains one message per turn boundary.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — Steering](02-agent-core.md)

---

### SafetyHooks

CodingAgent hook that enforces path blocking, command restrictions, and write size warnings to prevent the agent from performing dangerous operations.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — Safety](03-coding-agent.md)

---

### SessionCompactor

Summarizes older messages when conversation history exceeds context window limits. Supports LLM-driven summarization and a heuristic fallback when the LLM is unavailable.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — Sessions](03-coding-agent.md)

---

### SessionManager

JSONL-based session persistence with DAG branching support. Methods: `Create`, `Save`, `Resume`, `List`, `Branch`, `Switch`, `Delete`.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — Sessions](03-coding-agent.md)

---

### SimpleStreamOptions

Extended `StreamOptions` that adds `Reasoning` (`ThinkingLevel`) and `ThinkingBudgets` for reasoning model support.

**Source:** `BotNexus.Agent.Providers.Core`
**Training:** [Provider System — Streaming](01-providers.md)

---

### Skill

A markdown file with instructions injected into the system prompt to teach the agent domain-specific knowledge. Skills are loaded at agent startup and become part of the system prompt context.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent](03-coding-agent.md)

---

### StopReason

Enum indicating why the LLM stopped generating. Values: `Stop`, `Length`, `ToolUse`, `Error`, `Aborted`, `Refusal`, `PauseTurn`, `Sensitive`.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System — Streaming](01-providers.md)

---

### StreamAccumulator

Converts `LlmStream` events into `AgentEvent` instances and a final `AssistantAgentMessage`. Bridges the provider streaming layer and the agent event system.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — Events](02-agent-core.md) · [Provider System — Streaming](01-providers.md)

---

### StreamOptions

Record controlling LLM generation parameters: `Temperature`, `MaxTokens`, `CancellationToken`, `ApiKey`, `Transport`, `CacheRetention`, `SessionId`, `OnPayload`, `Headers`, `MaxRetryDelayMs`, `Metadata`.

**Source:** `BotNexus.Agent.Providers.Core`
**Training:** [Provider System](01-providers.md)

---

### SystemPromptBuilder

Assembles the system prompt from multiple sources: environment context, tool contributions, skill files, and custom instructions. Produces the final string passed as the system prompt to the LLM.

**Source:** `BotNexus.CodingAgent`
**Training:** [Coding Agent — System Prompt](03-coding-agent.md)

---

### Tool

Record defining a tool for the LLM at the provider level: `Name`, `Description`, `Parameters` (`JsonElement` JSON Schema). This is the wire-format representation sent to the LLM.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System](01-providers.md)

---

### ToolCallContent

`ContentBlock` subtype representing an LLM tool call. Contains `Id`, `Name`, `Arguments` (`Dictionary`), and `ThoughtSignature`.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System — Message Types](01-providers.md)

---

### ToolExecutionMode

Enum controlling tool execution strategy: `Sequential` (one tool at a time) or `Parallel` (concurrent execution with sequential prepare phase).

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — Tool Execution](02-agent-core.md)

---

### ToolExecutor

Runs tool calls through the full pipeline: lookup → prepare arguments → before hook → execute → after hook → result. Supports both sequential and parallel execution modes.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — Tool Execution](02-agent-core.md)

---

### ToolResultMessage

Provider-level record for tool execution results. Contains `ToolCallId`, `ToolName`, `Content`, `IsError`, and `Details`.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System — Message Types](01-providers.md)

---

### TransformContextDelegate

Delegate for pre-LLM context transformation such as compaction or message filtering. Invoked just before the context is sent to the provider.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core](02-agent-core.md)

---

### Turn

One LLM invocation plus optional tool execution within an agent run. The agent loop consists of repeated turns until the LLM produces a final response or an abort is triggered.

**Source:** `BotNexus.Agent.Core`
**Training:** [Agent Core — The Loop](02-agent-core.md)

---

### Usage

Token usage tracking record: `Input`, `Output`, `CacheRead`, `CacheWrite`, `TotalTokens`, `Cost`. Accumulated per-turn and per-session.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System](01-providers.md)

---

### UserMessageContent

Union type for user message payloads: either a plain `string` or an array of `ContentBlock[]` for multimodal inputs (text + images).

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Provider System — Message Types](01-providers.md)

---

### ContextFileDiscovery

Automatic mechanism that scans the working directory for project documentation (README.md, copilot-instructions.md, docs/*.md) and injects them into the system prompt. Respects a token budget and truncates files that exceed available space.

**Source:** `BotNexus.CodingAgent.Utils`
**Training:** [Context File Discovery](06-context-file-discovery.md)

---

### ThinkingLevel

Enum controlling reasoning intensity for models that support extended thinking: `Minimal`, `Low`, `Medium`, `High`, `ExtraHigh`. Each level has a corresponding thinking token budget.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Thinking Levels](07-thinking-levels.md) · [Provider System](01-providers.md)

---

### ThinkingBudget

Token allocation for internal LLM reasoning at a specific thinking level. For example, `ThinkingLevel.High` has a default budget of 16,384 tokens. Custom budgets can be provided via `ThinkingBudgets`.

**Source:** `BotNexus.Agent.Providers.Core.Models`
**Training:** [Thinking Levels](07-thinking-levels.md)

---

### SimpleStreamOptions

Extended `StreamOptions` that adds reasoning support via `Reasoning` (`ThinkingLevel`) and `ThinkingBudgets` fields. Used when calling LLMs that support extended thinking.

**Source:** `BotNexus.Agent.Providers.Core`
**Training:** [Thinking Levels](07-thinking-levels.md)

---

### SimpleOptionsHelper

Utility class that calculates thinking budgets, clamps reasoning levels to supported ranges, and adjusts maxTokens to ensure room for both thinking and output tokens. Port of pi-mono's providers/simple-options.ts.

**Source:** `BotNexus.Agent.Providers.Core.Utilities`
**Training:** [Thinking Levels](07-thinking-levels.md)
