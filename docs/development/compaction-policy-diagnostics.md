# Compaction policy diagnostics API

For developers integrating session diagnostics. This is the backend diagnostics slice of #4888; it does not add a settings UI or change compaction policy.

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

`/context` command integration and UI presentation remain outside this slice. The diagnostics service is reusable for a later command integration without introducing a second policy calculation.
