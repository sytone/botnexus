# sample-config.json notes

This file documents the fields in `docs/sample-config.json`.

## `gateway`

- `listenUrl`: HTTP endpoint the Gateway API listens on.
- `defaultAgentId`: agent used when a request does not specify one.
- `sessionsDirectory`: directory for file-backed session history (relative paths resolve from the config file directory).

## `agents`

- Each key is an agent ID (`assistant` in the sample).
- `provider`: provider key from `providers`.
- `model`: default model for the agent.
- `systemPromptFile`: path to a prompt file loaded at startup.
- `isolationStrategy`: runtime isolation mode (for example, `in-process`).
- `enabled`: set `false` to keep the definition without registering it.

## `providers`

- `copilot.baseUrl`: Copilot API base URL.
- `copilot.apiKey`: credential reference key, not a raw secret.
  - Store credentials in `~/.botnexus-agent/auth.json`.
  - Example `auth.json` entry:
    ```json
    {
      "copilot": {
        "type": "oauth"
      }
    }
    ```
  - With that entry, `"apiKey": "copilot"` resolves through `auth.json`.
