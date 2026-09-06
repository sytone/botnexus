# GitHub Copilot Provider

The GitHub Copilot provider connects BotNexus to models available through the GitHub Copilot API. It uses your existing Copilot subscription — no separate API key required. BotNexus supports both the Completions and Responses API paths, and includes dynamic model discovery.

## Prerequisites

- An active GitHub Copilot subscription (Individual, Business, or Enterprise)
- GitHub CLI (`gh`) authenticated with a Copilot-enabled account
- BotNexus running on a machine where `gh auth status` shows an active session

## Configuration

Set the provider on your agent in `config.json`:

```json
{
  "agents": {
    "my-agent": {
      "provider": "github-copilot",
      "model": "claude-sonnet-4"
    }
  }
}
```

`provider` names the model-registry provider instance; `copilot` is also a supported alias for `github-copilot`. `model` is the registered model ID, not an API name. These are platform configuration keys; tool and template contracts can separately use `apiProvider` and `modelId`.

### Authentication

BotNexus automatically discovers Copilot credentials via:

1. `COPILOT_GITHUB_TOKEN` environment variable
2. `GH_TOKEN` environment variable
3. `GITHUB_TOKEN` environment variable
4. GitHub CLI auth state (automatic OAuth refresh)

No API key configuration is needed when `gh` is authenticated.

### CLI Setup

Use the BotNexus CLI to verify and configure Copilot access:

```bash
# Check Copilot authentication, plan, and endpoint
botnexus provider copilot whoami

# List the models your account is entitled to
botnexus provider copilot models
```

See the [CLI Reference](../cli-reference.md#provider-copilot) for the full `provider copilot` diagnostic subcommand group (`login`, `whoami`, `models`, `quota`, `test`).

## Supported Models

The following examples are a subset of BotNexus's built-in Copilot registrations in `BuiltInModels.RegisterCopilotModels`. The limits are registry metadata, not a guarantee of account entitlement or current upstream availability.

| Model | API path | Context Window | Max Output Tokens |
|-------|----------|---------------:|------------------:|
| `claude-sonnet-4` | Messages | 216,000 | 16,000 |
| `claude-sonnet-4.5` | Messages | 144,000 | 32,000 |
| `claude-opus-4.5` | Messages | 160,000 | 32,000 |
| `claude-opus-5` | Messages | 200,000 | 64,000 |
| `gpt-4o` | Completions | 128,000 | 4,096 |
| `gpt-4.1` | Completions | 128,000 | 16,384 |
| `gpt-5.6` | Responses | 922,000 | 128,000 |

Run `botnexus provider copilot models` to inspect the catalog returned for your account. An ID absent from this built-in catalog requires a discovered or custom registration before use; absence from the built-ins does not establish upstream unavailability.

## Features

### Dynamic Model Discovery

At gateway startup, BotNexus queries Copilot's catalog and overlays discovered models and capabilities onto the built-in registry. Discovery can add IDs or replace metadata for an existing ID. It is best-effort: failures leave the built-in entries available as a fallback. The CLI discovery command also lets you inspect the account catalog.

### API and transport selection

- **Messages API** — Claude models are accessed via the Messages-compatible path.
- **Completions API** — built-in `gpt-4o`, `gpt-4.1`, Gemini and Grok entries use the Completions path.
- **Responses API** — built-in GPT-5-family entries use the Responses path for native tool call flow.

The selected model registration determines the API; model family alone is not sufficient.

For Responses models, discovery also records the endpoints advertised for each model. When a model advertises `ws:/responses`, BotNexus uses the WebSocket transport automatically; otherwise it keeps the Server-Sent Events (SSE) path. If the WebSocket fails before producing any semantic output, the provider safely retries over SSE. After output begins, it does not replay the request, avoiding duplicated text or tool calls.

Transport selection is capability-driven and has no user-facing configuration setting.

### Context Window

Context and output limits are model-specific; use the named registration rather than a fixed 200K assumption. The table above shows built-in values. Runtime discovery or custom registration may replace those values. The Copilot built-ins do not opt into `SupportsExtendedContextWindow`; do not infer an Anthropic-direct 1M tier from a Claude model name.

### Usage Tracking

BotNexus parses Copilot usage billing snapshots and emits activity tags for observability. Monitor per-agent token consumption through the platform diagnostics.

### Prompt Caching

Copilot supports prompt caching for compatible models. The `<!-- BOTNEXUS_CACHE_BOUNDARY -->` marker is respected.

## Known Limitations

- Model availability varies by Copilot subscription tier
- Rate limits are managed by GitHub — not configurable per-user
- Some models may not support all features (e.g., extended thinking availability depends on the model)
- Built-in limits are fallback metadata; inspect the effective registration after discovery rather than assuming every Claude model has the same context window
- OAuth token refresh requires `gh` CLI to be installed and authenticated
