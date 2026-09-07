# OpenAI-Compatible Provider

The OpenAI-Compatible provider connects BotNexus to any LLM API that implements the OpenAI Chat Completions protocol. This covers a wide range of self-hosted and third-party inference services.

## Prerequisites

- Access to an OpenAI-compatible API endpoint
- An API key for that service (if required)

## Configuration

Declare a provider instance with its endpoint, credentials and chat model registrations, then select that instance and model on the agent in `config.json`. This local example assumes the server already serves a model with the placeholder ID `my-local-model`:

```json
{
  "providers": {
    "local-llm": {
      "baseUrl": "http://localhost:8000/v1",
      "chat": {
        "api": "openai-compat",
        "models": ["my-local-model"]
      }
    }
  },
  "agents": {
    "my-agent": {
      "provider": "local-llm",
      "model": "my-local-model"
    }
  }
}
```

### Configuration boundaries

| Location | Field | Description |
|----------|-------|-------------|
| `providers.local-llm` | `baseUrl` | Base URL of your server's Chat Completions endpoint |
| `providers.local-llm` | `apiKey` | Optional credential when the service requires one; a literal key or `auth:<entry>` reference to a configured `auth.json` entry |
| `providers.local-llm.chat` | `api` | `"openai-compat"` selects this API implementation |
| `providers.local-llm.chat` | `models` | Exact server model IDs to register under this provider instance |
| `providers.local-llm.chat` | `contextWindow` | Optional context metadata override for config-declared models |
| `agents.my-agent` | `provider` | Provider instance key, here `"local-llm"`, not the API contract name |
| `agents.my-agent` | `model` | Registered model ID, here `"my-local-model"` |

Replace the placeholder model ID in both locations. It is not a built-in model and this configuration does not download it or verify upstream availability. Gateway startup registers `chat.models` with a 128,000-token context default when no `chat.contextWindow` is supplied, and 32,000-token maximum-output metadata. Those defaults are not a measurement of the server's limits; set context metadata to match the model/server you operate.

Endpoint and credential settings belong under `providers`, never under the agent. Keep real secrets out of committed configuration. Flat provider `api`/`models` fields remain legacy-compatible, but the nested `chat` fields take precedence per field. Tool/template parameters such as `apiProvider` and `modelId` are separate contracts and are not renamed by this platform-config recipe.

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
