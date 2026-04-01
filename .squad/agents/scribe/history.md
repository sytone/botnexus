# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Team Directives (All Agents Must Follow)

1. **Dynamic Assembly Loading** (2026-04-01T16:29Z)
   - All extensions (channels, providers, tools) must be dynamically loaded from `extensions/{type}/{name}/` folders
   - Configuration drives what loads — nothing loads by default unless referenced in config
   - Reduces security risk, keeps codebase abstracted
   - See decisions.md Section "Part 1: Dynamic Assembly Loading Architecture"

2. **Conventional Commits Format** (2026-04-01T16:43Z)
   - Use feat/fix/refactor/docs/test/chore prefixes on ALL commits
   - Commit granularly — one commit per work item or logical unit, not one big commit at end
   - Makes history clean, reversible, and easy to review

3. **Copilot Provider P0** (2026-04-01T16:46Z)
   - Copilot is the only provider Jon uses — it is P0, all other providers P1/P2
   - Use OAuth device code flow (like Nanobot) — no API key
   - Base URL: https://api.githubcopilot.com
   - Prioritize Copilot work before OpenAI, Anthropic

## Your Work Assignment

**Phase 1 P1 — Item 6: Gateway Authentication** (40 points)
- Add API key validation to Gateway REST and WebSocket endpoints
- Implement middleware for API key checking on /api/* routes
- Add token validation on WebSocket connection handshake
- Return 401 Unauthorized if missing or invalid
- Make API key configurable via config

**Phase 4 P0 — Item 16: Observability Metrics** (40 points)
- Add .NET metrics for:
  - Tool calls (count, latency by tool)
  - Agent loops (count, latency)
  - Provider calls (count, latency by provider)
  - Message processing (throughput, queue depth)
- Use System.Diagnostics.Metrics for .NET metrics
- Make metrics exportable to observability platforms

**Phase 4 P1 — Item 19: API Health Endpoint** (20 points)
- Add GET /health endpoint in Gateway API
- Check health of all providers, channels, MCP servers
- Return aggregated health status and component details
- Return 200 OK if all healthy, 503 if any unhealthy
- Format: JSON with provider/channel/server status and last check time

## Learnings

Initial setup complete.

<!-- Append new learnings below. Each entry is something lasting about the project. -->
