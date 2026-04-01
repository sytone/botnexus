# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Learnings

### 2026-04-01 — Architecture Review: Auth & Channel Gaps (from Leela)

**Critical findings affecting your work:**
- **No Auth Anywhere:** Gateway REST, WebSocket, and API endpoints have zero authentication/authorization. Anyone who can reach port 18790 owns the system. This is P0 blocking for any public deployment (P1 - defer implementation but urgently needed).
- **Slack Webhook Gap:** Slack channel supports webhook mode (`HandleMessageAsync` is public), but Gateway has no incoming webhook POST endpoint to receive Slack event subscriptions. You'll need to add an endpoint that accepts Slack's challenge and event callbacks (P1).
- **Channel Registration:** Discord/Slack/Telegram channels are implemented but not registered in DI. They're dead code until registration is added (see Amy's P0 list).
- **WebSocket Security:** Currently no token validation on WebSocket connection. Once you add auth, WebSocket must validate the auth token.

Baseline: build is clean, all 124 tests pass. Ready for implementation.

<!-- Append new learnings below. Each entry is something lasting about the project. -->
