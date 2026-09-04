---
name: botnexus-guide
description: "How BotNexus itself works: agents, conversations vs sessions, channels, skills, tools, cron, extensions and configuration. Use this to answer any question about operating, configuring or navigating BotNexus, including where a setting lives, why a message went where it did, and what a portal screen is for."
compatibility: "Reference files are copies of docs/user-guide; refresh them with scripts/install-guide-skill.sh after upgrading."
metadata:
  source: docs/user-guide
---

# Operating BotNexus

This skill teaches the platform the agent is running on. The orientation below is
enough for most questions; the reference files carry the detail.

## Read this first: the model

**Gateway** — the server. One instance is one "world". Everything else hangs off it.

**Agent** — identity + provider + model + system prompt + tools + extensions. Defined
in `config.json` under `agents`, or as a JSON file in `~/.botnexus/agents/`.

**Conversation vs session** — the distinction that causes the most confusion, so
check it before answering anything about "memory" or "forgetting":

| | Conversation | Session |
|---|---|---|
| What it is | The full dialogue history | The agent's *active context window* |
| Lifetime | Permanent until archived | Cleared on "New session" |
| Survives restart | Yes | Yes, if still active |

A conversation contains many sessions. **"New session" clears what the agent
remembers, not what the user can read.** If someone says the agent "forgot", the
answer is almost always that a new session started.

**Channel binding** — `(channelType, channelAddress)` pairs that attach a
conversation to a place it can be reached. One conversation can bind to several, so
the same dialogue appears in the portal and on Telegram at once. A message from an
unrecognised address attaches to the agent's default conversation.

**Skills vs tools** — skills are markdown that shapes how an agent *thinks*; tools
are code it *executes*. This file is a skill.

## Naming traps

Correct these before they mislead someone:

- **The "Tools" item in the portal sidebar is not agent tools.** It pins external
  websites into the portal navigation. An agent's tools are configured per agent
  via `toolIds`.
- **`provider` and `model`** are the `config.json` keys. **`apiProvider` and
  `modelId`** are the REST API's field names for the same things. Both are real;
  which is correct depends entirely on which file or endpoint is being edited.
- **Model ids are exact and dated.** A retired id fails with a 404 at send time, not
  at save time. Check the model picker or `GET /api/models?provider=<id>` rather
  than trusting an id copied from older documentation.

## Where things live

| Thing | Location |
|---|---|
| Configuration | `~/.botnexus/config.json` (or `$BOTNEXUS_HOME/config.json`) |
| Credentials | Environment: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`. Keep them out of `config.json`. |
| Logs | `~/.botnexus/logs/` |
| Global skills | `~/.botnexus/skills/` |
| Per-agent skills | `~/.botnexus/agents/{agent-id}/skills/` |
| Extensions | `~/.botnexus/extensions/` |

## Portal navigation

- **Home** — pick an agent and a model, start talking.
- **Chat** — the working surface. Five tabs per agent: Conversation, Workspace
  (files it can read and write), Reports, Canvas, Todo.
- **Activity** — everything happening across the platform, with Overview, Cost and
  Cron views and filters by agent, origin, pin and live state.
- **Agents / Cron Jobs / Configuration / Skills / Plugins** — management.
- **Guide** — this material, rendered in the portal.

## Reference files

Read the relevant one before answering a detailed question; do not guess at a
setting name.

| File | Covers |
|---|---|
| `reference/getting-started.md` | Install, first run, health checks |
| `reference/conversations.md` | Routing, sessions, fan-out, threading |
| `reference/agents.md` | Agent definition and every option |
| `reference/skills.md` | Authoring and placing skills |
| `reference/automation.md` | Cron, headless runs, exit codes |
| `reference/extensions.md` | Custom tools, MCP servers |
| `reference/configuration.md` | Full `config.json` reference |
| `reference/troubleshooting.md` | Failure modes and their causes |
| `reference/channels/` | SignalR, Telegram, Service Bus |

## Answering well

- Prefer quoting the reference file over recalling. These documents have drifted
  from the code before; if a claim looks doubtful, say so rather than asserting it.
- When a question is about *this* installation rather than the platform in general,
  check the live state — the config file, the model list, the logs — instead of
  answering from the documentation.
