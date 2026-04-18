# Deep Port Audit: pi-mono (TypeScript) → BotNexus (C#)

**Date:** 2025-07-24
**Auditor:** Leela (Lead / Architect)
**Requested by:** sytone

## Summary

| Severity | Providers | Agent | CodingAgent | Total |
|----------|-----------|-------|-------------|-------|
| Critical | 12 | 1 | 2 | **15** |
| Major | 27 | 5 | 11 | **43** |
| Minor | 36 | 14 | 13 | **63** |
| Enhancement | 16 | 8 | 8 | **32** |
| **Total** | **91** | **28** | **34** | **153** |

### Status Update (2026-04-05)

The following 16 findings have been **FIXED** in commits 92f655c through 405d7d8:

| Finding | Title | Fixed In |
|---------|-------|----------|
| PROV-005 | API mismatch validation missing in ApiProviderRegistry | 92f655c (GuardedProvider wraps all registrations) |
| PROV-030 | LlmStream.Push doesn't auto-complete channel on terminal events | 92f655c (auto-completes on DoneEvent/ErrorEvent) |
| PROV-037 | Anthropic doesn't disable thinking when not requested | 19b0b55 |
| PROV-038 | Adaptive-thinking model budget falls through | 19b0b55 |
| PROV-039 | Temperature suppression logic differs | 19b0b55 |
| PROV-040 | Missing HTTP headers (accept, anthropic-dangerous-direct-browser-access) | 19b0b55 |
| PROV-041 | Copilot dynamic headers built from transformed messages | 19b0b55 |
| PROV-055 | Missing finish_reason mappings in OpenAICompat | 8041838 |
| PROV-057 | Missing hasToolHistory check in OpenAICompat | 8041838 |
| PROV-061 | GitHub Copilot headers applied in wrong order (OpenAI Responses) | 6b8bd2c |
| AGENT-003 | TS mutates context.messages during streaming; C# does not | fdb42be (StreamAccumulator now tracks partials in context) |
| AGENT-004 | C# skips initial steering poll for follow-up continuations | b902c79 (only skips for steering queue, not follow-ups) |
| CODING-003 | System prompt vastly simplified vs pi-mono | b8b9abc (tool-aware guidelines, date, custom prompt support) |
| CODING-009 | Skills loading misses validation rules | 0bcd09f (hyphen, description, length rules enforced) |
| CODING-010 | ContextFileDiscovery missing additional file patterns | 405d7d8 (INSTRUCTIONS.md, .botnexus-agent/AGENTS.md added) |
| CODING-014 | Piped stdin content detection and processing missing | 405d7d8 (Console.IsInputRedirected detection added) |

### Top 10 Priority Fixes

1. **PROV-051** — OpenAICompat missing ALL reasoning/thinking stream parsing (DeepSeek R1, Qwen QwQ broken)
2. **PROV-068–070** — All 3 Google providers missing (~2,007 lines)
3. **PROV-063** — Entire Amazon Bedrock provider missing (~808 lines)
4. **PROV-074–076** — Mistral/Azure/Codex providers missing (~1,765 lines)
5. **CODING-001** — 23+ CLI flags missing from CommandParser
6. **CODING-002** — Print/JSON/RPC modes missing (editor integration broken)
7. **PROV-056** — Missing transformMessages in OpenAICompat (orphan tool calls crash)
8. ~~**PROV-037** — Anthropic doesn't disable thinking when not requested (cost impact)~~ **FIXED**
9. ~~**CODING-003** — System prompt is vastly simplified vs pi-mono~~ **FIXED**
10. **AGENT-019** — proxy.ts not ported (proxy-based LLM access unavailable)

---

## Critical Findings

### PROV-001 — Google-vertex API key detection entirely missing

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/env-api-keys.ts:78-90` |
| **BotNexus Ref** | `EnvironmentApiKeys.cs` — MISSING |
| **Details** | pi-mono checks `GOOGLE_CLOUD_API_KEY`, then probes for Application Default Credentials (ADC file at `~/.config/gcloud/application_default_credentials.json`) combined with `GOOGLE_CLOUD_PROJECT`/`GCLOUD_PROJECT` and `GOOGLE_CLOUD_LOCATION`. Returns `"<authenticated>"` sentinel when all present. BotNexus has no google-vertex entry at all — neither in the envMap nor as a special-case branch. Any future Vertex provider will silently get null API key. |
| **Effort** | Medium |

### PROV-002 — Amazon-bedrock credential detection entirely missing

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/env-api-keys.ts:92-109` |
| **BotNexus Ref** | `EnvironmentApiKeys.cs` — MISSING |
| **Details** | pi-mono checks 6 credential sources: `AWS_PROFILE`, `AWS_ACCESS_KEY_ID`+`AWS_SECRET_ACCESS_KEY`, `AWS_BEARER_TOKEN_BEDROCK`, `AWS_CONTAINER_CREDENTIALS_RELATIVE_URI`, `AWS_CONTAINER_CREDENTIALS_FULL_URI`, `AWS_WEB_IDENTITY_TOKEN_FILE`. Returns `"<authenticated>"` sentinel. BotNexus has no amazon-bedrock handling. Add a branch that mirrors the same 6 env-var checks. |
| **Effort** | Small |

### PROV-003 — BuiltInModels only registers 3 of 22+ known providers

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/models.generated.ts` (full file) |
| **BotNexus Ref** | `Registry/BuiltInModels.cs` (only github-copilot, anthropic, openai) |
| **Details** | pi-mono's generated model catalog includes ~22 providers with dozens of models (Nova, Gemini, Grok, Llama, Mistral, etc.) with precise costs, context windows, and API mappings. BotNexus only registers github-copilot, anthropic, and openai. All other providers' models are entirely absent. `ModelRegistry.GetModel()` will return null for any bedrock, google, groq, xai, etc. model. |
| **Effort** | Large |

### PROV-051 — Missing reasoning/thinking stream parsing in OpenAICompat

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:183-221` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:186-253` |
| **Details** | TS parses three reasoning fields (`reasoning_content`, `reasoning`, `reasoning_text`) from deltas and emits thinking events. C# only parses `content` and `tool_calls` — all reasoning output is silently dropped. DeepSeek R1, Qwen QwQ, and any reasoning-capable model served via compatible endpoints will lose all chain-of-thought output. Fix: add 3-field reasoning delta parsing. |
| **Effort** | Large |

### PROV-063 — Entire Amazon Bedrock provider is missing

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers — Bedrock |
| **pi-mono Ref** | `packages/ai/src/providers/amazon-bedrock.ts:1-808` |
| **BotNexus Ref** | MISSING |
| **Details** | Full Bedrock provider (~808 lines) with ConverseStream API, SigV4 auth, Claude/Nova support, prompt caching, extended/adaptive thinking, thinking signature validation. No `BotNexus.Providers.Bedrock` project exists. Must create from scratch using `AWSSDK.BedrockRuntime`. |
| **Effort** | Large |

### PROV-068 — Native Google Generative AI (Gemini API) provider entirely missing

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers — Google |
| **pi-mono Ref** | `packages/ai/src/providers/google.ts:1-477` |
| **BotNexus Ref** | MISSING |
| **Details** | Full Google provider (~477 lines) using `@google/genai` SDK for direct Gemini API. No `BotNexus.Providers.Google` project. Models only accessible via openai-completions shim, losing Google-specific features. |
| **Effort** | Large |

### PROV-069 — Google Vertex AI provider entirely missing

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers — Google |
| **pi-mono Ref** | `packages/ai/src/providers/google-vertex.ts:1-543` |
| **BotNexus Ref** | MISSING |
| **Details** | Full Vertex AI provider (~543 lines) with API key and ADC auth, project+location params. No `BotNexus.Providers.GoogleVertex` project. |
| **Effort** | Large |

### PROV-070 — Google Gemini CLI / Cloud Code Assist provider entirely missing

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers — Google |
| **pi-mono Ref** | `packages/ai/src/providers/google-gemini-cli.ts:1-987` |
| **BotNexus Ref** | MISSING |
| **Details** | Most complex Google provider (~987 lines) with dual endpoint support, retry with endpoint fallback, rate-limit parsing, empty stream retry. |
| **Effort** | Large |

### PROV-074 — Mistral provider completely missing (586 lines)

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers — Other |
| **pi-mono Ref** | `packages/ai/src/providers/mistral.ts` (586 lines) |
| **BotNexus Ref** | MISSING |
| **Details** | Full Mistral provider via `@mistralai/mistralai` SDK: streaming, 9-char tool-call ID normalization, image support, thinking blocks, `x-affinity` header for KV-cache reuse. Env key exists in BotNexus but nothing uses it. |
| **Effort** | Large |

### PROV-075 — Azure OpenAI Responses provider completely missing (249 lines)

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers — Other |
| **pi-mono Ref** | `packages/ai/src/providers/azure-openai-responses.ts` (249 lines) |
| **BotNexus Ref** | MISSING |
| **Details** | Azure deployment-name mapping (`AZURE_OPENAI_DEPLOYMENT_NAME_MAP`), resource-name URL construction, Azure-specific config. Env key exists in BotNexus but no provider. |
| **Effort** | Medium |

### PROV-076 — OpenAI Codex Responses provider completely missing (930 lines)

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Providers — Other |
| **pi-mono Ref** | `packages/ai/src/providers/openai-codex-responses.ts` (930 lines) |
| **BotNexus Ref** | MISSING |
| **Details** | JWT-based auth, dual-transport SSE+WebSocket with auto-fallback, WebSocket connection caching, rate-limit parsing, model-specific reasoning effort clamping. Most complex OpenAI provider variant. |
| **Effort** | Large |

### AGENT-019 — proxy.ts (ProxyMessageEventStream / streamProxy) not ported

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/proxy.ts:1-341` |
| **BotNexus Ref** | MISSING |
| **Details** | The TS proxy module provides `streamProxy()` — a stream function that routes LLM calls through a server using SSE. It handles auth tokens, event reconstruction, partial message assembly, and all content types (text, thinking, tool calls). C# has no proxy implementation. If BotNexus apps need proxy-based LLM access (e.g., for auth-managed environments), this must be implemented as a custom `LlmClient` or as a separate proxy stream. |
| **Effort** | Large |

