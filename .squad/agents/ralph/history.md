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

**Phase 1 P1 — Item 7: Slack Webhook Endpoint** (35 points)
- Add POST /webhook/slack endpoint in Gateway to receive Slack event subscriptions
- Validate Slack request signatures for security
- Handle Slack challenge handshake and event callbacks
- Blocked by Phase 1 P0 (core extensions), unblocks Phase 3

**Phase 3 P0 — Item 13: Config Validation All** (20 points)
- Validate all config sections on startup (Providers, Channels, Tools, Gateway, Api, etc.)
- Fail fast with helpful error messages if invalid
- Prevents silent failures and confusing runtime errors

**Phase 4 P1 — Item 21: IaC Containerization** (30 points)
- Write Dockerfile and docker-compose.yml for easy deployment
- Support env var overrides for all config
- Make deployment reproducible and portable

## Learnings

Initial setup complete.

<!-- Append new learnings below. Each entry is something lasting about the project. -->
