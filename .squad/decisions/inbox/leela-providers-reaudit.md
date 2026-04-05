# Deep Functional Re-Audit: BotNexus.Providers vs pi-mono @mariozechner/pi-ai

**Author:** Leela (Lead / Architect)  
**Requested by:** Jon Bullen (Copilot)  
**Date:** 2025-07-24  
**Scope:** Runtime-critical gaps — things that will BREAK when the coding agent runs  

---

## Executive Summary

After the model catalog was found completely empty at startup, I conducted a line-by-line audit of all 10 areas. I found **5 blockers** that will crash or fail at runtime, **6 degraded** behaviors that produce wrong results, and several cosmetic differences. The most critical: `Openai-Intent` header missing from OpenAI completions (Copilot may reject requests), `store: true` vs `false` semantics inverted, Anthropic adaptive thinking detection is too broad, tool call ID normalization silently corrupts IDs, and OAuth token exchange uses wrong auth header format.

---

## 1. Model Registration / Loading

**Files compared:**
- `pi-mono/packages/ai/src/models.generated.ts` + `models.ts` + `providers/register-builtins.ts`
- `botnexus/src/providers/BotNexus.Providers.Core/Registry/BuiltInModels.cs` + `ModelRegistry.cs`

### Status: ✅ Intentionally scoped — no blocker

pi-mono defines **828 models** across **23 providers** (openrouter, anthropic, openai, bedrock, google, etc.). BotNexus registers **24 models**, all under `github-copilot` provider. This is **by design** — BotNexus targets Copilot-only usage. The 24 Copilot models cover all the APIs we implement (anthropic-messages, openai-completions, openai-responses).

**Models registered:**
- 6 Claude models → `anthropic-messages` API
- 6 Gemini/GPT-4 models → `openai-completions` API
- 11 GPT-5.x models → `openai-responses` API (delegated to OpenAICompat)
- 1 Grok model → `openai-completions` API

**Provider registration at startup** (`Program.cs:167-176`):
1. `BuiltInModels.RegisterAll()` → 24 copilot models
2. `ApiProviderRegistry.Register(CopilotProvider)` → `github-copilot` API
3. `ApiProviderRegistry.Register(AnthropicProvider)` → `anthropic-messages` API
4. `ApiProviderRegistry.Register(OpenAICompletionsProvider)` → `openai-completions` API
5. `ApiProviderRegistry.Register(OpenAICompatProvider)` → `openai-responses` API

🟢 **COSMETIC** — No 1:1 model parity with pi-mono, but that's the correct scope for a Copilot-first agent.

---

## 2. Stream Function Routing

**Files compared:**
- `pi-mono/packages/ai/src/stream.ts` (lines 43-50)
- `botnexus/src/providers/BotNexus.Providers.Core/LlmClient.cs` (lines 26-30)

### Status: ✅ Functionally identical

Both follow the same pattern:
```
StreamSimple(model, context, options?)
  → resolveProvider(model.Api)
  → provider.StreamSimple(model, context, options)
```

API key resolution chain is identical:
1. `options?.ApiKey` (explicit)
2. `EnvironmentApiKeys.GetApiKey(model.Provider)` (env vars)
3. Fallback to `""` or throw

🟢 No issues.

---

## 3. OpenAI Completions Provider

**Files compared:**
- `pi-mono/packages/ai/src/providers/openai-completions.ts`
- `botnexus/src/providers/BotNexus.Providers.OpenAI/OpenAICompletionsProvider.cs`

### 3a. `store` field value inverted

- **Severity:** 🔴 BLOCKER
- **What pi-mono does:** Sets `store: false` (line 381) when `compat.supportsStore` is true. This prevents OpenAI from storing conversations.
- **What we do:** Sets `store: true` (line 162). This stores all conversations in OpenAI's systems.
- **Fix needed:** Change line 162 in `OpenAICompletionsProvider.cs` from `payload["store"] = true` to `payload["store"] = false`.