### CODING-001 — 23+ CLI flags missing from BotNexus CommandParser

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/cli/args.ts:58-187` |
| **BotNexus Ref** | `Cli/CommandParser.cs:10-115` |
| **Details** | pi-mono supports: `--version/-v`, `--continue/-c`, `--resume/-r`, `--mode` (text/json/rpc), `--no-session`, `--session <id>`, `--fork <id>`, `--session-dir`, `--models`, `--tools`, `--no-tools`, `--print/-p`, `--export`, `--extension/-e`, `--no-extensions/-ne`, `--skill`, `--no-skills/-ns`, `--system-prompt`, `--append-system-prompt`, `--api-key`, `--prompt-template`, `--no-prompt-templates/-np`, `--theme`, `--no-themes`, `--list-models`, `--offline`, `@file` args, and unknown-flag passthrough to extensions. BotNexus only has: `--model`, `--provider`, `--resume`, `--thinking`, `--non-interactive`, `--verbose`, `--help`, and positional prompt. |
| **Effort** | Large |

### CODING-002 — Print mode, JSON mode, and RPC mode completely missing

| Field | Value |
|-------|-------|
| **Severity** | Critical |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/modes/index.ts`, `src/modes/rpc/`, `src/modes/print-mode.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono supports 4 modes: interactive (TUI), print (streaming text to stdout), json (structured JSON output), and rpc (JSON-RPC over stdio for IDE integration). BotNexus only has interactive loop and a basic "single prompt" non-interactive path. The RPC mode is critical for VS Code / editor integration. Print mode is needed for piped/non-interactive use. JSON mode for machine-parsable output. |
| **Effort** | Large |

---

## Major Findings

### PROV-004 — OpenRouterRouting uses untyped Dictionary instead of typed record

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:296-301` |
| **BotNexus Ref** | `Compatibility/OpenAICompletionsCompat.cs:22` |
| **Details** | pi-mono defines `OpenRouterRouting` as `{ only?: string[], order?: string[] }`. BotNexus uses `Dictionary<string, object>?` which loses type safety and requires runtime casting. Should be a proper record with `Only`/`Order` properties. |
| **Effort** | Small |

### PROV-005 — ~~API mismatch validation missing in ApiProviderRegistry.Register~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers |
| **Status** | **✅ FIXED in 92f655c** — `Register()` now wraps every provider in a `GuardedProvider` that validates `model.Api` matches the provider's expected API via `StringComparison.Ordinal`. |
| **pi-mono Ref** | `packages/ai/src/api-registry.ts:42-63` |
| **BotNexus Ref** | `Registry/ApiProviderRegistry.cs:14-35` |
| **Details** | pi-mono wraps Stream/StreamSimple with a guard: `if (model.api !== api) throw new Error(...)`. This catches misconfigured calls where a model with `api="anthropic-messages"` is accidentally passed to the openai-completions provider. BotNexus `Register()` stores the provider directly with no runtime guard. |
| **Effort** | Small |

### PROV-006 — ToolCallValidator lacks type coercion (AJV coerceTypes:true)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/utils/validation.ts:31-34` |
| **BotNexus Ref** | `Validation/ToolCallValidator.cs` |
| **Details** | pi-mono's AJV instance uses `coerceTypes: true`, meaning string `"42"` gets coerced to number 42 before validation. BotNexus does strict type matching — a string `"42"` against `type:"integer"` will fail. This causes false validation failures when LLMs return stringified numbers, which is common. Add type coercion logic. |
| **Effort** | Medium |

### PROV-007 — ToolCallValidator only validates top-level properties, not nested schemas

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/utils/validation.ts:70-78` |
| **BotNexus Ref** | `Validation/ToolCallValidator.cs:71-94` |
| **Details** | pi-mono uses AJV which recursively validates entire JSON Schema including nested objects, arrays, oneOf/anyOf/allOf, $ref, patterns, minLength/maxLength, minimum/maximum, additionalProperties, etc. BotNexus only validates: required top-level props, top-level type checks, and top-level enum values. Nested validation is entirely missing. |
| **Effort** | Large |

### PROV-008 — validateToolCall (find tool by name + validate) not ported

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/utils/validation.ts:49-55` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono exports `validateToolCall(tools, toolCall)` which finds the tool by name from the array and throws "Tool not found" if missing, then delegates to `validateToolArguments`. BotNexus only has `Validate(arguments, parameterSchema)` which requires the caller to look up the tool. |
| **Effort** | Small |

### PROV-009 — ModelsAreEqual uses case-insensitive comparison vs pi-mono's case-sensitive

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/models.ts:71-77` |
| **BotNexus Ref** | `Registry/ModelRegistry.cs:61-65` |
| **Details** | pi-mono uses strict equality (`===`) for both id and provider. BotNexus uses `StringComparison.OrdinalIgnoreCase`. Model IDs are case-sensitive in most provider APIs. Change to `StringComparison.Ordinal` to match pi-mono behavior. |
| **Effort** | Small |

### PROV-010 — normalizeToolCallId callback signature differs from pi-mono

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/providers/transform-messages.ts:11` |
| **BotNexus Ref** | `Utilities/MessageTransformer.cs:27` |
| **Details** | pi-mono passes the full source `AssistantMessage` as the third parameter, giving the callback access to all source message metadata. BotNexus passes only the target provider string, and constructs a synthetic sourceModel from assistant message fields. The callback cannot access the original AssistantMessage's errorMessage, usage, responseId, or any other fields. |
| **Effort** | Medium |

### PROV-030 — ~~LlmStream.Push doesn't auto-complete channel on terminal events~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Streaming |
| **Status** | **✅ FIXED in 92f655c** — `Push()` now sets `_done = true` and calls `_channel.Writer.TryComplete()` after writing a `DoneEvent` or `ErrorEvent`, preventing consumer hangs. |
| **pi-mono Ref** | `packages/ai/src/utils/event-stream.ts:EventStream.[Symbol.asyncIterator]` |
| **BotNexus Ref** | `BotNexus.Agent.Providers.Core/Streaming/LlmStream.cs:27-48` |
| **Details** | In TS, the async iterator self-terminates after yielding all queued events when `this.done` is set. In C#, `ReadAllAsync` only exits when `_channel.Writer.TryComplete()` is called inside `End()`. If a provider pushes a DoneEvent/ErrorEvent but forgets to call `End()`, the C# consumer hangs forever. Fix: auto-complete the channel inside `Push` when a terminal event is received. |
| **Effort** | Small |

### PROV-037 — ~~Anthropic StreamSimple does not explicitly disable thinking when reasoning is not requested~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Anthropic |
| **Status** | **✅ FIXED in 19b0b55** — `StreamSimple()` now explicitly sets `ThinkingEnabled = false` when reasoning is not requested. |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:488-489` |
| **BotNexus Ref** | `AnthropicProvider.cs:68-126` |
| **Details** | TS explicitly sets `thinkingEnabled: false` when `options.reasoning` is falsy, causing `thinking: {type: "disabled"}` to be sent for reasoning-capable models. C# never sets `ThinkingEnabled` when reasoning is null, so no thinking field is emitted. For reasoning models, the API may default thinking to enabled, causing unexpected tokens and costs. Fix: when `options?.Reasoning` is null, set `anthropicOpts.ThinkingEnabled = false`. |
| **Effort** | Small |

### PROV-038 — ~~Adaptive-thinking model with budget tokens falls through to budget-based instead of adaptive~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Anthropic |
| **Status** | **✅ FIXED in 19b0b55** — Conditionals restructured so adaptive-thinking models are correctly identified first. |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:660-673` |
| **BotNexus Ref** | `AnthropicRequestBuilder.cs:108-134` |
| **Details** | TS checks `supportsAdaptiveThinking(model.id)` first. C# checks Effort is not null && isAdaptiveThinkingModel first — if Effort is null but ThinkingBudgetTokens has a value, the second else-if matches budget-based instead of adaptive. Fix: restructure C# conditionals to check isAdaptiveThinkingModel as outer condition. |
| **Effort** | Small |

