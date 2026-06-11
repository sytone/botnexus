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
      "api": "openai-completions",
      "baseUrl": "http://localhost:11434/v1",
      "defaultModel": "llama3"
    }
  },
  "agents": {
    "my-agent": {
      "apiProvider": "ollama",
      "modelId": "llama3"
    }
  }
}
```

No API key is required — Ollama does not authenticate local requests.

### Configuration Fields

| Field | Required | Description |
|-------|----------|-------------|
| `api` | Yes | Must be `"openai-completions"` |
| `baseUrl` | Yes | Ollama's OpenAI-compatible endpoint (default: `http://localhost:11434/v1`) |
| `defaultModel` | No | Default model ID for agents using this provider |

## CLI Diagnostics

The `botnexus provider ollama` command group provides operator diagnostics without requiring a running gateway:

### `ollama status`

Check server connectivity and version:

```powershell
botnexus provider ollama status
```

```text
✓ Ollama is running (version 0.5.4)
  URL: http://localhost:11434
```

### `ollama models`

List all models pulled on the local instance:

```powershell
botnexus provider ollama models
```

```text
┌───────────────────┬─────────┬────────────┐
│ Model             │ Size    │ Modified   │
├───────────────────┼─────────┼────────────┤
│ llama3:latest     │ 4.7 GB  │ 2 days ago │
│ codellama:13b     │ 7.4 GB  │ 5 days ago │
│ mistral:latest    │ 4.1 GB  │ 1 week ago │
└───────────────────┴─────────┴────────────┘
```

### `ollama test`

Send a test prompt to verify end-to-end model inference:

```powershell
botnexus provider ollama test --model llama3
```

Uses the Ollama OpenAI-compatible chat completions endpoint to confirm the model responds correctly.

## Features

### Streaming

Fully supported via the standard SSE protocol.

### Tool Use (Function Calling)

Depends on the specific model. Models like `llama3` and `mistral` support function calling when configured with `--tool-call` in Ollama. Smaller or older models may not support tools.

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
      "api": "openai-completions",
      "baseUrl": "http://192.168.1.100:11434/v1"
    }
  }
}
```

## Known Limitations

- **No authentication** — Ollama does not support API keys; secure your network if exposing remotely
- **Model availability** — models must be pulled before use (`ollama pull <model>`)
- **Context window** — varies by model; BotNexus cannot auto-detect limits for all models
- **Token counting** — uses tiktoken-compatible estimates which may be inaccurate for non-OpenAI architectures
- **Structured outputs** — JSON mode support depends on the specific model

## See Also

- [OpenAI-Compatible Provider](openai-compatible.md) — the underlying protocol Ollama uses
- [Provider Setup](../cli-reference.md#provider-setup) — interactive provider setup wizard
- [Configuration Guide](../configuration.md) — full configuration reference
