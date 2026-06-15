# GitHub Models Provider

The GitHub Models provider gives BotNexus access to the free-tier models hosted on [GitHub Models](https://github.com/marketplace/models). It is a thin configuration layer over the [OpenAI-Compatible provider](openai-compatible.md): GitHub Models exposes an OpenAI Chat Completions endpoint, so requests are handled by the existing `openai-compat` wire contract with no separate provider registration.

A curated catalog of GitHub Models is registered into the model registry automatically at gateway startup, so the models are available out of the box once you supply a token.

## Prerequisites

- A GitHub account with [GitHub Models](https://github.com/marketplace/models) access
- A GitHub personal access token (classic or fine-grained) exposed as the `GITHUB_TOKEN` environment variable

> GitHub Models is a free, rate-limited inference service intended for experimentation and prototyping. It is not designed for production traffic.

## Configuration

Because GitHub Models speaks the OpenAI-compatible protocol, you configure it as an `openai-compat` provider entry that points at the GitHub Models inference endpoint:

```json
{
  "providers": {
    "github-models": {
      "enabled": true,
      "api": "openai-compat",
      "baseUrl": "https://models.inference.ai.azure.com",
      "apiKeyEnvVar": "GITHUB_TOKEN"
    }
  }
}
```

| Field | Value | Description |
|-------|-------|-------------|
| `api` | `"openai-compat"` | GitHub Models uses the OpenAI Chat Completions wire contract |
| `baseUrl` | `https://models.inference.ai.azure.com` | The fixed GitHub Models inference base URL |
| `apiKeyEnvVar` | `GITHUB_TOKEN` | Environment variable holding the GitHub token used for authentication |

Then point an agent at a registered GitHub Models model:

```json
{
  "agents": {
    "my-agent": {
      "apiProvider": "github-models",
      "modelId": "gpt-4o-mini"
    }
  }
}
```

## Supported Models

The following free-tier models are registered automatically. All have a `text` input modality and zero cost, and route through the `openai-compat` API.

| Model | Identifier | Context Window | Max Output Tokens |
|-------|-----------|---------------:|------------------:|
| GPT-4o Mini | `gpt-4o-mini` | 128,000 | 4,096 |
| GPT-4o | `gpt-4o` | 128,000 | 4,096 |
| Phi-3.5 Mini Instruct | `Phi-3.5-mini-instruct` | 128,000 | 4,096 |
| Phi-4 | `Phi-4` | 128,000 | 16,384 |
| Meta Llama 3.1 8B Instruct | `Meta-Llama-3.1-8B-Instruct` | 128,000 | 2,048 |
| Mistral Small | `Mistral-small` | 32,000 | 4,096 |
| AI21 Jamba 1.5 Mini | `AI21-Jamba-1.5-Mini` | 256,000 | 4,096 |

Use the exact identifier in your `modelId` field. Other models published on GitHub Models can be used by adding them with the same `openai-compat` configuration and the GitHub Models `baseUrl`.

## Authentication

The provider resolves its credential from the `GITHUB_TOKEN` environment variable at request time. The same token can be reused for the [GitHub Copilot provider](github-copilot.md), which also accepts `GITHUB_TOKEN` (among other variables) — but the two are distinct services: Copilot uses the Copilot API and OAuth device flow, while GitHub Models uses the OpenAI-compatible inference endpoint.

## Known Limitations

- **Free tier only** — GitHub Models enforces rate and usage limits per token. Expect throttling under sustained load.
- **No reasoning effort** — the registered models do not support reasoning/thinking effort levels (`SupportsReasoningEffort = false`).
- **No `store` or `developer` role** — GitHub Models does not support the OpenAI `store` parameter or the `developer` message role, so those compatibility features are disabled.
- **Feature support varies** — as with any OpenAI-compatible endpoint, function calling and structured output support depend on the specific model.
- **Not for production** — use a first-party provider (Anthropic, OpenAI, GitHub Copilot) for production workloads.

## Related

- [OpenAI-Compatible Provider](openai-compatible.md) — the underlying wire contract GitHub Models uses
- [GitHub Copilot Provider](github-copilot.md) — a separate GitHub-backed provider using the Copilot API