### PROV-039 — ~~Temperature suppression logic differs~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Anthropic |
| **Status** | **✅ FIXED in 19b0b55** — Temperature suppression now applies whenever thinking is enabled, regardless of `model.Reasoning`. |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:649-651` |
| **BotNexus Ref** | `AnthropicRequestBuilder.cs:136-145` |
| **Details** | TS suppresses temperature whenever `thinkingEnabled` is truthy, regardless of `model.reasoning`. C# only enters the thinking/temperature branches when `model.Reasoning` is true. Fix: add standalone temperature guard if `ThinkingEnabled == true`. |
| **Effort** | Small |

### PROV-040 — ~~Missing HTTP headers: accept and anthropic-dangerous-direct-browser-access~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Anthropic |
| **Status** | **✅ FIXED in 19b0b55** — Headers now applied to all Anthropic requests. |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:540-601` |
| **BotNexus Ref** | `AnthropicProvider.cs:217-264` |
| **Details** | TS sets `accept: application/json` and `anthropic-dangerous-direct-browser-access: true` for ALL auth modes. C# never sets either. Fix: add `accept: application/json` to all Anthropic requests. |
| **Effort** | Small |

### PROV-041 — ~~Copilot dynamic headers built from transformed messages instead of original~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Anthropic |
| **Status** | **✅ FIXED in 19b0b55** — Copilot dynamic headers now built from `context.Messages` (originals) before transformation. |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:237-241` |
| **BotNexus Ref** | `AnthropicProvider.cs:170-173` |
| **Details** | TS builds Copilot dynamic headers from `context.messages` (originals). C# calls `TransformMessages()` first and builds from transformed result, also calling TransformMessages twice for Copilot. Fix: use `context.Messages` directly for Copilot header construction. |
| **Effort** | Small |

### PROV-045 — User messages missing image filtering for non-vision models (OpenAI Completions)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAI Completions |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:555-558` |
| **BotNexus Ref** | `OpenAICompletionsProvider.cs:506-534` |
| **Details** | TS filters out `image_url` content when `!model.input.includes("image")` and skips empty messages. C# always includes all image blocks. Will cause API errors on text-only models. |
| **Effort** | Small |

### PROV-046 — Missing sanitizeSurrogates on user messages and system prompt (OpenAI Completions)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAI Completions |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:517,544,662` |
| **BotNexus Ref** | `OpenAICompletionsProvider.cs` |
| **Details** | TS calls `sanitizeSurrogates()` on system prompt, user text, and tool result text. C# only sanitizes assistant messages. Lone surrogates in user content could cause JSON serialization errors. |
| **Effort** | Small |

### PROV-047 — content_filter maps to Sensitive instead of Error with error message (OpenAI Completions)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAI Completions |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:785-786` |
| **BotNexus Ref** | `OpenAICompletionsProvider.cs:1028` |
| **Details** | TS maps `content_filter` to `stopReason: "error"` with `errorMessage`. C# maps to `StopReason.Sensitive` with null error. |
| **Effort** | Small |

### PROV-048 — ThinkingSignature not captured during SSE streaming (OpenAI Completions)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAI Completions |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:205` |
| **BotNexus Ref** | `OpenAICompletionsProvider.cs:812` |
| **Details** | TS sets `thinkingSignature: foundReasoningField` when creating thinking block. C# creates `new ThinkingContent("")` without ThinkingSignature. This is used when converting assistant messages back to API format. Fix: pass reasoningField as ThinkingSignature. |
| **Effort** | Small |

### PROV-049 — reasoning_details parsing uses index-based matching vs ID-based (OpenAI Completions)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAI Completions |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:261-273` |
| **BotNexus Ref** | `OpenAICompletionsProvider.cs:860-867` |
| **Details** | TS matches reasoning_details to tool calls by `detail.id === toolCall.id`. C# matches by positional index. If entries don't align positionally, wrong signatures are associated. Fix: implement ID-based matching. |
| **Effort** | Medium |

### PROV-050 — CompatDetector missing critical providers (zai, opencode, chutes.ai, openrouter)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:802-853` |
| **BotNexus Ref** | `CompatDetector.cs:10-125` |
| **Details** | pi-mono `detectCompat` handles 8+ providers. BotNexus covers only ollama, vllm, lmstudio, sglang, cerebras, xai, deepseek, groq. Missing: zai/z.ai, opencode, chutes.ai, openrouter. |
| **Effort** | Medium |

### PROV-052 — Missing thinkingFormat support in OpenAICompat

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:410-434` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:322-324` |
| **Details** | TS supports 5 thinkingFormat modes (openai, openrouter, zai, qwen, qwen-chat-template). C# only sends `reasoning_effort`. Fix: implement all format variants. |
| **Effort** | Medium |

### PROV-053 — Missing OpenRouter and Vercel AI Gateway routing preferences

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:431-445` |
| **BotNexus Ref** | MISSING |
| **Details** | TS injects provider routing preferences for OpenRouter and Vercel. C# has the compat properties but never consumes them. |
| **Effort** | Medium |

### PROV-054 — Usage parsing missing cache token details and reasoning token handling

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:733-769` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:536-547` |
| **Details** | TS extracts `cached_tokens`, `cache_write_tokens`, `reasoning_tokens` with cache normalization. C# only reads basic prompt/completion/total. |
| **Effort** | Medium |

### PROV-055 — ~~Missing finish_reason mappings in OpenAICompat~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAICompat |
| **Status** | **✅ FIXED in 8041838** — Maps `end→stop`, `function_call→toolUse`, `content_filter→error`, `network_error→error` in addition to existing mappings. |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:771-795` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:269-275` |
| **Details** | TS maps 7 finish reasons. C# only maps stop/length/tool_calls. Missing: `end→stop`, `function_call→toolUse`, `content_filter→error`, `network_error→error`. |
| **Effort** | Small |

### PROV-056 — Missing transformMessages in OpenAICompat (orphan tool calls, error filtering)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/transform-messages.ts:1-172` |
| **BotNexus Ref** | MISSING |
| **Details** | TS normalizes tool call IDs, inserts synthetic empty tool results for orphans, filters errored messages, converts cross-model thinking. C# maps messages directly. Will cause API errors with orphaned tool calls. |
| **Effort** | Large |

### PROV-057 — ~~Missing hasToolHistory check in OpenAICompat~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAICompat |
| **Status** | **✅ FIXED in 8041838** — Sends empty `tools: []` when no tools defined but messages contain tool call history. |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:41-53,401-404` |
| **BotNexus Ref** | `OpenAICompatProvider.cs` |
| **Details** | When no tools are defined but messages contain tool_calls/toolResult, TS sends empty `tools: []`. Required by Anthropic via proxy. Fix: add hasToolHistory check. |
| **Effort** | Small |

### PROV-058 — OpenAICompatOptions missing toolChoice structured type support

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:56-57` |
| **BotNexus Ref** | `OpenAICompatOptions.cs:8` |
| **Details** | TS `toolChoice` supports string AND `{type:"function",function:{name}}` object form. C# is `string?` only. Fix: make `JsonNode?` or dedicated type. |
| **Effort** | Small |

### PROV-059 — Tool call ID normalization logic entirely missing from OpenAI Responses

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses-shared.ts:94-117` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:89` |
| **Details** | TS defines `normalizeIdPart`, `buildForeignResponsesItemId`, `normalizeToolCallId` for 64-char alphanumeric IDs with `fc_` prefix. C# never passes a normalizer callback. Cross-provider tool call replay will send raw un-normalized IDs causing validation errors. |
| **Effort** | Medium |

### PROV-060 — System prompt format differs in OpenAI Responses (plain string vs structured message)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses-shared.ts:122-128` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:144-158` |
| **Details** | TS sends `{role, content: "text"}`. C# sends `{type:"message", role, content:[{type:"input_text",text}]}`. Different formats affect tokenization/caching. Match TS shorthand for parity. |
| **Effort** | Small |

### PROV-061 — ~~GitHub Copilot headers applied in wrong order in OpenAI Responses~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAI Responses |
| **Status** | **✅ FIXED in 6b8bd2c** — Header precedence corrected: model.headers → copilot → options.headers (callers win). |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses.ts:164-177` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:105-122` |
| **Details** | TS: model.headers → copilot → options.headers (callers win). C#: model → options → copilot (copilot wins). Fix: move copilot before options.Headers merge. |
| **Effort** | Small |

### PROV-062 — previous_response_id added in C# but absent from TS source

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses.ts:187-229` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:187-191` |
| **Details** | C# adds `previous_response_id` from options or by walking assistant messages. TS does not set this field. Changes API semantics — may cause double-counting of messages. |
| **Effort** | Small |