### 3b. `Openai-Intent` header missing from OpenAI provider

- **Severity:** 🔴 BLOCKER
- **What pi-mono does:** `buildCopilotDynamicHeaders()` in `github-copilot-headers.ts` (line 29) ALWAYS includes `"Openai-Intent": "conversation-edits"` for Copilot models.
- **What we do:** `CopilotHeaders.BuildDynamicHeaders()` does NOT include `Openai-Intent`. The `CopilotProvider.cs` adds it separately (line 73), but the `OpenAICompletionsProvider.cs` uses `CopilotHeaders.BuildDynamicHeaders` which omits it.
- **Impact:** Copilot models routed through OpenAI completions (GPT-4o, Gemini, Grok) will NOT send `Openai-Intent`. Copilot proxy may reject or deprioritize these requests.
- **Fix needed:** Add `"Openai-Intent": "conversation-edits"` to `CopilotHeaders.BuildDynamicHeaders()` — exactly like pi-mono's `buildCopilotDynamicHeaders`.

### 3c. SSE parsing — manual vs SDK

- **Severity:** 🟢 COSMETIC
- **What pi-mono does:** Uses OpenAI SDK's built-in stream iterator — SSE parsing is handled by the SDK.
- **What we do:** Manual line-by-line SSE parsing: strip `data: ` prefix, check `[DONE]`, parse JSON.
- **Fix needed:** None — both produce equivalent results. Our manual approach is actually more explicit about error conditions.

### 3d. Tool call state tracking

- **Severity:** 🟢 COSMETIC
- **What pi-mono does:** Single `currentBlock` variable, tracks tool calls by ID equality.
- **What we do:** `Dictionary<int, state>` keyed by explicit `index` field. Handles multiple concurrent tool calls.
- **Fix needed:** None — our approach is more robust for concurrent tool calls.

---

## 4. Anthropic Provider

**Files compared:**
- `pi-mono/packages/ai/src/providers/anthropic.ts`
- `botnexus/src/providers/BotNexus.Providers.Anthropic/AnthropicProvider.cs`

### 4a. Adaptive thinking model detection too broad

- **Severity:** 🔴 BLOCKER
- **What pi-mono does:** (line 446-453) Checks for SPECIFIC model IDs:
  ```
  modelId.includes("opus-4-6") || modelId.includes("opus-4.6") ||
  modelId.includes("sonnet-4-6") || modelId.includes("sonnet-4.6")
  ```
  Only 4.6 models get adaptive thinking.
- **What we do:** (line 826-828)
  ```csharp
  modelId.Contains("opus-4") || modelId.Contains("sonnet-4")
  ```
  This matches `claude-sonnet-4`, `claude-sonnet-4.5`, `claude-opus-4.5` — ALL of which should use budget-based thinking, not adaptive.
- **Impact:** Models like `claude-sonnet-4` and `claude-sonnet-4.5` will incorrectly get `thinking: { type: "adaptive" }` instead of `thinking: { type: "enabled", budget_tokens: N }`. Anthropic API may reject the request or behave unpredictably.
- **Fix needed:** Change `IsAdaptiveThinkingModel` to match only 4.6 variants:
  ```csharp
  private static bool IsAdaptiveThinkingModel(string modelId) =>
      modelId.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
      modelId.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase) ||
      modelId.Contains("sonnet-4-6", StringComparison.OrdinalIgnoreCase) ||
      modelId.Contains("sonnet-4.6", StringComparison.OrdinalIgnoreCase);
  ```

### 4b. Tool call ID normalization: replace-with-empty vs replace-with-underscore

- **Severity:** 🟡 DEGRADED
- **What pi-mono does:** (line 698-699) `id.replace(/[^a-zA-Z0-9_-]/g, "_").slice(0, 64)` — replaces invalid chars with `_`.
- **What we do:** (line 835) `NonAlphanumericRegex().Replace(id, "")` — REMOVES invalid chars entirely.
- **Impact:** ID `"tool|call-123"` → pi-mono: `"tool_call-123"`, BotNexus: `"toolcall-123"`. Downstream tool result matching may fail if the ID doesn't match what the model expects.
- **Fix needed:** Change regex replacement from `""` to `"_"`.

