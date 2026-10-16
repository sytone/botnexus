# Compaction policy diagnostics

For developers integrating session diagnostics. These are the backend and `/context` command diagnostics slices of #4888; they do not add a settings UI or change compaction policy.

## Read the current policy

The existing authenticated context route, `GET /api/agents/{agentId}/sessions/{sessionId}/context`, adds a nullable `compactionPolicy` object. Existing fields retain their names and semantics, including the handle-bound `contextBudget`, `contextWindowTokens`, token sections and `usagePercent`.

Each HTTP read resolves policy afresh through `ISessionCompactionDiagnostics`. The service loads the session's actual conversation link, checks its agent and local-world ownership, then uses the existing context-window and ceiling resolvers. It reads current monitored compaction options and computes both thresholds through `CompactionThreshold.Resolve`. It does not copy the handle's older model-binding snapshot or infer a conversation from the session ID.

| Field under `compactionPolicy` | Meaning |
| --- | --- |
| `scopedContextWindowTokens` | Current working window returned by the context-window resolver. Null when scope cannot establish one. |
| `contextWindowTokens` | Window used by runtime compaction policy. When scope is unknown, this is the configured compaction fallback, not a newly invented capacity. |
| `tokenThresholdRatio` | Current configured fraction for the ratio safety threshold. |
| `ratioSafetyThresholdTokens` | Ratio-only threshold from the shared policy, without the ceiling cap. |
| `compactionTokenCeiling` | Resolved positive ceiling, or null if none applies. This is not model capacity. |
| `compactionTokenCeilingSource` | `conversation`, `agent` or `world`; null if no scoped ceiling applies. |
| `effectiveThresholdTokens` | Shared runtime threshold after the ceiling cap. Token pressure triggers above this value, not at equality. |
| `localEstimatedTokens` | Existing compactor estimate over persisted LLM-visible history. Excludes system prompt, tool definitions and injected workspace context. |
| `providerPromptTokens` | Real count from the last completed provider request, or null when unavailable, malformed or cleared. It is not a measurement of a new request. |
| `providerMeasurementSource` | `lastProviderPromptTokens` only when that producer-backed count is valid; otherwise null. |
| `providerToEstimateRatio` | Existing measurement ratio of provider count to local history estimate; null if not computable. |

A configured fallback policy window is distinct from known model capacity. Clients must not use it to replace an unknown legacy `contextWindowTokens` field. The legacy `totalEstimatedTokens` covers the handle's broader diagnostic context; it is not interchangeable with the persisted-history estimate used by compaction.

## Availability and safety

- No resident handle still returns the existing transient 404 body. This read never creates an agent handle or process.
- A handle without context diagnostics still returns 404.
- Missing session/conversation state, mismatched ownership or a foreign world makes `compactionPolicy` null. Direct callers without the optional service also receive null.
- Policy read or validation failures propagate through the existing API error handling. The service does not substitute stale or relaxed policy.
- The new object contains scalar policy/count fields only: no prompt text, configuration object, metadata bag or credentials.
- Token diagnostics do not claim to report the separate oversized-entry byte trigger, compaction circuit-breaker state or whether compaction will succeed.
- The provider measurement is cleared by existing successful-compaction behavior; unknown measurements remain null, never zero placeholders.

## `/context` command

The built-in `/context` command also calls `ISessionCompactionDiagnostics` on every successful resident-handle read. It reports the fresh scoped working window, resolved ceiling and source, configured ratio, ratio safety threshold and effective token trigger threshold. It reuses the service's values, not a second threshold calculation or the handle's older model binding.

When the scoped window is unknown, the command labels the runtime policy window as a compaction fallback, not known model capacity. Breakdown percentages are unavailable in that case. An unavailable policy stays unavailable; the command does not invent thresholds. Legacy callers without the optional service still receive the handle breakdown and a clearly labelled configured fallback.

The persisted LLM-visible history estimate, last completed provider prompt count and source, and provider-to-estimate ratio are separate from the broader handle context estimate. Missing provider measurements and ratios say `unavailable`, not zero. Output contains only counts and allowed source labels, never prompt text or arbitrary metadata. A trigger threshold is not a hard cap or a promise that compaction will succeed; the byte trigger and circuit-breaker state are not reported.

Missing context, unsupported diagnostics and missing handles keep their existing errors without reading policy or starting a handle. Cancellation and policy failures propagate. UI settings, UI presentation and the parent issue's UI acceptance remain outstanding outside this command-only slice.