### PROV-064–067 — Bedrock provider sub-features missing

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Bedrock |
| **pi-mono Ref** | Various in `packages/ai/src/providers/amazon-bedrock.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | When Bedrock provider is created: (PROV-064) SigV4 credential chain and region resolution, (PROV-065) Prompt caching with cache points, (PROV-066) Extended/adaptive thinking, (PROV-067) Thinking signature validation, tool result batching, error formatting, request metadata for cost allocation. |
| **Effort** | Medium each |

### PROV-071 — Google shared utilities (message conversion, tool mapping) not ported

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Google |
| **pi-mono Ref** | `packages/ai/src/providers/google-shared.ts:1-327` |
| **BotNexus Ref** | MISSING |
| **Details** | ~327 lines shared across all 3 Google providers: message conversion, thought signature handling, tool declarations, 15+ FinishReason mappings. Required prerequisite for any Google provider. |
| **Effort** | Medium |

### PROV-072 — Google thought signature protocol not ported

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Google |
| **pi-mono Ref** | `packages/ai/src/providers/google-shared.ts:16-64` |
| **BotNexus Ref** | MISSING |
| **Details** | `thought: true` markers, `thoughtSignature` for multi-turn replay, base64 validation, `SKIP_THOUGHT_SIGNATURE_VALIDATOR` sentinel for Gemini 3 function calls. |
| **Effort** | Medium |

### PROV-073 — Gemini thinking level/budget system not ported

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Google |
| **pi-mono Ref** | `packages/ai/src/providers/google.ts:296-476` |
| **BotNexus Ref** | MISSING |
| **Details** | Dual thinking system: Gemini 3 uses `thinkingLevel` (MINIMAL/LOW/MEDIUM/HIGH), Gemini 2.x uses `thinkingBudget` with model-specific limits. |
| **Effort** | Medium |

### PROV-077 — Faux (test) provider missing

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Other |
| **pi-mono Ref** | `packages/ai/src/providers/faux.ts` (499 lines) |
| **BotNexus Ref** | MISSING |
| **Details** | Programmable response sequences, simulated streaming with token-per-second delays, usage estimation with cache simulation, abort handling. BotNexus uses Moq mocks but no equivalent deterministic streaming test harness. |
| **Effort** | Medium |

### PROV-078 — Provider count: BotNexus has 4 of 10 pi-mono API providers

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Providers — Other |
| **pi-mono Ref** | `packages/ai/src/providers/register-builtins.ts:366-426` |
| **BotNexus Ref** | `Program.cs:263-267` |
| **Details** | pi-mono: anthropic, openai-completions, mistral, openai-responses, azure-openai-responses, codex, google-generative-ai, google-gemini-cli, google-vertex, bedrock. BotNexus: anthropic, openai-completions, openai-responses, openai-compat. Missing 6 providers. |
| **Effort** | Large |

### AGENT-001 — C# adds retry logic with exponential backoff not present in TS

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent-loop.ts:streamAssistantResponse` |
| **BotNexus Ref** | `AgentLoopRunner.cs:264-317` |
| **Details** | C# adds `ExecuteWithRetryAsync` with maxAttempts=4, 500ms initial backoff doubling each attempt, transient error detection (rate limit, 429, 502-504, timeout). TS has zero retry logic. Enhancement but creates behavioral divergence. |
| **Effort** | N/A (Enhancement divergence) |

### AGENT-002 — C# adds context overflow detection and compaction not in TS

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent-loop.ts` |
| **BotNexus Ref** | `AgentLoopRunner.cs:298-305,319-328` |
| **Details** | C# catches context overflow exceptions (via `ContextOverflowDetector`), compacts messages by keeping max(8, count/3) tail messages, then retries once. TS has no overflow handling — hard-fails. |
| **Effort** | N/A (Enhancement divergence) |

### AGENT-003 — ~~TS mutates context.messages during streaming; C# does not~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Agent |
| **Status** | **✅ FIXED in fdb42be** — `StreamAccumulator.AccumulateAsync` now accepts optional `contextMessages` parameter and tracks streaming partials in the message list, replacing them with the final message on completion. |
| **pi-mono Ref** | `packages/agent/src/agent-loop.ts:280-281,296-297` |
| **BotNexus Ref** | `StreamAccumulator.cs:28-235` |
| **Details** | In TS, the partial message is pushed into `context.messages` on "start" and replaced in-place on each delta. In C#, `StreamAccumulator` only emits AgentEvents — it never touches the messages list. Context.messages does NOT contain the partial during streaming, which could affect mid-stream transforms. |
| **Effort** | Small |

### AGENT-004 — ~~C# skips initial steering poll for follow-up continuations; TS does not~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Agent |
| **Status** | **✅ FIXED in b902c79** — `_skipInitialSteeringPollForNextRun` now only set when drained messages came from the steering queue (`fromSteeringQueue` flag). Follow-up messages no longer skip the steering poll. |
| **pi-mono Ref** | `packages/agent/src/agent.ts:342-344` |
| **BotNexus Ref** | `Agent.cs:262-272` |
| **Details** | In TS `continue()`, when draining follow-up messages, `runPromptMessages` is called WITHOUT `{ skipInitialSteeringPoll: true }`. Only steering messages set that flag. In C#, `_skipInitialSteeringPollForNextRun` is ALWAYS set to true for both steering and follow-up messages. This means C# skips the initial steering poll even for follow-up messages, potentially dropping concurrent steering messages. Fix: Only set when drained messages came from the steering queue. |
| **Effort** | Small |

### AGENT-005 — C# swallows listener exceptions; TS propagates them

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:535-537` |
| **BotNexus Ref** | `Agent.cs:636-643` |
| **Details** | TS `Agent.processEvents` iterates listeners and awaits each without catching — a throwing listener aborts the loop. C# `HandleEventAsync` catches non-cancellation exceptions and routes to `OnDiagnostic`, then continues to the next listener. A broken listener in TS crashes the run, while in C# it's silently logged. |
| **Effort** | Small |

### CODING-003 — ~~System prompt is vastly simplified vs pi-mono~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **Status** | **✅ FIXED in b8b9abc** — `SystemPromptBuilder` now includes: tool-aware guidelines that adapt based on enabled tools, current date/time (ISO 8601), `customPrompt` support that replaces the entire base prompt, and `CustomInstructions` appended as final section. |
| **pi-mono Ref** | `packages/coding-agent/src/core/system-prompt.ts:28-168` |
| **BotNexus Ref** | `SystemPromptBuilder.cs:28-190` |
| **Details** | pi-mono's system prompt includes: tool-aware guidelines that adapt based on which tools are enabled, current date, cwd with forward-slash normalization, `customPrompt` support that replaces the entire base prompt, `appendSystemPrompt` support. BotNexus has: static guidelines, no date, no documentation references, no tool-conditional guidelines, no custom/append prompt support. |
| **Effort** | Medium |

### CODING-004 — Session fork, continue-recent, in-memory, cross-project search missing

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/session-manager.ts` |
| **BotNexus Ref** | `Session/SessionManager.cs` |
| **Details** | pi-mono's SessionManager supports: `forkFrom()` to branch a session, `continueRecent()` to auto-resume the last session, `inMemory()` for no-persist mode, `listAll()` for cross-project global session search, and the session picker TUI. BotNexus has only `CreateSessionAsync` and `ResumeSessionAsync`. |
| **Effort** | Large |

### CODING-005 — Model resolution lacks scoped models, cycling, pattern matching

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/model-resolver.ts` |
| **BotNexus Ref** | `CodingAgent.cs:264-298` |
| **Details** | pi-mono supports: `--model provider/pattern` syntax, pattern matching with fuzzy matching, `--models` for comma-separated scope lists enabling Ctrl+P cycling, thinking level embedded in model pattern (e.g., `"claude-3.5:high"`), `ScopedModel` with per-model thinking overrides, model fallback with user-facing messages. BotNexus does basic exact lookup. |
| **Effort** | Medium |

### CODING-006 — Prompt template system completely missing

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/prompt-templates.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono supports prompt templates: `.md` files in global/project prompts directories invoked via `/templateName`. Templates support `$1`, `$2`, `$@`, `$ARGUMENTS`, `${@:N}` positional argument substitution. Loaded from global `~/.pi/agent/prompts/`, project `.pi/prompts/`, and explicit `--prompt-template` paths. |
| **Effort** | Medium |

