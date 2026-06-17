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
      "apiProvider": "copilot",
      "modelId": "claude-sonnet-4-20250514"
    }
  }
}
```

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

Copilot provides access to models from multiple families. Availability depends on your subscription tier:

| Model | Family |
|-------|--------|
| `claude-sonnet-4-20250514` | Claude (Anthropic) |
| `claude-opus-4-20250514` | Claude (Anthropic) |
| `gpt-4o` | GPT (OpenAI) |
| `gpt-4.1` | GPT (OpenAI) |
| `o3-mini` | GPT (OpenAI) |

Run `botnexus provider copilot models` to see the full list available to your account.

## Features

### Dynamic Model Discovery

BotNexus can query Copilot's model catalog at runtime to discover available models and their capabilities. This happens automatically when using the CLI discovery command.

### Dual API Path

- **Messages API** — Claude models are accessed via the Messages-compatible path
- **Responses API** — OpenAI models use the Responses API for native tool call flow

The provider automatically selects the correct path based on the model family.

### Usage Tracking

BotNexus parses Copilot usage billing snapshots and emits activity tags for observability. Monitor per-agent token consumption through the platform diagnostics.

### Prompt Caching

Copilot supports prompt caching for compatible models. The `<!-- BOTNEXUS_CACHE_BOUNDARY -->` marker is respected.

## Known Limitations

- Model availability varies by Copilot subscription tier
- Rate limits are managed by GitHub — not configurable per-user
- Some models may not support all features (e.g., extended thinking availability depends on the model)
- OAuth token refresh requires `gh` CLI to be installed and authenticated
