# Config-defined model capacities

Developer reference for the registration/provenance slice of #4738. This applies to config-defined chat catalogues, including Microsoft Foundry. It does not change discovered-provider limits or the working-budget and compaction policies tracked in #4736.

## Configuration

Use the installed central configuration CLI for the intended BotNexus home. Replace `my-provider` with an existing provider-instance key. This PowerShell example replaces that provider's entire model-capacity map; preserve other entries when updating it:

```powershell
botnexus config set 'providers.my-provider.chat.modelCapacities' '{"large-deployment":{"contextWindow":1050000,"maxTokens":128000},"small-deployment":{"contextWindow":128000}}'
botnexus config get 'providers.my-provider.chat.modelCapacities'
```

The CLI accepts `config set <key> <value>` and parses the dictionary value as JSON. Supplying the whole map avoids dotted-path ambiguity for model IDs containing periods. This example declares synthetic capacities, not verified limits for a real deployment. Only declare limits supported by your deployment's contract. A capacity entry does not itself add a model to `chat.models` or select it for an agent.

The configuration path is `providers.<provider-instance>.chat.modelCapacities.<exact-model-id>`. Keys use ordinal, case-sensitive comparison. Each entry has nullable integer `contextWindow` and `maxTokens` fields. Different models and provider instances resolve independently.

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

The existing model-list diagnostics expose `contextWindow`, `maxTokens`, `contextWindowSource`, and `maxTokensSource`. This slice adds metadata only: it does not redesign output reservation, prompt allocation, provider request construction, overflow recovery, or compaction thresholds. Full #4738 budget acceptance remains outside this bounded slice.