### 4c. Redacted thinking loses semantic text

- **Severity:** 🟡 DEGRADED
- **What pi-mono does:** (line 297-301) Sets thinking text to `"[Reasoning redacted]"` and stores the signature data separately in `thinkingSignature`.
- **What we do:** (line 323-326) Stores the `data` field (base64 signature) in the text accumulator. On block stop (line 404-405), creates `ThinkingContent(accumulated, signature, Redacted: true)` where `accumulated` contains the raw signature data, not human-readable text.
- **Fix needed:** In `HandleContentBlockStart` for `redacted_thinking`, store `"[Reasoning redacted]"` as text, and store `data` in the signature accumulator instead.

### 4d. OAuth beta headers outdated

- **Severity:** 🟡 DEGRADED
- **What pi-mono does:** (line 576) Sends `anthropic-beta: claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14,...`
- **What we do:** (line 769) Sends `anthropic-beta: claude-code,...` — missing the dated version suffix and `oauth-2025-04-20`.
- **Fix needed:** Update to `claude-code-20250219` and add `oauth-2025-04-20` for OAuth mode.

### 4e. OAuth missing `x-app` header and versioned user-agent

- **Severity:** 🟡 DEGRADED
- **What pi-mono does:** (line 577-578) Sends `user-agent: claude-cli/2.1.75` and `x-app: cli`.
- **What we do:** (line 770) Sends `user-agent: claude-cli` — no version, no `x-app`.
- **Fix needed:** Add version to user-agent and include `x-app: cli`.

### 4f. Total tokens calculation excludes cache tokens

- **Severity:** 🟡 DEGRADED
- **What pi-mono does:** (line 414-416) `totalTokens = input + output + cacheRead + cacheWrite`
- **What we do:** (line 790) `TotalTokens = Input + Output` — excludes cache read/write tokens.
- **Fix needed:** Include `CacheRead` and `CacheWrite` in `TotalTokens` calculation.

### 4g. Missing "Claude Code" system prompt for OAuth

- **Severity:** 🟢 COSMETIC (for Copilot usage)
- **What pi-mono does:** (line 622-636) For OAuth tokens, prepends `"You are Claude Code, Anthropic's official CLI for Claude."` before the user's system prompt.
- **What we do:** No special handling — just sends the user's system prompt.
- **Impact:** Only matters for direct Anthropic OAuth, not Copilot routing. Low priority.

### 4h. Missing Claude Code tool name conversion

- **Severity:** 🟢 COSMETIC (for Copilot usage)
- **What pi-mono does:** (line 64-101) Converts tool names to Claude Code canonical casing (e.g., `grep` → `Grep`) for OAuth mode.
- **What we do:** No conversion — uses raw tool names.
- **Impact:** Only matters for direct Anthropic OAuth, not Copilot routing. Low priority.

---

## 5. API Key Resolution During Streaming

**Files compared:**
- `pi-mono/packages/ai/src/stream.ts`, `env-api-keys.ts`, `oauth.ts`
- `botnexus/src/providers/BotNexus.Providers.Core/LlmClient.cs`, `EnvironmentApiKeys.cs`, `BotNexus.CodingAgent/Auth/AuthManager.cs`

### Status: ✅ Keys reach HTTP requests

Full trace verified:
1. `AgentLoop` calls `AuthManager.GetApiKeyAsync()` → returns Copilot session token (`tid=...`)
2. Token passed to `LlmClient.StreamSimple()` via `SimpleStreamOptions.ApiKey`
3. `StreamSimple` → `SimpleOptionsHelper.BuildBaseOptions()` → `StreamOptions.ApiKey = apiKey`
4. Provider's `Stream()` picks up `options.ApiKey`
5. HTTP request: `Authorization: Bearer {apiKey}`

