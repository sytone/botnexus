# OpenAI Provider

The OpenAI provider connects BotNexus to GPT models via both the Chat Completions API and the newer Responses API. It supports streaming, function calling, and structured outputs.

## Prerequisites

- An OpenAI API key (from [platform.openai.com](https://platform.openai.com/))

## Configuration

Select the provider instance and a registered model on your agent in `config.json`:

```json
{
  "agents": {
    "my-agent": {
      "provider": "openai",
      "model": "gpt-4o"
    }
  }
}
```

`provider` and `model` are platform-agent configuration keys. Tool and template contracts can separately use `apiProvider` and `modelId`; do not copy those names into an entry under `agents`.

### API Key

BotNexus resolves the key from the `OPENAI_API_KEY` environment variable.

Alternatively, configure a literal key or an `auth:<entry>` reference on the provider instance. Keep credentials under `providers`, not under an agent, and do not commit real keys:

```json
{
  "providers": {
    "openai": {
      "apiKey": "auth:openai"
    }
  },
  "agents": {
    "my-agent": {
      "provider": "openai",
      "model": "gpt-4o"
    }
  }
}
```

## Supported Models

The following models are registered by `BuiltInModels.RegisterOpenAIModels`. The limits are built-in registry metadata, not a statement about every model available from OpenAI.

| Model | Identifier | API path | Context Window | Max Output Tokens |
|-------|------------|----------|---------------:|------------------:|
| GPT-4.1 | `gpt-4.1` | Completions | 1,047,576 | 32,768 |
| GPT-4.1 Mini | `gpt-4.1-mini` | Completions | 1,047,576 | 32,768 |
| GPT-4o | `gpt-4o` | Completions | 128,000 | 16,384 |
| o3 | `o3` | Responses | 200,000 | 100,000 |
| o4-mini | `o4-mini` | Responses | 200,000 | 100,000 |

Use an exact registered identifier in the agent's `model` field. An ID absent from this table requires custom or dynamically supplied registration before use; absence from the built-ins does not establish upstream unavailability.

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
