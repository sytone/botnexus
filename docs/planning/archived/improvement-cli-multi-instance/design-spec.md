---
id: improvement-cli-multi-instance
title: "Improvement: Full multi-instance support via --source and --target"
type: improvement
priority: medium
status: in-progress
created: 2026-04-28
author: rusty
---

# Improvement: Full multi-instance CLI support

**Type**: Improvement  
**Priority**: Medium  
**Status**: In-progress (partially delivered by PR #22)

## Background

The CLI previously hardcoded `~/.botnexus` as the BotNexus home directory and `~/botnexus` as the
source repo. Running two instances (prod + dev) from the same machine required the sync script to
manage process launch directly.

## What PR #22 delivered

- `--source` on `build`, `serve`, `gateway start/restart`, `install` — specifies repo location (default: `~/botnexus`)
- `--target` on `gateway start/stop/status/restart`, `serve` — specifies runtime home (default: `~/.botnexus`, sets `BOTNEXUS_HOME`)
- `GatewayProcessManager` reads `BOTNEXUS_HOME` env var / accepts `homePath` constructor param
- `CliPaths` helper centralises default resolution
- `DeployExtensions` takes explicit home path

The sync script can now be simplified to:

```powershell
# prod
botnexus gateway start --source ~/botnexus-prod --target ~/.botnexus --port 5005

# dev
botnexus gateway start --source ~/projects/botnexus --target ~/.botnexus-dev --port 5006
```

## Remaining work

The following commands still read `PlatformConfigLoader.DefaultConfigPath` directly and do not
respect `--target`:

- `InitCommand` — initialises config at `~/.botnexus`
- `ProviderCommand` — reads/writes provider credentials
- `ConfigCommands` — reads/writes `config.json`
- `DoctorCommand` — health checks config path
- `AgentCommands` / `MemoryCommands` — agent and memory management
- `ValidateCommand` — validates config at default path

### Required change

Add `--target` as a global option on the root command (or a shared option on affected commands)
and thread it through to `PlatformConfigLoader` so all commands respect the specified home.

The cleanest approach is a **global `--target` option** on the root `RootCommand` in `Program.cs`,
resolved once and passed via a shared `CliContext` or `IOptions<CliOptions>` to all commands.

```bash
# Full multi-instance — all commands respect --target
botnexus --target ~/.botnexus-dev provider setup
botnexus --target ~/.botnexus-dev gateway status
botnexus --target ~/.botnexus-dev config validate
```

## Done When

- All commands respect `--target` (or global `--target` on root)
- `botnexus-sync.ps1` delegates gateway lifecycle entirely to the CLI
- `botnexus gateway status --target ~/.botnexus-dev` correctly reports the dev instance state
