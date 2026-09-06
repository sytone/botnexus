# GitHub Models Provider

The GitHub Models catalog connects BotNexus to [GitHub Models](https://github.com/marketplace/models). It is a thin configuration layer over the [OpenAI-Compatible provider](openai-compatible.md): GitHub Models exposes an OpenAI Chat Completions endpoint, so requests are handled by the existing `openai-compat` wire contract with no separate provider registration.

A curated catalog is registered into the model registry automatically at gateway startup. A registry entry makes a model selectable in BotNexus; successful inference still requires valid credentials and endpoint access.

## Prerequisites

- A GitHub account with [GitHub Models](https://github.com/marketplace/models) access
- A credential accepted by your GitHub Models endpoint, configured on the `github-models` provider instance

> This guide describes BotNexus's source-defined catalog and configuration, not current service pricing, quotas or production suitability. Check the service terms for your account before relying on it.

## Configuration

The built-in models are registered under the provider instance `github-models` with the `openai-compat` API and the endpoint shown below. Supply credentials on that provider instance, not on an agent:

```json
{
  "providers": {
    "github-models": {
      "enabled": true,
      "baseUrl": "https://models.inference.ai.azure.com",
      "apiKey": "your-github-models-token",
      "chat": {
        "api": "openai-compat"
      }
    }
  }
}
```

| Field | Value | Description |
|-------|-------|-------------|
| `chat.api` | `"openai-compat"` | API contract used for explicitly configured chat model registrations |
| `baseUrl` | `https://models.inference.ai.azure.com` | Endpoint recorded by the built-in GitHub Models catalog |
| `apiKey` | Your token, or `auth:github-models` | Provider credential, or reference to a configured `auth.json` entry |

Then point an agent at a registered GitHub Models model:

```json
{
  "agents": {
    "my-agent": {
      "provider": "github-models",
      "model": "gpt-4o-mini"
    }
  }
}
```

## Supported Models

`GitHubModelsProvider.RegisterModels` registers the following seven built-in entries automatically. All declare `text` input, zero cost metadata and the `openai-compat` API. These are source-defined defaults, not verified service pricing, entitlement or current upstream availability.

| Model | Identifier | Context Window | Max Output Tokens |
|-------|-----------|---------------:|------------------:|
| GPT-4o Mini | `gpt-4o-mini` | 128,000 | 4,096 |
| GPT-4o | `gpt-4o` | 128,000 | 4,096 |
| Phi-3.5 Mini Instruct | `Phi-3.5-mini-instruct` | 128,000 | 4,096 |
| Phi-4 | `Phi-4` | 128,000 | 16,384 |
| Meta Llama 3.1 8B Instruct | `Meta-Llama-3.1-8B-Instruct` | 128,000 | 2,048 |
| Mistral Small | `Mistral-small` | 32,000 | 4,096 |
| AI21 Jamba 1.5 Mini | `AI21-Jamba-1.5-Mini` | 256,000 | 4,096 |

Use the exact identifier in the agent's `model` field. For additional models, explicitly register the endpoint's accepted IDs under `providers.github-models.chat.models` with `chat.api` set to `openai-compat`; set `chat.contextWindow` from the model's actual limits. These are custom registrations, not additions to the built-in table. Listing an existing built-in ID there replaces its metadata with config-derived values, so omit `chat.models` when using the built-in limits above.

## Authentication

Use `providers.github-models.apiKey` or a configured `auth.json` entry (referenced with `apiKey: "auth:github-models"`). Keep real tokens out of committed examples. `ProviderConfig` has no `apiKeyEnvVar` field, and `EnvironmentApiKeys` does not map `github-models` to `GITHUB_TOKEN`; setting that variable alone is not this provider's credential configuration. The [GitHub Copilot provider](github-copilot.md) has a separate credential path and service contract.

## Known Limitations

- **Static catalog** — built-in entries are fallback configuration, not a live availability or entitlement check.
- **No reasoning effort** — the registered models do not support reasoning/thinking effort levels (`SupportsReasoningEffort = false`).
- **No `store` or `developer` role in built-in metadata** — the registered compatibility flags disable these features.
- **Feature support varies** — as with any OpenAI-compatible endpoint, function calling and structured output support depend on the specific model.
- **Cost metadata** — zero-cost registrations are not evidence of free usage under your account's service terms.

## Related

- [OpenAI-Compatible Provider](openai-compatible.md) — the underlying wire contract GitHub Models uses
- [GitHub Copilot Provider](github-copilot.md) — a separate GitHub-backed provider using the Copilot API