🟢 No gap — API key flows correctly from agent to HTTP request.

---

## 6. SSE Parsing

**Files compared:**
- pi-mono relies on SDK stream iterators (OpenAI SDK, Anthropic SDK)
- BotNexus uses manual line-by-line parsing in both providers

### Status: ✅ Functionally equivalent

Both handle:
- `data: ` prefix stripping
- `[DONE]` signal (OpenAI) / `event: message_stop` (Anthropic)
- JSON parsing of each event
- Error events

🟢 **COSMETIC** — Manual parsing is slightly more verbose but works identically.

---

## 7. Model Headers (Copilot)

**Files compared:**
- `pi-mono/packages/ai/src/providers/github-copilot-headers.ts`
- `botnexus/src/providers/BotNexus.Providers.Core/Utilities/CopilotHeaders.cs`

### 7a. `Openai-Intent` header not in shared `CopilotHeaders`

- **Severity:** 🔴 BLOCKER (same as 3b above — repeated for clarity)
- **What pi-mono does:** `buildCopilotDynamicHeaders()` returns `{ "X-Initiator": ..., "Openai-Intent": "conversation-edits", ... }`.
- **What we do:** `CopilotHeaders.BuildDynamicHeaders()` returns `{ "X-Initiator": ... }` only. The `CopilotProvider` adds `Openai-Intent` separately (line 73), but `OpenAICompletionsProvider` does NOT.
- **Fix needed:** Add `Openai-Intent` to `CopilotHeaders.BuildDynamicHeaders()` to match pi-mono exactly.

### 7b. `X-Initiator` logic differs slightly

- **Severity:** 🟢 COSMETIC
- **What pi-mono does:** (line 7) `last.role !== "user" ? "agent" : "user"` — checks only the LAST message.
- **What we do:** (line 19-27) Walks backwards skipping `ToolResultMessage`s, finds last non-tool message.
- **Impact:** Our approach is arguably more correct — tool results shouldn't reset initiator. No fix needed.

---

## 8. OAuth Token Exchange

**Files compared:**
- `pi-mono/packages/ai/src/utils/oauth/github-copilot.ts`
- `botnexus/src/providers/BotNexus.Providers.Copilot/CopilotOAuth.cs`

### 8a. Authorization header format mismatch

- **Severity:** 🔴 BLOCKER
- **What pi-mono does:** (line 251) `Authorization: Bearer ${refreshToken}` — uses `Bearer` prefix.
- **What we do:** (line 146) `Authorization: token ${githubToken}` — uses `token` prefix.
- **Impact:** GitHub API accepts both `token` and `Bearer` for PATs, but the Copilot token endpoint at `api.github.com/copilot_internal/v2/token` may be stricter. If it rejects `token` prefix, the entire OAuth flow breaks.
- **Fix needed:** Change line 146 to `$"Bearer {githubToken}"` to match pi-mono.

### 8b. No dynamic base URL from `proxy-ep` in token

- **Severity:** 🟡 DEGRADED
- **What pi-mono does:** Parses `proxy-ep=<host>` from the session token, converts `proxy.xxx` → `api.xxx`, uses that as the API base URL.
- **What we do:** Uses static `model.BaseUrl` from the model registry. However, we DO extract `endpoints.api` from the token exchange response (lines 159-164).
- **Impact:** If the proxy endpoint changes dynamically (e.g., regional routing), pi-mono auto-adapts via token parsing. We partially adapt via the `endpoints.api` response field but don't fall back to token parsing.
- **Fix needed:** Low priority — the `endpoints.api` extraction covers the main case. Consider adding token parsing as fallback.

---

## 9. SimpleStreamOptions → StreamOptions

**Files compared:**
- `pi-mono/packages/ai/src/providers/simple-options.ts` (lines 3-16)
- `botnexus/src/providers/BotNexus.Providers.Core/Utilities/SimpleOptionsHelper.cs` (lines 14-30)

