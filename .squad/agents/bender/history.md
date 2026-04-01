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

**Phase 2 P1 — Item 10: Anthropic Tool Calling** (50 points)
- Add tool calling support to Anthropic provider for feature parity with OpenAI
- Update AnthropicProvider to support tool definitions, calls, and responses
- Test against same integration tests as OpenAI
- Blocked by Phase 1 P0, unblocks Phase 3 P0

## Learnings

### 2026-04-01 — Architecture Review: Anthropic Provider Gaps (from Leela)

**Critical findings affecting your work:**
- **Tool Calling Missing:** OpenAI provider supports tool calling. Anthropic provider does not. Needs implementation for feature parity (P1).
- **No DI Extension:** Anthropic provider exists but has no `AddAnthropicProvider()` method in ServiceCollectionExtensions. OpenAI has one; Anthropic needs one too (P0 blocker).
- **CA2024 Warning:** AnthropicProvider streaming has `EndOfStream` check instead of `ReadLineAsync` check. Minor fix for compiler warning (P1).
- **Provider Parity:** Once tool calling is added to Anthropic, it should be tested against the same integration tests as OpenAI to ensure feature parity.

Build is clean, tests pass. ProviderRegistry exists but is unused — evaluate integration or removal.

<!-- Append new learnings below. Each entry is something lasting about the project. -->
