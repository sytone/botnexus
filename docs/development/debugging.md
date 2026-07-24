# Debugging BotNexus

Practical debugging guide for the three surfaces you touch most often when
developing BotNexus: the **Gateway** (the ASP.NET Core host and agent runtime),
**extensions** (dynamically loaded providers, channels, and tools), and the
**WebUI** (the Blazor front-end and its SignalR connection).

This guide assumes you can already build and run BotNexus. If not, start with
[Developer setup](../getting-started-dev.md).

---

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| .NET SDK 10.0+ | `dotnet --version` |
| PowerShell 7+ | All repo scripts are `pwsh`. |
| A debugger | Visual Studio, VS Code (C# Dev Kit), or JetBrains Rider. |

BotNexus runs on Windows and Linux. Everything below works on both; only the
attach mechanics differ per debugger.

---

## Debugging the Gateway

The Gateway is a standard ASP.NET Core application in
`src/gateway/BotNexus.Gateway`. There are two ways to run it under a debugger.

### Launch from your IDE

Set `BotNexus.Gateway` as the startup project and run with debugging (F5). The
Gateway starts on `http://localhost:5005` with the WebUI at the root URL. Set
breakpoints anywhere in the solution and they bind normally.

### Attach to a running Gateway

If you started the Gateway with `.\scripts\dev-loop.ps1` or
`.\scripts\start-gateway.ps1`, attach your debugger to the `BotNexus.Gateway`
process. In VS Code: **Run → Attach to .NET Process** and pick the Gateway PID.

> **Tip:** Run without the test/build phases when you only need to debug:
> `.\scripts\start-gateway.ps1 -SkipBuild` starts faster once you have a good
> build.

### Logs

Application logs are written under `~/.botnexus/logs/`. Increase verbosity with
the standard ASP.NET Core logging configuration in `appsettings.json` (or via
the `Logging__LogLevel__Default` environment variable), then reproduce the
issue and read the log. Structured log lines carry the session and agent
identifiers, so filter on those when tracing a single conversation.

### Crash diagnostics

BotNexus captures a minidump and a last-chance fault breadcrumb on unclean
shutdown, and detects unclean shutdowns on the next start. When the Gateway
dies unexpectedly, the artifacts it leaves behind are your first stop — see
[Gateway crash diagnostics](gateway-crash-diagnostics.md) for where they land
and how to read them.

### Tracing a message end to end

When behaviour is wrong but nothing crashes, follow the message through the
pipeline rather than guessing. These walkthroughs map the exact call path:

- [Message flow](message-flow.md) — channel dispatch, routing, session lifecycle.
- [LLM request lifecycle](llm-request-lifecycle.md) — how a user message becomes an LLM API call.
- [Prompt pipeline](prompt-pipeline.md) — system-prompt construction and caching.
- [Agent execution](agent-execution.md) — agent lifecycle, isolation, instance management.

Set a breakpoint at the stage the walkthrough identifies rather than at the
channel entry point — it collapses most "why did the agent do that?" hunts.

---

## Debugging extensions

Extensions (providers, channels, tools) are class libraries loaded dynamically
from `~/.botnexus/extensions/{providers|channels|tools}/` at Gateway startup.
Because they load into the Gateway process, you debug them by debugging the
Gateway — but the build/copy step in the middle trips people up.

### The load cycle

1. Build your extension against `BotNexus.Gateway.Abstractions` (target `net10.0`).
2. Copy the built DLL to the matching `~/.botnexus/extensions/` subfolder.
3. **Restart the Gateway** — extension assemblies are discovered at startup, not hot-reloaded.
4. Attach or launch under the debugger; breakpoints in your extension bind once the assembly loads.

See [Extension development](../extension-development.md) for the full authoring guide.

### When an extension does not load

| Symptom | Likely cause |
|---|---|
| Extension never appears / no breakpoint binds | DLL not in the correct `{providers\|channels\|tools}` subfolder, or Gateway not restarted after copy. |
| `TypeLoadException` / missing method on load | Extension built against a different `BotNexus.Gateway.Abstractions` version than the running Gateway. Rebuild against the current abstractions. |
| Loads but is never invoked | Provider/channel/tool not referenced by any enabled agent in `config.json`. |

Check the startup logs in `~/.botnexus/logs/` — extension discovery and any
load failures are logged there with the assembly path.

### Faster inner loop

Symlink or post-build-copy your extension's output directly into the
`~/.botnexus/extensions/` subfolder so each rebuild lands in place, then just
restart the Gateway. Avoid editing the copied DLL by hand — always rebuild from
source so the PDB stays in sync and breakpoints keep binding.

---

## Debugging the WebUI

The WebUI is a Blazor front-end served by the Gateway. It talks to the backend
over a SignalR hub at `/hub/gateway` using a subscribe-all model — see
[WebUI connection](webui-connection.md) for the connection and multi-session
architecture.

### Client-side (browser)

Open your browser's developer tools:

- **Console** — Blazor and SignalR client errors surface here first.
- **Network → WS** — inspect the `/hub/gateway` WebSocket frames to see exactly what the client sent and received.
- Reproduce the issue with DevTools open; a "Disconnected" banner almost always has a matching WS close frame or console error explaining why.

### Server-side (hub and components)

The hub and Razor components run in the Gateway process, so debug them the same
way you debug the Gateway: set breakpoints in the component or hub code and
launch/attach. Server-side render errors also land in `~/.botnexus/logs/`.

### Common WebUI symptoms

| Symptom | Fix |
|---|---|
| WebUI shows "Disconnected" | Confirm the Gateway is running; restart with `.\scripts\dev-loop.ps1`. Check the WS frames for the close reason. |
| Changes to a `.razor` file not showing | Rebuild — component changes require a rebuild, not just a page refresh. |
| Messages send but no streamed reply appears | Watch the `/hub/gateway` WS frames; trace the server side via [Message flow](message-flow.md). |

---

## Writing tests instead of poking

Reproducing a bug in a test is usually faster than manual debugging and leaves a
regression guard behind — which AGENTS.md requires anyway. Impacted-test
selection and the local test workflow are documented in
[Running tests](running-tests.md). Prefer a focused failing test that pins the
bug, then fix until it passes.

---

## Related documentation

- [Developer setup](../getting-started-dev.md) — build, run, and configure BotNexus.
- [Gateway crash diagnostics](gateway-crash-diagnostics.md) — minidumps and fault breadcrumbs.
- [Message flow](message-flow.md), [LLM request lifecycle](llm-request-lifecycle.md), [Prompt pipeline](prompt-pipeline.md) — runtime walkthroughs.
- [WebUI connection](webui-connection.md) — SignalR hub and multi-session model.
- [Extension development](../extension-development.md) — building providers, channels, and tools.
- [Running tests](running-tests.md) — impacted-test selection and local test flow.