### CODING-007 — Multiple slash commands missing from InteractiveLoop

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/slash-commands.ts`, `src/modes/interactive/` |
| **BotNexus Ref** | `Cli/InteractiveLoop.cs:198-323` |
| **Details** | BotNexus has: /login, /logout, /model, /thinking, /session, /clear, /help, /quit, /exit. pi-mono additionally supports: /compact, /export, /share, /undo, /config, /sessions, /doctor, /models, /mode, prompt templates via /templateName, and extension-provided commands. |
| **Effort** | Medium |

### CODING-008 — SettingsManager with per-project and global user settings missing

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/settings-manager.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono has a `SettingsManager` that merges settings from multiple sources: built-in defaults, global `~/.pi/agent/settings.json`, project `.pi/settings.json`. Manages: default model, provider, theme, session directory, quiet startup, image auto-resize, enabled models, custom instructions. |
| **Effort** | Medium |

### CODING-009 — ~~Skills loading misses validation rules~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **Status** | **✅ FIXED in 0bcd09f** — Validates: no leading/trailing hyphens, no consecutive hyphens, description required, max 1024 chars. |
| **pi-mono Ref** | `packages/coding-agent/src/core/skills.ts:92-131` |
| **BotNexus Ref** | `Extensions/SkillsLoader.cs:168-210` |
| **Details** | pi-mono validates: name must match parent directory, no leading/trailing hyphens, no consecutive hyphens, max 64 chars, description required, description max 1024 chars. Also respects `.gitignore`, `.ignore`, `.fdignore` files. BotNexus only checks lowercase-alphanumeric-hyphen and max 64 chars. |
| **Effort** | Small |

### CODING-010 — ~~ContextFileDiscovery missing additional file patterns~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **Status** | **✅ FIXED in 405d7d8** — Discovers: `.github/copilot-instructions.md`, `INSTRUCTIONS.md`, `AGENTS.md`, `.botnexus-agent/AGENTS.md`. Walks from cwd to git root. |
| **pi-mono Ref** | `packages/coding-agent/src/core/resource-loader.ts` |
| **BotNexus Ref** | `Utils/ContextFileDiscovery.cs:110-118` |
| **Details** | BotNexus discovers: copilot-instructions.md, AGENTS.md, .botnexus-agent/AGENTS.md. pi-mono additionally discovers: `.copilot-codegeneration.md`, `INSTRUCTIONS.md`, `.pi/AGENTS.md` (config-dir based). BotNexus does walk up to git root (good), but should add INSTRUCTIONS.md and config-dir AGENTS.md patterns. |
| **Effort** | Small |

### CODING-011 — HTML export feature completely missing

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/export-html/` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono supports exporting sessions to self-contained HTML files with ANSI color rendering, tool call visualization, and a styled template. Invoked via `--export` or `/export`. |
| **Effort** | Medium |

### CODING-012 — Image reading uses System.Drawing (Windows-only)

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/tools/read.ts` |
| **BotNexus Ref** | `Tools/ReadTool.cs:200-228` |
| **Details** | BotNexus uses `System.Drawing.Common` for image resize which throws `PlatformNotSupportedException` on Linux/macOS. pi-mono uses photon (Wasm-based) or sharp for cross-platform handling. Switch to SkiaSharp or ImageSharp. Also missing: EXIF orientation correction. |
| **Effort** | Medium |

### CODING-013 — @file argument processing completely missing

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/cli/file-processor.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono supports `@filepath` arguments that read file contents and inject them into the initial prompt. Handles glob expansion, image auto-resize, MIME detection, and combining text+image content. |
| **Effort** | Medium |

### CODING-014 — ~~Piped stdin content detection and processing missing~~ ✅ FIXED

| Field | Value |
|-------|-------|
| **Severity** | Major |
| **Area** | CodingAgent |
| **Status** | **✅ FIXED in 405d7d8** — `ReadPipedStdinAsync()` checks `Console.IsInputRedirected`, reads all piped input, combines with CLI prompt. |
| **pi-mono Ref** | `packages/coding-agent/src/main.ts:43-60` |
| **BotNexus Ref** | `Program.cs:253-267` |
| **Details** | pi-mono detects when stdin is not a TTY (piped input), reads all stdin content, and automatically switches to print mode. Enables `cat file.txt | pi "explain this"` workflow. BotNexus always assumes interactive terminal. |
| **Effort** | Small |

---

## Minor Findings

### PROV-011 — StopReason has extra values Refusal and Sensitive not in pi-mono

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:182` |
| **BotNexus Ref** | `Models/Enums.cs:35-39` |
| **Details** | BotNexus adds Refusal and Sensitive stop reasons. Creates divergence from TypeScript contract. Document as intentional extensions. |
| **Effort** | Small |

### PROV-012 — ThinkingBudgets has ExtraHigh field not present in pi-mono

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:48-53` |
| **BotNexus Ref** | `Models/ThinkingBudgets.cs:12` |
| **Details** | pi-mono does not include an xhigh/ExtraHigh budget. BotNexus adds ExtraHigh to the record. Forward-looking enhancement. |
| **Effort** | Small |

### PROV-013 — OpenAICompletionsCompat has 3 extra fields not in pi-mono

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:255-284` |
| **BotNexus Ref** | `Compatibility/OpenAICompletionsCompat.cs:9,13,14` |
| **Details** | BotNexus adds `SupportsStoreParam` (redundant with `SupportsStore`?), `SupportsTemperature`, and `SupportsMetadata`. Review whether SupportsStoreParam should be removed. |
| **Effort** | Small |

### PROV-014 — OpenAIResponsesCompat interface not ported

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:287-289` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono defines empty `OpenAIResponsesCompat` interface reserved for future use. Low priority since TS type is empty. |
| **Effort** | Small |

### PROV-015 — TextSignatureV1 type not ported

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:131-135` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono defines structured `TextSignatureV1` type for OpenAI responses metadata. Currently stored as opaque string in both. |
| **Effort** | Small |

### PROV-016 — calculateCost mutates usage in pi-mono vs returns new UsageCost in BotNexus

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/models.ts:39-46` |
| **BotNexus Ref** | `Registry/ModelRegistry.cs:45-54` |
| **Details** | pi-mono mutates input `usage.cost` in-place. BotNexus creates new immutable `UsageCost` record. C# approach is safer but callers must use returned value. |
| **Effort** | Small |

### PROV-017 — supportsXhigh uses model ID pattern matching vs static flag

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/models.ts:55-65` |
| **BotNexus Ref** | `Registry/ModelRegistry.cs:56-59` |
| **Details** | pi-mono dynamically checks model ID strings at runtime. BotNexus uses static boolean `SupportsExtraHighThinking` set at registration. C# approach is cleaner but custom models must explicitly set the flag. |
| **Effort** | Small |

### PROV-018 — ContextOverflowDetector string overload doesn't check stopReason

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/utils/overflow.ts:113-114` |
| **BotNexus Ref** | `Utilities/ContextOverflowDetector.cs:43-52` |
| **Details** | pi-mono only tests error patterns when `message.stopReason` is "error". BotNexus's string-based overload has no stopReason gate. |
| **Effort** | Small |

### PROV-019 — MessageTransformer skips Refusal/Sensitive messages not in pi-mono

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/providers/transform-messages.ts:132-134` |
| **BotNexus Ref** | `Utilities/MessageTransformer.cs:76-79` |
| **Details** | pi-mono only skips assistant messages with stopReason "error" or "aborted". BotNexus also skips Refusal and Sensitive, which may lose useful context. |
| **Effort** | Small |

### PROV-022 — ToolResultMessage Content allows ThinkingContent/ToolCallContent — too permissive

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:207` |
| **BotNexus Ref** | `Models/Messages.cs:32` |
| **Details** | pi-mono restricts `ToolResultMessage.content` to `TextContent | ImageContent`. BotNexus uses base `ContentBlock` which also allows ThinkingContent and ToolCallContent. |
| **Effort** | Small |

### PROV-023 — AssistantMessage Content allows ImageContent — too permissive

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:191` |
| **BotNexus Ref** | `Models/Messages.cs:20` |
| **Details** | pi-mono restricts to TextContent/ThinkingContent/ToolCall. BotNexus uses base ContentBlock which also includes ImageContent. |
| **Effort** | Small |

### PROV-024 — BuildBaseOptions resolves defaults differently for Transport/CacheRetention

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/providers/simple-options.ts:3-16` |
| **BotNexus Ref** | `Utilities/SimpleOptionsHelper.cs:31-32` |
| **Details** | pi-mono passes undefined for transport/cacheRetention when not set. BotNexus creates unnecessary object allocations with `new StreamOptions()` defaults. |
| **Effort** | Small |

### PROV-025 — AdjustMaxTokensForThinking has different signature

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/providers/simple-options.ts:22-46` |
| **BotNexus Ref** | `Utilities/SimpleOptionsHelper.cs:85-97` |
| **Details** | pi-mono combines budget resolution and maxTokens adjustment. BotNexus splits into separate functions. Algorithm is the same but callers must compose differently. |
| **Effort** | Small |

### PROV-026 — ThinkingFormat uses string instead of union type

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:274` |
| **BotNexus Ref** | `Compatibility/OpenAICompletionsCompat.cs:21` |
| **Details** | pi-mono restricts ThinkingFormat to 5 known values via union type. BotNexus uses plain string, losing compile-time validation. |
| **Effort** | Small |

### PROV-028 — MaxTokensField uses string instead of union type

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/types.ts:267` |
| **BotNexus Ref** | `Compatibility/OpenAICompletionsCompat.cs:17` |
| **Details** | pi-mono restricts to two known values. BotNexus uses plain string. Consider using an enum or string constants. |
| **Effort** | Small |

