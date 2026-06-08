# OpenAI Provider

The OpenAI provider connects BotNexus to GPT models via both the Chat Completions API and the newer Responses API. It supports streaming, function calling, and structured outputs.

## Prerequisites

- An OpenAI API key (from [platform.openai.com](https://platform.openai.com/))

## Configuration

Set the provider on your agent in `config.json`:

```json
{
  "agents": {
    "my-agent": {
      "apiProvider": "openai",
      "modelId": "gpt-4o"
    }
  }
}
```

### API Key

BotNexus resolves the key from the `OPENAI_API_KEY` environment variable.

Alternatively, set it directly in agent configuration:

```json
{
  "agents": {
    "my-agent": {
      "apiProvider": "openai",
      "modelId": "gpt-4o",
      "apiKey": "sk-..."
    }
  }
}
```

## Supported Models

| Model | Identifier |
|-------|-----------|
| GPT-4o | `gpt-4o` |
| GPT-4.1 | `gpt-4.1` |
| GPT-4.1 mini | `gpt-4.1-mini` |
| o3 | `o3` |
| o4-mini | `o4-mini` |

Use the model identifier as published by OpenAI in your `modelId` field.

## Features

### Dual API Support

BotNexus supports two OpenAI API paths:

- **Responses API** — newer, supports streaming with native tool call flow
- **Completions API** — legacy Chat Completions endpoint

The provider automatically selects the appropriate API based on the model and configuration.

### Function Calling

All registered agent tools are automatically converted to OpenAI function definitions. No additional configuration required.

### Prompt Caching

OpenAI's automatic prompt caching is leveraged when available. The system prompt cache boundary marker helps optimize cache hit rates.

## Known Limitations

- Rate limits are per-organization — monitor usage in the OpenAI dashboard
- Some models (o3, o4-mini) have different token pricing and capability profiles
- Reasoning models (o-series) do not support all tool use patterns
