# Config-defined model capacities

Developer reference for the registration/provenance slice of #4738. This applies to config-defined chat catalogues, including Microsoft Foundry. It does not change discovered-provider limits or the working-budget and compaction policies tracked in #4736.

## Configuration

Use the installed central configuration CLI for the intended BotNexus home. Replace `my-provider` with an existing provider-instance key. This PowerShell example replaces that provider's entire model-capacity map; preserve other entries when updating it:

```powershell
botnexus config set 'providers.my-provider.chat.modelCapacities' '{"large-deployment":{"contextWindow":1050000,"maxTokens":128000},"small-deployment":{"contextWindow":128000}}'
botnexus config get 'providers.my-provider.chat.modelCapacities'
```

The CLI accepts `config set <key> <value>` and parses the dictionary value as JSON. Supplying the whole map avoids dotted-path ambiguity for model IDs containing periods. This example declares synthetic capacities, not verified limits for a real deployment. Only declare limits supported by your deployment's contract. A capacity entry does not itself add a model to `chat.models` or select it for an agent.

The map lives at `providers.<provider-instance>.chat.modelCapacities`. Its JSON keys are literal, ordinal, case-sensitive model IDs: `Model.1`, `model.1`, and `vendor:model` remain distinct across JSON, SQLite, and runtime options. Each entry has nullable integer `contextWindow` and `maxTokens` fields. Different models and provider instances resolve independently.

The complete map is one configuration value. A map in a later source replaces an earlier map; an absent map inherits, and an empty object or explicit null clears earlier entries. SQLite follows JSON in the normal provider order, so its map wins when present. Environment or command-line overlays can supply the complete map as a JSON string at the map key. Individual field overlays remain available for IDs that the overlay provider can represent, but separator-containing IDs and case-only siblings require the whole-map form. Do not use a dotted CLI path to address an ID containing a period.

Internally, SQLite stores the map as a JSON-valued leaf rather than splitting model IDs into dotted paths. The framework binding projection omits this map; post-configuration materializes it from accepted documents in provider order. Other configuration values retain ordinary framework binding.

## Resolution and validation

| Field | Precedence | Origin reported |
| --- | --- | --- |
| Context capacity | Per-model value → `chat.contextWindow` (or legacy provider `contextWindow`) → 128000 | `configured-model`, `configured-provider`, or `fallback` |
| Maximum output | Per-model value → 32000, reduced to context minus one if needed | `configured-model` or `fallback` |

Resolution is per field: overriding output does not override context. Both effective values must be positive, and output must be strictly less than context. Context therefore must allow at least one positive output token. Explicit invalid values are rejected, not silently capped. A rejected reload retains the last-known-good complete catalogue and records an activation failure naming the provider and model. A successful later reload clears the failure.

Existing registrations without explicit provenance report `registered`. Neither `registered` nor operator configuration means provider-verified; `fallback` is a conservative default, not a discovered capability.

## Keep the limits separate

- **Context capacity** describes the token window declared for a model registration.
- **Maximum output** describes the output-token ceiling, not an input or working-history budget.
- **Tokens per minute (TPM)** is a throughput/rate-limit quota. It is not a context window or output ceiling and must not populate either field.

The model-list diagnostics expose `contextWindow`, `maxTokens`, `contextWindowSource`, and `maxTokensSource`.

## Active-session working-budget diagnostics

`GET /api/agents/{agentId}/sessions/{sessionId}/context` includes an additive `contextBudget` snapshot for in-process handles. It reports:

| Field | Meaning |
| --- | --- |
| `modelContextWindowTokens` / `modelContextWindowSource` | Registered context declaration and its origin |
| `modelMaxOutputTokens` / `modelMaxOutputSource` | Independent registered output declaration and its origin |
| `effectiveWorkingBudgetTokens` / `effectiveWorkingBudgetSource` | Selected working window and selection layer: `conversation`, `agent`, or `model` |

The selected working window follows conversation override → agent override → registered model. The existing `contextWindowTokens` and `usagePercent` use this same window, not the maximum output. Capacity declaration origins remain `configured-model`, `configured-provider`, `fallback`, or `registered`; none asserts provider verification. An extended-context override can exceed a model's standard registered window without rewriting that declaration.

The snapshot describes the active handle's binding, not a live resampling after configuration changes. Unknown capacities and origins remain null. Inspectors without budget provenance retain legacy window reporting and return null `contextBudget`. When no scoped window is available, compaction still uses its configured global options; the endpoint does not invent a global window for an unresolvable binding.

Registration-to-scoped-compaction regressions cover configured context/output separation, agent/conversation precedence, fallback model metadata and missing-model global fallback. This diagnostic slice does not redesign output reservation, prompt allocation, provider request construction, overflow recovery, or compaction thresholds. Verified-hard-limit enforcement and output-reserve policy remain outside this bounded #4738 slice; #4736 separately owns coordinator budget propagation.
