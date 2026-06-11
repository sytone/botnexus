# OpenAI-Compatible Provider

The OpenAI-Compatible provider connects BotNexus to any LLM API that implements the OpenAI Chat Completions protocol. This covers a wide range of self-hosted and third-party inference services.

## Prerequisites

- Access to an OpenAI-compatible API endpoint
- An API key for that service (if required)

## Configuration

Set the provider on your agent in `config.json`:

```json
{
  "agents": {
    "my-agent": {
      "apiProvider": "openai-compat",
      "modelId": "deepseek-chat",
      "apiBaseUrl": "https://api.deepseek.com/v1",
      "apiKey": "your-api-key"
    }
  }
}
```

### Required Fields

| Field | Description |
|-------|-------------|
| `apiProvider` | Must be `"openai-compat"` |
| `modelId` | The model identifier as expected by the target API |
| `apiBaseUrl` | The base URL for the API (e.g. `https://api.deepseek.com/v1`) |

### Optional Fields

| Field | Description |
|-------|-------------|
| `apiKey` | API key for authentication (if the service requires one) |

## Compatible Services

This provider works with any service implementing the OpenAI Chat Completions protocol, including:

- **DeepSeek** — `https://api.deepseek.com/v1`
- **Groq** — `https://api.groq.com/openai/v1`
- **Together AI** — `https://api.together.xyz/v1`
- **Ollama** — `http://localhost:11434/v1` (local) — see [dedicated Ollama page](ollama.md) for CLI diagnostics
- **vLLM** — `http://localhost:8000/v1` (local)
- **LM Studio** — `http://localhost:1234/v1` (local)
- **OpenRouter** — `https://openrouter.ai/api/v1`

## Features

### Streaming

Streaming is supported via the standard SSE (Server-Sent Events) protocol. Most compatible APIs support streaming out of the box.

### Tool Use

Function calling is converted to the OpenAI format. Compatibility depends on the target API — some services support function calling while others do not.

## Known Limitations

- Feature support varies by target API — not all support function calling, streaming, or structured outputs
- Token counting may be inaccurate for non-OpenAI models (BotNexus uses tiktoken-compatible estimates)
- Some APIs may not support all parameters (temperature, top_p, etc.) — unsupported parameters are silently ignored by most services