### PROV-031 — DoneEvent/ErrorEvent StopReason not constrained to valid subsets

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Streaming |
| **pi-mono Ref** | `packages/ai/src/types.ts:248-249` |
| **BotNexus Ref** | `Streaming/AssistantMessageEvent.cs:66-74` |
| **Details** | TS constrains DoneEvent to "stop"/"length"/"toolUse" and ErrorEvent to "aborted"/"error". C# uses full StopReason enum on both, allowing invalid combos. |
| **Effort** | Small |

### PROV-032 — LlmStream._done field lacks thread-safety guarantees

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Streaming |
| **pi-mono Ref** | `packages/ai/src/utils/event-stream.ts` |
| **BotNexus Ref** | `Streaming/LlmStream.cs:21` |
| **Details** | `_done` is a plain bool with no `volatile` modifier or `Interlocked`. If Push and End are called from different threads, one may not see the other's write. |
| **Effort** | Small |

### PROV-033 — LlmStream allows multiple readers; TS is single-reader

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Streaming |
| **pi-mono Ref** | `packages/ai/src/utils/event-stream.ts` |
| **BotNexus Ref** | `Streaming/LlmStream.cs:14` |
| **Details** | C# sets `SingleReader = false` but events are delivered to only ONE reader. Set `SingleReader = true` for perf optimization. |
| **Effort** | Small |

### PROV-042 — Stop reason mapping: refusal → Error in TS vs Refusal in C# (Anthropic)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Anthropic |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:893` |
| **BotNexus Ref** | `AnthropicProvider.cs:296` |
| **Details** | TS maps "refusal" to "error". C# maps to StopReason.Refusal. Intentional C# enhancement. |
| **Effort** | Small |

### PROV-043 — PI_CACHE_RETENTION environment variable fallback missing (Anthropic)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Anthropic |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:39-47` |
| **BotNexus Ref** | MISSING |
| **Details** | TS checks `PI_CACHE_RETENTION` env var as fallback. C# always defaults to `CacheRetention.Short`. |
| **Effort** | Small |

### PROV-044 — Tool result is_error sends null instead of false/omitted (Anthropic)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Anthropic |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:813` |
| **BotNexus Ref** | `AnthropicMessageConverter.cs:312` |
| **Details** | TS sends `is_error: false` (or omits). C# sends null for non-error. Anthropic API expects boolean; null could cause validation issues. |
| **Effort** | Small |

### PROV-079 — Tool result content structure differs with multiple text blocks (Anthropic)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Anthropic |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:106-153` |
| **BotNexus Ref** | `AnthropicMessageConverter.cs:257-314` |
| **Details** | No images, multiple text: TS concatenates to single string; C# creates block array. With images+text: TS preserves interleaving order; C# joins ALL text then appends images. Fix: match TS wire format. |
| **Effort** | Medium |

### PROV-080 — Cache control on last user message applies to any block type (Anthropic)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Anthropic |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:846-849` |
| **BotNexus Ref** | `AnthropicMessageConverter.cs:359-362` |
| **Details** | TS only applies `cache_control` to last block if type is text/image/tool_result. C# applies regardless. |
| **Effort** | Small |

### PROV-081 — Tool schema normalization differs (Anthropic)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Anthropic |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:871-882` |
| **BotNexus Ref** | `AnthropicMessageConverter.cs:35-67` |
| **Details** | TS always reconstructs with only type/properties/required, stripping extras. C# returns as-is if already has `type: object`. |
| **Effort** | Small |

### PROV-082 — Missing opencode provider in compat detection (OpenAI Completions)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAI Completions |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:816-817` |
| **BotNexus Ref** | `OpenAICompletionsProvider.cs:1081-1141` |
| **Details** | TS `detectCompat` includes opencode/opencode.ai. C# has no entry. |
| **Effort** | Small |

### PROV-083 — Missing function-based tool_choice variant (OpenAI Completions)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAI Completions |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:56` |
| **BotNexus Ref** | `OpenAICompletionsOptions.cs:12` |
| **Details** | TS `toolChoice` union includes `{type:"function",function:{name:string}}`. C# `ToolChoice` is `string?` only. |
| **Effort** | Small |

### PROV-084 — Error metadata.raw enrichment missing (OpenAI Completions)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAI Completions |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:296-297` |
| **BotNexus Ref** | `OpenAICompletionsProvider.cs:1156-1190` |
| **Details** | TS appends `error?.error?.metadata?.raw` (OpenRouter diagnostic context). C# doesn't extract raw field. |
| **Effort** | Small |

### PROV-085 — response.output_item.done doesn't reconstruct final text (OpenAI Responses)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses-shared.ts:441` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:665-671` |
| **Details** | TS replaces accumulated text with definitive text from done event. C# uses accumulated delta text. If done event text differs from deltas, C# has stale text. |
| **Effort** | Small |

### PROV-086 — Start event emitted at different times — TS immediate, C# lazy (OpenAI Responses)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses.ts:101` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:467-472` |
| **Details** | TS pushes start before any SSE. C# defers until first `output_item.added`. Affects UX timing. |
| **Effort** | Small |

### PROV-087 — ConvertResponsesTools options.strict not passable (OpenAI Responses)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses-shared.ts:267-276` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:420-436` |
| **Details** | TS accepts `ConvertResponsesToolsOptions.strict`. C# hardcodes `strict=false`. |
| **Effort** | Small |

### PROV-088 — Empty user message after image filtering not skipped (OpenAI Responses)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses-shared.ts:152-155` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:262-292` |
| **Details** | TS skips if 0 content parts remain after image filtering. C# still adds potentially empty message. |
| **Effort** | Small |

### PROV-089 — Aborted request handling emits DoneEvent instead of ErrorEvent (OpenAI Responses)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses.ts:108-114` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:986-1001` |
| **Details** | TS produces error event with stopReason "aborted". C# pushes `DoneEvent(StopReason.Aborted)`. Consumers expecting ErrorEvent for aborts would miss it. |
| **Effort** | Small |

### PROV-090 — CacheRetention env var fallback not ported (OpenAI Responses)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses.ts:27-35` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:61` |
| **Details** | Same as PROV-043 but for Responses provider. TS checks `PI_CACHE_RETENTION`. C# defaults Short. |
| **Effort** | Small |

### PROV-091 — Strict mode sends true in BotNexus vs false in pi-mono (OpenAICompat)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:728` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:344` |
| **Details** | `strict: true` requires JSON Schema compliance with `additionalProperties: false`. Will reject many schemas. Fix: change to false. |
| **Effort** | Small |

### PROV-092 — Missing OpenRouter Anthropic cache_control injection (OpenAICompat)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:457-488` |
| **BotNexus Ref** | MISSING |
| **Details** | TS adds `cache_control` breakpoints for OpenRouter→Anthropic routing. C# has no equivalent. |
| **Effort** | Small |

### PROV-093 — Tool result images not forwarded as separate user messages (OpenAICompat)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:644-708` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:516-533` |
| **Details** | TS extracts images from tool results and appends as separate user message. C# drops images entirely. |
| **Effort** | Medium |

### PROV-094 — Thinking block handling differs (OpenAICompat)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:588-603` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:483-484` |
| **Details** | TS converts thinking to plain text without tags "to avoid model mimicking them." C# wraps in `<thinking>...</thinking>`. |
| **Effort** | Small |

### PROV-095 — Empty/errored assistant messages not filtered (OpenAICompat)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:631-641` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:467-513` |
| **Details** | TS skips assistant messages with no content and no tool calls. C# always emits. Some providers reject empty assistant messages. |
| **Effort** | Small |

### PROV-096 — Developer role not gated on model.reasoning (OpenAICompat)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:515-516` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:373` |
| **Details** | TS requires BOTH `model.reasoning` AND `supportsDeveloperRole`. C# only checks `supportsDeveloperRole`. |
| **Effort** | Small |

### PROV-097 — Synthetic assistant bridge content differs (OpenAICompat)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:527-531` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:405-409` |
| **Details** | TS: "I have processed the tool results." C#: "". Some providers reject empty strings. Fix: match TS text. |
| **Effort** | Small |

### PROV-098 — Missing text sanitization on outbound messages (OpenAICompat)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:517,543-544,580,662` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:419-465` |
| **Details** | TS sanitizes all outbound text. C# only sanitizes inbound streaming deltas. Could send invalid unicode to compat servers. |
| **Effort** | Small |

### PROV-099 — Missing user message filtering when model doesn't support images (OpenAICompat)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:555-558` |
| **BotNexus Ref** | `OpenAICompatProvider.cs:453-455` |
| **Details** | TS skips entire message if empty after image filtering. C# may emit empty user message. |
| **Effort** | Small |

