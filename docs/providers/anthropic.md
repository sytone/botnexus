# Anthropic Provider

The Anthropic provider connects BotNexus to Claude models via the Anthropic Messages API. It supports streaming, extended thinking, prompt caching, and tool use.

## Prerequisites

- An Anthropic API key (from [console.anthropic.com](https://console.anthropic.com/))
- Or an Anthropic OAuth token (for Max subscribers)

## Configuration

Set the provider on your agent in `config.json`:

```json
{
  "agents": {
    "my-agent": {
      "apiProvider": "anthropic",
      "modelId": "claude-sonnet-4-20250514"
    }
  }
}
```

### API Key

BotNexus resolves the key from environment variables in priority order:

1. `ANTHROPIC_OAUTH_TOKEN` — OAuth token (Max subscribers)
2. `ANTHROPIC_API_KEY` — Standard API key

Alternatively, set the key directly in agent configuration (not recommended for production):

```json
{
  "agents": {
    "my-agent": {
      "apiProvider": "anthropic",
      "modelId": "claude-sonnet-4-20250514",
      "apiKey": "sk-ant-..."
    }
  }
}
```

## Supported Models

| Model | Identifier |
|-------|-----------|
| Claude Opus 4 | `claude-opus-4-20250514` |
| Claude Sonnet 4 | `claude-sonnet-4-20250514` |
| Claude Haiku 3.5 | `claude-3-5-haiku-20241022` |

Use the full model identifier in your `modelId` field.

## Features

### Extended Thinking

Claude supports extended thinking (chain-of-thought reasoning before responding). Configure via reasoning settings:

```json
{
  "agents": {
    "my-agent": {
      "apiProvider": "anthropic",
      "modelId": "claude-sonnet-4-20250514",
      "reasoning": {
        "effort": "medium"
      }
    }
  }
}
```

Effort levels: `low`, `medium`, `high`, `max`.

### Prompt Caching

BotNexus automatically uses Anthropic's prompt caching. The system prompt is split at the `<!-- BOTNEXUS_CACHE_BOUNDARY -->` marker — content before the boundary is cached across turns, reducing latency and cost.

### Tool Use

All registered agent tools are automatically converted to Anthropic's tool format. No additional configuration required.

## Known Limitations

- Extended thinking requires models that support it (Opus 4, Sonnet 4)
- Maximum output tokens depend on the model and whether thinking is enabled
- Anthropic enforces rate limits per API key — monitor usage in the Anthropic console
