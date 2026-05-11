# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform built in C#/.NET
- **Stack:** C# (.NET latest), modular class libraries, dynamic extension loading
- **Created:** 2026-04-01

## Core Context

**Zapp's Specialization:** E2E simulation and deployment lifecycle testing. Split from Hermes: Hermes tests code quality; Zapp tests customer experience.

**Key Delivered Infrastructure:**
- 10 deployment lifecycle E2E tests (SC-DPL-001 through SC-DPL-010) in `tests/BotNexus.Tests.Deployment/`
- `GatewayProcessFixture`: real OS process lifecycle — starts Gateway via `dotnet <dll>`, polls /health, kills on cleanup
- Scenario registry (`tests/SCENARIOS.md`): 56 scenarios, 48 covered (86%)
- Categories: agent workspace, memory, deployment lifecycle, multi-agent simulation

**Key Discoveries:**
- SessionManager path = `{workspace}/sessions/` (from `config.Agents.Workspace`), NOT `{BOTNEXUS_HOME}/sessions/`
- Agent workspaces are lazy-created on first message (via `AgentContextBuilder.BuildSystemPromptAsync`), not at startup
- Extension loader does NOT auto-scan folders — extensions must be explicitly configured
- xUnit 2.9.x does NOT reliably call `IAsyncDisposable.DisposeAsync()` on test classes — use `await using var fixture` inside test methods
- `Process.Kill(entireProcessTree: true)` required on Windows to avoid orphaned child processes

## Learnings

- Real process E2E tests need isolated temp BOTNEXUS_HOME per test, random ports, and `await using` for guaranteed cleanup.
- Health endpoint: `http://localhost:{port}/health` (no auth, 200 OK with `{"status":"ok"}`).
- Gateway DLL path: `{repoRoot}/src/gateway/BotNexus.Gateway.Api/bin/Release/net10.0/BotNexus.Gateway.Api.dll`.
- `Workspace: "~/.botnexus"` in test config aligns SessionManager path with expected session storage location.
- Sprint 5 established patterns: workspace + identity files, memory management (daily + consolidation), deployment lifecycle validation.