### PROV-100 — CopilotHeaders.InferInitiator returns "agent" on empty messages; TS returns "user"

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Copilot |
| **pi-mono Ref** | `packages/ai/src/providers/github-copilot-headers.ts:6-7` |
| **BotNexus Ref** | `CopilotHeaders.cs:18-19` |
| **Details** | TS: if last is undefined, returns "user". C#: if messages empty, last is null, falls to else → "agent". Fix: return "user" when no messages. |
| **Effort** | Small |

### PROV-101 — Hardcoded Editor-Version and Editor-Plugin-Version in token exchange (Copilot)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Copilot |
| **pi-mono Ref** | N/A |
| **BotNexus Ref** | `CopilotOAuth.cs:149-150` |
| **Details** | Uses "vscode/1.99.0" and "copilot-chat/0.26.0". Should be configurable or auto-detected. GitHub may reject old versions. |
| **Effort** | Small |

### PROV-102 — Copilot token exchange endpoints.api parsed but discarded

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Copilot |
| **pi-mono Ref** | N/A |
| **BotNexus Ref** | `CopilotOAuth.cs:159-164` |
| **Details** | The API endpoint from token response is extracted but discarded at both call sites. Should be stored and used as base URL. |
| **Effort** | Small |

### PROV-103 — BotNexus Gemini models registered as openai-completions instead of native API

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Providers — Google |
| **pi-mono Ref** | `packages/ai/src/providers/google.ts` |
| **BotNexus Ref** | `Registry/BuiltInModels.cs:49-52` |
| **Details** | BotNexus routes Gemini through github-copilot OpenAI-completions shim. Loses Google-specific features. |
| **Effort** | Small |

### AGENT-006 — TS uses case-sensitive tool lookup; C# uses case-insensitive

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent-loop.ts:479` |
| **BotNexus Ref** | `ToolExecutor.cs:228-229` |
| **Details** | TS finds tools with strict equality (`===`). C# uses `OrdinalIgnoreCase`. A tool named "ReadFile" would match a call to "readfile" in C# but not TS. |
| **Effort** | Small |

### AGENT-007 — TS sets streamingMessage for ALL message types; C# only for assistant

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:493-495` |
| **BotNexus Ref** | `Agent.cs:653-658` |
| **Details** | C# behavior is arguably more correct since streaming only applies to assistant generation. |
| **Effort** | Small |

### AGENT-008 — C# updates State.Messages during streaming; TS does not

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:498-499` |
| **BotNexus Ref** | `Agent.cs:659-666` |
| **Details** | C# `ProcessEvent` replaces the last message in `_state.Messages` with updated snapshot during streaming. TS only updates `streamingMessage`. UI consumers will see different content mid-stream. |
| **Effort** | Small |

### AGENT-009 — C# catches BeforeToolCall hook exceptions differently

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent-loop.ts:491-508` |
| **BotNexus Ref** | `ToolExecutor.cs:262-269` |
| **Details** | Both produce error results but error message format differs. C# has explicit catch with "BeforeToolCall hook failed:" prefix. |
| **Effort** | Small |

### AGENT-010 — C# silently swallows AfterToolCall exceptions; TS propagates them

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent-loop.ts:573-592` |
| **BotNexus Ref** | `ToolExecutor.cs:345-349` |
| **Details** | In TS, if afterToolCall throws, exception propagates. In C#, exceptions are caught and original unmodified result is returned silently. |
| **Effort** | Small |

### AGENT-011 — Event payload structure differs (MessageUpdateEvent)

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/types.ts:336` |
| **BotNexus Ref** | `AgentEvent.cs:97-107` |
| **Details** | TS carries raw `AssistantMessageEvent`. C# decomposes into explicit fields: ContentDelta, IsThinking, ToolCallId, etc. Improvement but changes API surface. |
| **Effort** | Medium |

### AGENT-012 — ThinkingLevel defaults to "off" in TS; null in C#

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:73` |
| **BotNexus Ref** | `AgentState.cs:45` |
| **Details** | TS initializes to "off". C# to null. If the LLM provider treats "no reasoning parameter" differently from "reasoning=off", this causes behavioral differences. |
| **Effort** | Small |

### AGENT-013 — C# merges external message delegates with queue drain

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:423-428` |
| **BotNexus Ref** | `Agent.cs:593-611` |
| **Details** | C# supports external message injection sources merged with the queue. Enhancement but creates divergence. |
| **Effort** | N/A (Enhancement) |

### AGENT-014 — TS abort() is synchronous; C# AbortAsync() awaits settlement

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:286-287` |
| **BotNexus Ref** | `Agent.cs:338-366` |
| **Details** | TS abort is fire-and-forget. C# AbortAsync blocks until run settles. C# also introduces an Aborting status. |
| **Effort** | N/A (Enhancement) |

### AGENT-015 — TS exposes signal property; C# does not expose CancellationToken

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:281-282` |
| **BotNexus Ref** | MISSING |
| **Details** | TS Agent exposes `signal` for external cancellation watching. C# Agent does not expose internal `CancellationTokenSource`. |
| **Effort** | Small |

### AGENT-016 — ThinkingBudgets per-level token budgets not ported

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:179-180` |
| **BotNexus Ref** | MISSING |
| **Details** | TS Agent has `thinkingBudgets` property forwarded to the stream function. C# `AgentOptions`/`AgentLoopConfig` has no equivalent. |
| **Effort** | Medium |

### AGENT-017 — onPayload streaming callback not ported

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:100,167` |
| **BotNexus Ref** | MISSING |
| **Details** | TS Agent has `onPayload` callback for raw provider payload inspection. C# has no equivalent. |
| **Effort** | Small |

### AGENT-021 — TS AgentToolResult<T> is generic; C# uses object?

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/types.ts:281-286` |
| **BotNexus Ref** | `AgentToolResult.cs:13` |
| **Details** | TS is generic over details payload type. C# uses `object?`, losing type information. |
| **Effort** | Small |

### CODING-015 — Config migration system completely missing

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/migrations.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono handles auth provider renames, config directory moves, and deprecation warnings. Will cause issues as config format evolves. |
| **Effort** | Small |

### CODING-016 — stdout takeover/restore for non-interactive modes missing

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/output-guard.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono intercepts stdout in non-interactive modes to prevent stray console output from corrupting structured output. |
| **Effort** | Small |

### CODING-017 — No event bus for decoupled component communication

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/event-bus.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono has typed EventBus. BotNexus uses direct method calls. Functionally equivalent for current scope but may limit extensibility. |
| **Effort** | Small |

### CODING-018 — Keybinding system for interactive mode missing

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/keybindings.ts` (303 lines) |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono supports configurable keybindings: Ctrl+P (model cycling), Ctrl+L (clear), Ctrl+T (thinking toggle), Ctrl+K (compact). BotNexus only handles Ctrl+C. |
| **Effort** | Medium |

### CODING-019 — Startup performance timing missing

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/timings.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono tracks startup performance with named timing checkpoints. BotNexus has no startup timing instrumentation. |
| **Effort** | Small |

### CODING-020 — GitUtils missing commit log, diff, stash operations

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/utils/git.ts` |
| **BotNexus Ref** | `Utils/GitUtils.cs` |
| **Details** | BotNexus has only branch and status queries. Missing operations may be needed as features are added. |
| **Effort** | Small |

### CODING-021 — Clipboard integration for image pasting missing

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/utils/clipboard.ts`, `clipboard-image.ts`, `clipboard-native.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono supports pasting images from clipboard in interactive mode. BotNexus has no clipboard support. |
| **Effort** | Medium |

### CODING-022 — Package management commands missing

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/package-manager-cli.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono supports `install/uninstall/update <extension>` and `config` commands. BotNexus has no equivalent. |
| **Effort** | Medium |

### CODING-023 — Shell tool working directory not set from agent's working directory

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/tools/bash.ts` |
| **BotNexus Ref** | `Tools/ShellTool.cs:114-122` |
| **Details** | BotNexus creates `ProcessStartInfo` without setting `WorkingDirectory`, so commands execute in process's current directory rather than agent's configured working directory. Fix: add `startInfo.WorkingDirectory = workingDirectory`. |
| **Effort** | Small |

### CODING-024 — Config merge strategy differs: BotNexus replaces lists, pi-mono merges them

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/resolve-config-value.ts` |
| **BotNexus Ref** | `CodingAgentConfig.cs:137-166` |
| **Details** | BotNexus replaces AllowedCommands and BlockedPaths entirely when override has items. pi-mono supports additive merging. A project-local config could remove global blocked paths. Consider additive merging for security-sensitive lists. |
| **Effort** | Small |

### CODING-025 — No --version flag or version display

| Field | Value |
|-------|-------|
| **Severity** | Minor |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/main.ts:410-413` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono displays version with `--version/-v` flag and shows it in TUI header. BotNexus has no version tracking or display. |
| **Effort** | Small |

---

## Enhancement Findings

