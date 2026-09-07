# Ollama Provider

Ollama runs large language models locally on your machine. BotNexus connects to Ollama through its OpenAI-compatible API endpoint, giving you fully offline agent execution with no API keys required.

## Prerequisites

- [Ollama](https://ollama.com/) installed and running locally
- At least one model pulled (e.g. `ollama pull llama3`)

## Quick Start

Use the built-in CLI diagnostics to verify connectivity and list available models:

```powershell
# Check server status
botnexus provider ollama status

# List pulled models
botnexus provider ollama models

# Test a model with a simple prompt
botnexus provider ollama test --model llama3
```

## Configuration

Ollama uses the OpenAI-compatible provider under the hood. Configure it in `config.json`:

```json
{
  "providers": {
    "ollama": {
      "baseUrl": "http://localhost:11434/v1",
      "chat": {
        "api": "openai-compat",
        "models": ["llama3"]
      }
    }
  },
  "agents": {
    "my-agent": {
      "provider": "ollama",
      "model": "llama3"
    }
  }
}
```

This example assumes an unauthenticated local endpoint. `ollama` is the provider instance name; `openai-compat` is the API contract. `llama3` is a custom, server-supplied model ID, not a BotNexus built-in registration. Replace it in both `chat.models` and the agent's `model` with the exact ID served by your instance (including a tag such as `:latest` when applicable). Pulling a model on the server and registering it in BotNexus are separate steps.

### Configuration Fields

| Field | Required | Description |
|-------|----------|-------------|
| `baseUrl` | Yes | Provider endpoint; the local example uses `http://localhost:11434/v1` |
| `chat.api` | Yes for this recipe | `"openai-compat"` selects the OpenAI-Compatible implementation |
| `chat.models` | Yes for this recipe | Exact server model IDs to register under the `ollama` instance |
| `chat.contextWindow` | No | Context limit for config-declared models; set it to match your server/model |
| Agent `provider` / `model` | Yes | `"ollama"` and a registered model ID |

Gateway startup registers `chat.models` with a 128,000-token context default when `chat.contextWindow` is omitted, and a 32,000-token maximum-output metadata value. These are BotNexus config-registration defaults, not detected Ollama capacities. The `PreConfiguredModels.Ollama` helper is a separate code-level model factory; this JSON recipe does not call it. Keep explicit agent `model` values rather than relying on a provider default to populate the agent descriptor.

## CLI Diagnostics

The `botnexus provider ollama` command group provides operator diagnostics without requiring a running gateway:

### `ollama status`

Check connectivity to the server root and display its response:

```powershell
botnexus provider ollama status
```

The command prints the reachable URL and the server's response body; it does not query a separate version endpoint.

### `ollama models`

List all models pulled on the local instance:

```powershell
botnexus provider ollama models
```

The command reads `/api/tags` and displays Name, Size, Modified, Family and Parameters columns, followed by a model count. It does not register those models in the gateway.

### `ollama test`

Send a test prompt to verify end-to-end model inference:

```powershell
botnexus provider ollama test --model llama3
```

This CLI diagnostic posts a non-streaming prompt to Ollama's native `/api/chat` endpoint. It checks basic inference, not the gateway's `/v1` OpenAI-compatible path, streaming or tool use.

## Features

### Streaming

Fully supported via the standard SSE protocol.

### Tool Use (Function Calling)

The OpenAI-Compatible implementation serializes tool definitions and tool calls in Chat Completions format. Actual tool support depends on the model and server configuration; registering an ID in BotNexus does not establish that support.

### Custom Server URL

If Ollama runs on a remote machine or non-default port:

```powershell
botnexus provider ollama status --url http://192.168.1.100:11434
botnexus provider ollama models --url http://192.168.1.100:11434
```

Update `baseUrl` in config accordingly:

```json
{
  "providers": {
    "ollama": {
      "baseUrl": "http://192.168.1.100:11434/v1",
      "chat": {
        "api": "openai-compat",
        "models": ["llama3"]
      }
    }
  }
}
```

## Known Limitations

- **Local trust boundary** — the example omits credentials; secure access before exposing the endpoint remotely
- **Model availability** — models must be pulled before use (`ollama pull <model>`)
- **Context window** — varies by model; BotNexus cannot auto-detect limits for all models
- **Token counting** — uses tiktoken-compatible estimates which may be inaccurate for non-OpenAI architectures
- **Structured outputs** — JSON mode support depends on the specific model

## See Also

- [OpenAI-Compatible Provider](openai-compatible.md) — the underlying protocol Ollama uses
- [Provider Setup](../cli-reference.md#provider-setup) — interactive provider setup wizard
- [Configuration Guide](../configuration.md) — full configuration reference