### 9a. No 32K `maxTokens` default cap

- **Severity:** 🟢 COSMETIC
- **What pi-mono does:** (line 8) `maxTokens: options?.maxTokens || Math.min(model.maxTokens, 32000)` — caps at 32K if user doesn't specify.
- **What we do:** Passes through `null` if user doesn't specify, letting the provider decide.
- **Impact:** Models with >32K max tokens will get their full limit in BotNexus. This is arguably better behavior — pi-mono's cap is a safety guard that may truncate output. No fix needed unless we want exact parity.

---

## 10. Error Recovery

**Files compared:**
- `pi-mono/packages/ai/src/providers/openai-completions.ts` (lines 278-300)
- `botnexus/src/providers/BotNexus.Providers.OpenAI/OpenAICompletionsProvider.cs` (lines 664-697)
- `botnexus/src/providers/BotNexus.Providers.Anthropic/AnthropicProvider.cs` error handling

### Status: ✅ Equivalent with minor differences

Both implementations:
- Distinguish abort (cancellation) from error
- Emit partial content in error messages
- Close the stream after error

Minor differences:
- pi-mono extracts `error?.metadata?.raw` for provider metadata — we don't
- pi-mono deletes `.index` properties from blocks before error — we don't (not applicable in C#)

🟢 No issues.

---

## Summary of All Findings

| # | Finding | Severity | Area |
|---|---------|----------|------|
| 1 | `store: true` instead of `store: false` | 🔴 BLOCKER | OpenAI Completions |
| 2 | `Openai-Intent` header missing from OpenAI provider + CopilotHeaders | 🔴 BLOCKER | Copilot Headers |
| 3 | Adaptive thinking matches all 4.x models, not just 4.6 | 🔴 BLOCKER | Anthropic |
| 4 | OAuth token exchange uses `token` prefix, not `Bearer` | 🔴 BLOCKER | OAuth |
| 5 | Tool call ID normalization removes chars instead of replacing with `_` | 🟡 DEGRADED | Anthropic |
| 6 | Redacted thinking stores signature as text, not `[Reasoning redacted]` | 🟡 DEGRADED | Anthropic |
| 7 | OAuth beta header missing version suffix + `oauth-2025-04-20` | 🟡 DEGRADED | Anthropic |
| 8 | OAuth missing `x-app: cli` header and versioned user-agent | 🟡 DEGRADED | Anthropic |
| 9 | Total tokens excludes cache read/write tokens | 🟡 DEGRADED | Anthropic |
| 10 | No dynamic base URL parsing from `proxy-ep` in token | 🟡 DEGRADED | OAuth |
| 11 | No 32K maxTokens default cap (arguably better) | 🟢 COSMETIC | SimpleOptions |
| 12 | X-Initiator walks backwards past tool results (arguably better) | 🟢 COSMETIC | Copilot Headers |
| 13 | Manual SSE parsing vs SDK iterators | 🟢 COSMETIC | All Providers |
| 14 | Missing Claude Code system prompt for OAuth | 🟢 COSMETIC | Anthropic |
| 15 | Missing tool name conversion for OAuth | 🟢 COSMETIC | Anthropic |

---

## Recommended Fix Priority

### Immediate (before next test run):
1. **Fix `store` value** — one-line change, `true` → `false`
2. **Add `Openai-Intent` to CopilotHeaders.BuildDynamicHeaders** — prevents Copilot rejection
3. **Fix adaptive thinking detection** — restrict to 4.6 models only
4. **Fix OAuth auth header** — `token` → `Bearer`

### Next sprint:
5. Fix tool call ID normalization (`""` → `"_"`)
6. Fix redacted thinking text
7. Update OAuth beta headers
8. Fix total tokens calculation
9. Add `x-app` header and version user-agent

### Backlog:
10-15. Cosmetic items — no runtime impact
