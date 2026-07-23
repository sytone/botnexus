# Break-Glass Gateway Recovery

When the BotNexus gateway fails to start or become healthy, the built-in BotNexus
helper agent (Nexus Trailguide) cannot help you — the gateway that hosts the agent
runtime is the very thing that is down. The CLI only surfaces a generic 10-second
`/health` timeout, which **hides the real fault**.

`scripts/recover-gateway.ps1` is a standalone break-glass tool that runs independently
of a running gateway. It gathers diagnostics and can hand off to an interactive GitHub
Copilot CLI session primed with everything it needs to find the fault and propose a fix,
so you are never fully blocked getting the gateway green again.

## Quick start

```powershell
# From the repo root
pwsh -File scripts/recover-gateway.ps1
```

This will:

1. Probe `/health` and report whether the gateway is up.
2. Inspect the gateway process and listening port.
3. Find the newest hourly log (`~/.botnexus/logs/botnexus-YYYYMMDDHH.log`) and extract
   `[ERR]`/`[FTL]`/exception lines — the true cause the CLI timeout hides.
4. List the deployed extension set (a common source of startup crashes).
5. Capture git branch/HEAD (and the deployed profile repo HEAD if different).
6. Write a structured diagnostic report to a temp `.md` file.
7. If the gateway is unhealthy, offer to launch **GitHub Copilot CLI interactively** in
   the repo, primed with a platform-aware prompt, to help diagnose and fix.

If `/health` returns 200 the script reports the gateway is healthy and does not launch
Copilot.

## Options

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `-GatewayUrl` | `http://localhost:5005` | Gateway base URL for the health probe. |
| `-RepoPath` | repo containing the script | BotNexus repo path. |
| `-ConfigDir` | `~/.botnexus` | Config/state directory (logs, extensions). |
| `-LogLines` | `200` | Trailing log lines to capture. |
| `-NoCopilot` | off | Only produce the diagnostic report; skip the Copilot handoff. |
| `-Yes` | off | Launch Copilot without prompting. |

```powershell
# Just gather the report (e.g. to attach to a GitHub issue)
pwsh -File scripts/recover-gateway.ps1 -NoCopilot

# Non-interactive machines / automation: report only
pwsh -File scripts/recover-gateway.ps1 -NoCopilot -GatewayUrl http://localhost:5005
```

## The Copilot handoff

When the gateway is down and [GitHub Copilot CLI](https://github.com/github/copilot-cli)
(`copilot`) is on `PATH`, the script offers to launch it interactively inside the repo.
The priming prompt tells Copilot:

- What BotNexus is and how the gateway is started (native apphost
  `BotNexus.Gateway.Api.exe` or `dotnet BotNexus.Gateway.Api.dll`).
- Where config, logs, and extensions live.
- The **known recurring crash class**: an extension shipping a private copy of a
  host-registered contract assembly (e.g. `IConfiguration`, `IFileSystem` via
  `System.IO.Abstractions`) diverges the type identity, DI stops recognising it, and the
  host aborts on startup. See the fix precedent in PR #2218 / issue #2184, and the
  permanent-fix tracking issues #2219 (categorical unification) and #2220 (boot smoke gate).
- The path to the already-gathered diagnostic report.
- Explicit instruction **not** to restart, rebuild, or push anything without your
  confirmation.

Copilot can then read the report and logs, explain the root cause, and — with your
approval — file an issue (`gh`) or open a PR following the repo's worktree +
Conventional Commits workflow.

## When to reach for this

- Gateway won't start after an update or an extension change.
- CLI reports only `Health check failed for http://localhost:5005/health`.
- You suspect an extension assembly-load regression (the most common cause).
- You want a diagnostic bundle to attach to a GitHub issue.

## Related

- [Gateway Crash Diagnostics](../development/gateway-crash-diagnostics.md) — deeper
  crash-analysis reference.
- [Watchdog Setup](./watchdog-setup.md) — automated health monitoring and restart.
