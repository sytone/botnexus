# GitHub Copilot Provider

The GitHub Copilot provider connects BotNexus to models available through the GitHub Copilot API. It uses your existing Copilot subscription — no separate API key required. BotNexus supports both the Completions and Responses API paths, and includes dynamic model discovery.

## Prerequisites

- An active GitHub Copilot subscription (Individual, Business, or Enterprise)
- BotNexus CLI for the device-code login and provider diagnostics
- Network access for authorization, token exchange and model requests

GitHub CLI (`gh`) is not required. Its authenticated state is not a BotNexus credential source.

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

`provider` names the model-registry provider instance. `model` is the registered model ID, not an API name. These are platform configuration keys; tool and template contracts can separately use `apiProvider` and `modelId`.

### Accounts, provider instances and the `copilot` alias

The canonical default instance is `github-copilot`. `copilot` is an alias for that default, not a second subscription. Existing named-provider seams support configured Copilot instances for model discovery/routing and the Portal Usage selection. The Usage view isolates account and header observations by the selected instance and credential generation; unknown or unsupported identity is unavailable, never an implicit default-account fallback.

Canonical CLI login and diagnostics still use `github-copilot`; named-instance Usage support does not promise the complete provisioning/feature matrix tracked by [#4192](https://github.com/sytone/botnexus/issues/4192). Enterprise authority interoperability is separate work. Do not manually craft auth entries or assume that changing an instance name establishes another account. There is no automatic account fallback or subscription rotation.

### Authentication

The gateway's `GatewayAuthManager.GetApiKeyAsync` resolves credentials in this order:

1. A usable BotNexus `auth.json` entry for `github-copilot` takes precedence.
2. Otherwise, resolve `providers.github-copilot.apiKey`, including an `auth:<entry>` reference. Any explicit declaration blocks ambient fallback when it is blank or cannot resolve.
3. Only when no provider credential was declared, try ambient credentials: the first nonblank value of `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, then `GITHUB_TOKEN`.

For stored OAuth credentials, BotNexus refreshes the Copilot session token by re-exchanging the retained GitHub credential directly over HTTP through `CopilotOAuth.RefreshAsync`. It does not invoke `gh` or read GitHub CLI auth state.

The CLI diagnostics have a different entry point: `CopilotAuthLoader` loads the `github-copilot` entry from BotNexus `auth.json` in the selected target directory. It does not fall back to gateway provider configuration, environment variables or GitHub CLI auth state. When necessary it refreshes the session token over HTTP and attempts to persist the refreshed credentials. An ambient variable that works for the gateway therefore does not by itself configure these diagnostics.

### CLI Setup

Use BotNexus's device-code login to create the `github-copilot` entry in its `auth.json` store (normally under the BotNexus home directory). `botnexus provider copilot login` is an alias for `botnexus provider setup --provider github-copilot`; follow the displayed authorization URL and code. Rerunning either setup command overwrites the existing `github-copilot` auth entry with the newly authorized account. It does not add another Copilot account. Treat the auth file as a secret and do not commit it.

```bash
# Authorize BotNexus and save its OAuth credentials
botnexus provider copilot login

# Check the stored Copilot authentication, plan, and endpoint
botnexus provider copilot whoami

# List the models your account is entitled to
botnexus provider copilot models
```

`whoami`, `models`, `quota`, and `test` read only the canonical `github-copilot` auth entry in the selected BotNexus home. `whoami` validates account identity, plan and endpoint; `models` projects the discovered catalog into the effective BotNexus model descriptors; `quota` reads its reported quota snapshots; and `test` resolves from that same discovered projection before sending a request. A newly entitled model therefore does not require a BotNexus release before the diagnostic can invoke it. `botnexus provider list` is different: it reports saved provider configuration and does not validate credentials or connectivity.

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
| `gpt-6-astra` | Responses | 922,000 | 128,000 |
| `gpt-6-luna` | Responses | 922,000 | 128,000 |
| `gpt-6-sol` | Responses | 922,000 | 128,000 |

Run `botnexus provider copilot models` to inspect the catalog returned for your account. An ID absent from this built-in catalog requires a discovered or custom registration before use; absence from the built-ins does not establish upstream unavailability.

## Features

### Dynamic Model Discovery

At gateway startup, BotNexus queries Copilot's catalog and overlays discovered models and capabilities onto the built-in registry. Discovery can add IDs or replace metadata for an existing ID. It is best-effort: failures leave the built-in entries available as a fallback. The CLI discovery command also lets you inspect the account catalog.

### API and transport selection

- **Messages API** — Claude models are accessed via the Messages-compatible path.
- **Completions API** — built-in `gpt-4o`, `gpt-4.1`, Gemini and Grok entries use the Completions path.
- **Responses API** — built-in GPT-5- and GPT-6-family entries use the Responses path for native tool call flow.

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
- OAuth refresh requires a retained GitHub credential and access to the HTTP token-exchange endpoint, not an installed/authenticated `gh` CLI
- Copilot CLI diagnostics require the BotNexus `auth.json` entry; gateway ambient fallback is not a diagnostic login substitute

## Read quota and activity

**CLI:** `botnexus provider copilot quota` reads quota for the canonical stored account. It does not select a named Portal instance or report gateway-wide scheduled activity.

**Portal:** Open **Usage**, then choose the configured Copilot instance in **Copilot account quota**. Account API quota and response-header observations appear separately, with their units and observation times. **Refresh account** requests a throttled refresh; ordinary panel reads do not contact GitHub. Closing the panel stops its polling and cancels panel-owned requests.

Fractional quota values are preserved. Unknown, partial, stale and unlimited states are explicit. The latest header may disagree with the account API; do not add those observations together. Header percentage and count fields are distinct. Neither quota nor local tokens is converted into a bill.

**Observed local activity** is a bounded process-local view of requests, failures and rate-limit-counter-derived burn, not authoritative model-token usage. **Gateway-wide scheduled activity** separately shows retained scheduled-run measurements for the last 24 hours or a calendar UTC day. Its displayed UTC bounds, truncation and per-field coverage explain what is known. Totals include all matching jobs, not only the top-job list. Historical runs lack provider identity, and delegated activity can overlap. Cache-token splits and all-conversation burn are unsupported.

Account-wide quota and gateway-wide activity require admin access. Missing credentials or an unsupported account scope produce unavailable observations, not zero consumption. Header observations are process-local and remain unavailable after restart until another attributed response arrives. This view does not add a polling cron or modify model execution. Developers can read the [Usage API contract](../api/provider-usage.md).
