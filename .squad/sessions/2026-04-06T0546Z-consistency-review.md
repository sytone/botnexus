# Phase 10 Consistency Review

**Reviewer:** Nibbler  
**Date:** 2026-04-06  
**Scope:** Phase 10 — CLI commands, WebSocket handler decomposition, API changes, config defaults, cross-file consistency  
**Grade:** Good

## Summary

Phase 10 added significant new functionality (CLI commands, WebSocket decomposition, PUT validation, CORS). Code quality is excellent — zero code-level bugs. All issues were in documentation alignment and one provider naming inconsistency in the CLI.

## Findings

### P1 — Fixed (6)

| # | Area | Issue | Fix |
|---|------|-------|-----|
| 1 | api-reference.md | PUT /api/agents/{agentId} docs said "URL takes precedence" and overwrites body ID. Code actually returns 400 on mismatch. | Updated docs to describe correct 400 behavior. |
| 2 | CLI provider name | `botnexus init` and `agent add` defaulted to `"github-copilot"`, but all docs, config examples, and code comments use `"copilot"`. Would confuse every user running `botnexus init`. | Changed CLI defaults to `"copilot"`. |
| 3 | Gateway README | Missing `PUT /api/agents/{agentId}` in endpoint list. | Added to Agents section. |
| 4 | Gateway README | `tool_end` WebSocket event missing `toolName` and `toolIsError` fields in protocol docs. | Added both fields to match XML docs and wire format. |
| 5 | platform-config.example.json | Used `"auth:github-copilot"` while all other docs use `"auth:copilot"`. | Changed to `"auth:copilot"`. |
| 6 | sample-config.json | `apiKeys` section at root level instead of nested under `gateway.apiKeys`. Inconsistent with Gateway README and PlatformConfig class structure. | Moved under `gateway`. |

### P1 — Documentation Gaps Fixed (4)

| # | Area | Issue | Fix |
|---|------|-------|-----|
| 7 | dev-loop.md | No mention of CLI commands (`init`, `agent list/add/remove`, `config get/set`) despite CLI project being listed in the edit directory table. | Added CLI Tool section with command table. |
| 8 | getting-started-dev.md | `botnexus init` section only showed global tool usage, not `dotnet run` from source. Developers building from source need the `dotnet run --project` form. | Added `dotnet run --project` alternative. |
| 9 | api-reference.md | CORS behavior completely undocumented despite being environment-aware (dev=permissive, prod=restricted methods). | Added CORS section under Authentication. |
| 10 | README.md | CLI key commands list was stale — missing `init`, `agent list/add/remove`, `config get/set`. | Updated command list. |

### P1 — WebSocket Protocol (2)

| # | Area | Issue | Fix |
|---|------|-------|-----|
| 11 | Gateway README | `connected` event missing `sequenceId` field (present in XML docs and wire format). | Added `sequenceId` to protocol example. |
| 12 | Gateway README | Session endpoints missing `history`, `suspend`, `resume` from summary list. | Added all three. |

### P2 — Noted (3)

| # | Area | Issue | Status |
|---|------|-------|--------|
| 1 | Test files | `GatewayWebSocketHandlerTests.cs` covers the decomposed handler but no separate test files for `WebSocketConnectionManager` or `WebSocketMessageDispatcher`. | Tests still validate behavior correctly. Split when test count grows. |
| 2 | configuration.md | Port 18790 reference still in some config examples. | Pre-existing from Phase 9 review. |
| 3 | README project structure | Lists `BotNexus.Core`, `BotNexus.Api`, `BotNexus.Diagnostics` which may not reflect current directory layout. | Cannot verify — frozen directories. |

## Consistency Check: WebSocket Handler Decomposition

- **GatewayWebSocketHandler** — Orchestrates lifecycle; delegates to ConnectionManager and Dispatcher. XML docs comprehensive and match behavior. ✅
- **WebSocketConnectionManager** — Admission control, session locks, throttling. XML docs accurate. ✅
- **WebSocketMessageDispatcher** — Inbound dispatch, outbound framing. XML docs accurate. ✅
- No docs reference the old monolithic handler by name. ✅
- TestSpecification.md remains accurate — describes behavior, not implementation classes. ✅

## Consistency Check: Provider Naming

| Source | Provider Name Used | Status |
|--------|-------------------|--------|
| CLI `init` default | ~~`github-copilot`~~ → `copilot` | **Fixed** |
| CLI `agent add` default | ~~`github-copilot`~~ → `copilot` | **Fixed** |
| platform-config.example.json | ~~`auth:github-copilot`~~ → `auth:copilot` | **Fixed** |
| sample-config.json | `copilot` | ✅ Already correct |
| dev-loop.md | `copilot` | ✅ |
| getting-started-dev.md | `copilot` | ✅ |
| README.md | `copilot` | ✅ |
| Gateway README | `copilot` | ✅ |
| AgentDescriptor.cs XML doc | `copilot` | ✅ |
| GatewayAuthManager.cs | Accepts both `copilot` and `github-copilot` | ✅ Backwards compatible |

## Build & Test Verification

- **CLI build:** ✅ 0 errors, 0 warnings
- **Gateway tests:** ✅ 289/289 pass

## Files Modified

- `docs/api-reference.md` — PUT endpoint behavior, CORS section
- `docs/dev-loop.md` — CLI Tool section
- `docs/getting-started-dev.md` — `dotnet run --project` init alternative
- `docs/platform-config.example.json` — Provider name fix
- `docs/sample-config.json` — apiKeys nesting fix
- `src/gateway/BotNexus.Cli/Program.cs` — Provider default "copilot"
- `src/gateway/README.md` — PUT endpoint, session endpoints, WS protocol fields
- `README.md` — CLI command list update
