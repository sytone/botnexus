# Anthropic Provider

The Anthropic provider connects BotNexus to Claude models via the Anthropic Messages API. It supports streaming, extended thinking, prompt caching, and tool use.

## Prerequisites

- An Anthropic API key (from [console.anthropic.com](https://console.anthropic.com/))
- Or an Anthropic OAuth token (for Max subscribers)

## Configuration

Select the provider instance and a registered model on your agent in `config.json`:

```json
{
  "agents": {
    "my-agent": {
      "provider": "anthropic",
      "model": "claude-sonnet-4-5-20250929"
    }
  }
}
```

`provider` and `model` are platform-agent configuration keys. Tool and template contracts can separately use `apiProvider` and `modelId`; do not copy those names into an entry under `agents`.

### API Key

BotNexus resolves the key from environment variables in priority order:

1. `ANTHROPIC_OAUTH_TOKEN` — OAuth token (Max subscribers)
2. `ANTHROPIC_API_KEY` — Standard API key

Alternatively, configure a literal key or an `auth:<entry>` reference on the provider instance. Keep credentials under `providers`, not under an agent, and do not commit real keys:

```json
{
  "providers": {
    "anthropic": {
      "apiKey": "auth:anthropic"
    }
  },
  "agents": {
    "my-agent": {
      "provider": "anthropic",
      "model": "claude-sonnet-4-5-20250929"
    }
  }
}
```

## Supported Models

The following models are registered by `BuiltInModels.RegisterAnthropicModels`. The limits are built-in registry metadata; Anthropic discovery can add models or update metadata at runtime.

| Model | Identifier | Default Context Window | Max Output Tokens |
|-------|------------|-----------------------:|------------------:|
| Claude Opus 5 | `claude-opus-5` | 200,000 | 128,000 |
| Claude Sonnet 5 | `claude-sonnet-5` | 200,000 | 128,000 |
| Claude Haiku 4.5 | `claude-haiku-4-5-20251001` | 200,000 | 64,000 |
| Claude Sonnet 4.5 | `claude-sonnet-4-5-20250929` | 200,000 | 64,000 |
| Claude Opus 4.5 | `claude-opus-4-5-20251101` | 200,000 | 64,000 |

Use an exact registered identifier in the agent's `model` field. An ID absent from this table requires dynamic discovery or custom registration before use; absence from the built-ins does not establish upstream unavailability.

## Features

### Extended Thinking

Claude supports extended thinking (reasoning before responding). Configure the agent's `thinking` level:

```json
{
  "agents": {
    "my-agent": {
      "provider": "anthropic",
      "model": "claude-sonnet-4-5-20250929",
      "thinking": "medium"
    }
  }
}
```

Effort levels: `low`, `medium`, `high`, `max`.

### Context Window Selection

The built-in long-context models on the Anthropic-direct path (Opus 5, Sonnet 5, and Sonnet 4.5) support a selectable context window of either 200K (the default) or 1M tokens.
When -- and only when -- the 1M window is selected, BotNexus sends the Anthropic
`context-1m-2025-08-07` beta header on the messages request. Leaving the window unset keeps
the standard 200K window and sends no beta header. Each model advertises its valid set of
context sizes, so the selector only ever offers values the provider accepts (200K / 1M for
these models; 200K only for built-ins that do not advertise extended context, such as Claude Haiku 4.5 and Opus 4.5).

This is the Anthropic-direct path only. Claude registrations reached through GitHub Copilot have model-specific context limits and do not expose Anthropic's selectable 1M tier or emit its beta header. See the GitHub Copilot provider page for the built-in Copilot limits.

### Prompt Caching

BotNexus automatically uses Anthropic's prompt caching. The system prompt is split at the `<!-- BOTNEXUS_CACHE_BOUNDARY -->` marker — content before the boundary is cached across turns, reducing latency and cost.

### Tool Use

All registered agent tools are automatically converted to Anthropic's tool format. No additional configuration required.

## Known Limitations

- Extended thinking requires a model whose effective registration supports reasoning
- Maximum output tokens depend on the model and whether thinking is enabled
- Anthropic enforces rate limits per API key — monitor usage in the Anthropic console