### PROV-020 — ContextOverflowDetector has Exception overload (C# enhancement)

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers |
| **pi-mono Ref** | N/A |
| **BotNexus Ref** | `Utilities/ContextOverflowDetector.cs:54-72` |
| **Details** | BotNexus adds `IsContextOverflow(Exception?)` which recursively checks exception messages including AggregateException unwrapping. Valuable C#-specific enhancement. |
| **Effort** | N/A |

### PROV-021 — getOverflowPatterns test helper not ported

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/utils/overflow.ts:136-138` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono exports `getOverflowPatterns()` for test access. Low priority. |
| **Effort** | Small |

### PROV-027 — All BuiltInModels use FreeCost (0,0,0,0) — no cost tracking

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers |
| **pi-mono Ref** | `packages/ai/src/models.generated.ts` |
| **BotNexus Ref** | `Registry/BuiltInModels.cs:30` |
| **Details** | pi-mono's models have precise costs (e.g., Nova Lite: input=0.06, output=0.24, cacheRead=0.015). All BotNexus models use `FreeCost(0,0,0,0)`. Cost tracking is effectively disabled. |
| **Effort** | Medium |

### PROV-034 — ToolCallContent has extra ThoughtSignature field (C# extension)

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers — Streaming |
| **pi-mono Ref** | `packages/ai/src/types.ts:ToolCall` |
| **BotNexus Ref** | `ContentBlock.cs:32-37` |
| **Details** | C# ToolCallContent includes optional `ThoughtSignature` for extended-thinking verification. |
| **Effort** | Small |

### PROV-035 — C# LlmStream is specialized; TS uses generic EventStream<T,R>

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers — Streaming |
| **pi-mono Ref** | `packages/ai/src/utils/event-stream.ts:4-66` |
| **BotNexus Ref** | `Streaming/LlmStream.cs:11` |
| **Details** | TS has generic `EventStream<T,R>`. C# hardcodes terminal-event detection. Consider generic base if additional stream types needed. |
| **Effort** | Medium |

### PROV-036 — Signature captured for all block types in C# (improvement)

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers — Anthropic |
| **pi-mono Ref** | `packages/ai/src/providers/anthropic.ts:358-364` |
| **BotNexus Ref** | `AnthropicStreamParser.cs:248-253` |
| **Details** | TS only processes `signature_delta` for thinking blocks. C# accumulates for all block types. More correct for fine-grained-tool-streaming beta. |
| **Effort** | Small |

### PROV-104 — C# extracts cache_write_tokens — TS does not (OpenAI Responses improvement)

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers — OpenAI Responses |
| **pi-mono Ref** | `packages/ai/src/providers/openai-responses-shared.ts:471-480` |
| **BotNexus Ref** | `OpenAIResponsesProvider.cs:796-802` |
| **Details** | TS hardcodes `cacheWrite: 0`. C# reads `cache_write_tokens` and subtracts from input. More correct. |
| **Effort** | Small |

### PROV-105 — PreConfiguredModels missing factories for cloud providers

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | `packages/ai/src/providers/openai-completions.ts:806-854` |
| **BotNexus Ref** | `PreConfiguredModels.cs:1-106` |
| **Details** | PreConfiguredModels has Ollama/vLLM/LMStudio/SGLang only. Add factories for Cerebras, xAI, DeepSeek, Groq. |
| **Effort** | Small |

### PROV-106 — OpenAICompat is a positive BotNexus-only divergence

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers — OpenAICompat |
| **pi-mono Ref** | N/A |
| **BotNexus Ref** | `BotNexus.Agent.Providers.OpenAICompat/*` |
| **Details** | Valid addition for local model servers. Needs edge-case handling from openai-completions.ts compat profiles. |
| **Effort** | N/A |

### PROV-107 — CopilotOAuth.LoginAsync defers token exchange to first use

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers — Copilot |
| **pi-mono Ref** | N/A |
| **BotNexus Ref** | `CopilotOAuth.cs:82` |
| **Details** | Returns GitHub token with `ExpiresAt=0` for lazy exchange. Valid but first API call incurs extra roundtrip. |
| **Effort** | Small |

### PROV-108 — Codex WebSocket transport not available despite Transport enum existing

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Providers — Other |
| **pi-mono Ref** | `packages/ai/src/providers/openai-codex-responses.ts:458-819` |
| **BotNexus Ref** | `Models/Enums.cs:Transport` |
| **Details** | BotNexus has `Transport` enum (Sse/WebSocket/Auto) suggesting planned support but no implementation. |
| **Effort** | Large |

### AGENT-020 — agentLoop/agentLoopContinue EventStream wrappers not ported

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent-loop.ts:31-93` |
| **BotNexus Ref** | MISSING |
| **Details** | TS exports `agentLoop()` and `agentLoopContinue()` which return `EventStream` objects. C# only has callback-based emission. |
| **Effort** | Medium |

### AGENT-023 — C# IAgentTool adds GetPromptSnippet() and GetPromptGuidelines()

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/types.ts:292-307` |
| **BotNexus Ref** | `IAgentTool.cs:99-106` |
| **Details** | C# adds optional methods for system prompt integration. TS tools have no equivalent. |
| **Effort** | N/A |

### AGENT-024 — TS prompt() returns void; C# PromptAsync returns messages

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:310-320` |
| **BotNexus Ref** | `Agent.cs:220-233` |
| **Details** | C# returns list of new messages directly. UX improvement. |
| **Effort** | N/A |

### AGENT-025 — C# TurnEndEvent uses AssistantAgentMessage (more precise typing)

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/types.ts:332` |
| **BotNexus Ref** | `AgentEvent.cs:63-66` |
| **Details** | TS uses broad `AgentMessage`. C# narrows to `AssistantAgentMessage`. More precise since turn_end always carries assistant response. |
| **Effort** | N/A |

### AGENT-026 — C# adds explicit AgentStatus enum

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Agent |
| **pi-mono Ref** | `packages/agent/src/agent.ts:87` |
| **BotNexus Ref** | `AgentStatus.cs:1-23` |
| **Details** | TS uses `isStreaming` boolean only. C# has formal Idle/Running/Aborting enum. |
| **Effort** | N/A |

### AGENT-030 — C# adds OnDiagnostic callback for non-fatal runtime diagnostics

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | Agent |
| **pi-mono Ref** | MISSING |
| **BotNexus Ref** | `AgentOptions.cs:49` |
| **Details** | C# supports `OnDiagnostic` callback for logging non-fatal issues. Enhancement for observability. |
| **Effort** | N/A |

### CODING-026 — Rich TUI with themes, Ink components, and footer/status bar missing

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/modes/interactive/` |
| **BotNexus Ref** | `Cli/InteractiveLoop.cs` |
| **Details** | pi-mono has full TUI with themed colors, status bar, markdown rendering, syntax highlighting, keybinding hints. BotNexus uses basic Console I/O. |
| **Effort** | Large |

### CODING-027 — Auth storage missing multi-provider support

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/auth-storage.ts` |
| **BotNexus Ref** | `Auth/AuthManager.cs` |
| **Details** | pi-mono supports multiple providers with independent credentials, runtime API key injection. |
| **Effort** | Small |

### CODING-028 — No user-configurable models.json for custom model definitions

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/model-registry.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono loads user-defined models from `~/.pi/agent/models.json`. BotNexus only uses built-in registry. |
| **Effort** | Medium |

### CODING-029 — ListDirectoryTool depth difference (3 vs 2)

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/tools/read.ts` |
| **BotNexus Ref** | `Tools/ListDirectoryTool.cs:153-258` |
| **Details** | BotNexus lists 3 levels deep; pi-mono lists 2. Could produce unexpectedly large outputs. |
| **Effort** | Small |

### CODING-030 — No diagnostic/doctor command for troubleshooting

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/diagnostics.ts` |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono collects diagnostics from extension/skill loading, settings parsing, model resolution. |
| **Effort** | Small |

### CODING-031 — No tool-level prompt contributions

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/tools/render-utils.ts` |
| **BotNexus Ref** | `SystemPromptBuilder.cs:102-114` |
| **Details** | pi-mono tools contribute one-line prompt snippets. BotNexus built-in tools don't contribute snippets. |
| **Effort** | Small |

### CODING-033 — Compaction parameters should be configurable

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/compaction/` |
| **BotNexus Ref** | `Program.cs:176-208`, `Cli/InteractiveLoop.cs:153-193` |
| **Details** | BotNexus hardcodes compaction parameters inline. Logic is also duplicated between Program.cs and InteractiveLoop.cs. Should be configurable through CodingAgentConfig. |
| **Effort** | Small |

### CODING-034 — Footer/status information provider missing for interactive display

| Field | Value |
|-------|-------|
| **Severity** | Enhancement |
| **Area** | CodingAgent |
| **pi-mono Ref** | `packages/coding-agent/src/core/footer-data-provider.ts` (340 lines) |
| **BotNexus Ref** | MISSING |
| **Details** | pi-mono provides real-time footer data: current model, token usage, context percentage, session ID, keybinding hints. |
| **Effort** | Medium |
