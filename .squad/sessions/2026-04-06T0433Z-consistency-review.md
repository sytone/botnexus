# Consistency Review — Gateway Service (Post Phase 7+)

**Reviewer:** Nibbler  
**Date:** 2026-04-06T04:33Z  
**Scope:** Docs ↔ Code, Code ↔ Comments, Config ↔ Code, Stale References, Test ↔ Code

---

## Summary

**Result: 0 P0, 4 P1 (fixed), 5 P2 (fixed), 3 P2 (noted)**

Code quality remains excellent — zero code-level issues. All findings are documentation alignment gaps caused by incremental feature additions across 7+ phases without corresponding doc updates.

---

## P1 Issues (Fixed)

### 1. api-reference.md: Session response shape wrong
Session list endpoint documented `key`, `agentName`, `title`, `messageCount` fields. Actual `GatewaySession` has `sessionId`, `agentId`, `status`, `channelType`, `callerId`, `createdAt`, `updatedAt`. Fixed response example to match actual model.

### 2. api-reference.md: Missing session endpoints
Three session endpoints exist in `SessionsController` but were undocumented:
- `GET /api/sessions/{sessionId}/history` — paginated history with offset/limit
- `PATCH /api/sessions/{sessionId}/suspend` — suspend active session
- `PATCH /api/sessions/{sessionId}/resume` — resume suspended session

Added complete documentation for all three.

### 3. api-reference.md: Missing status codes and error codes
- `429 Too Many Requests` missing from common status codes table (used by chat endpoints)
- `202 Accepted` missing (used by steer/follow-up)
- `409 Conflict` description didn't mention state conflicts
- Error codes table missing `CONCURRENCY_LIMIT` (429) and `STATE_CONFLICT` (409)

### 4. platform-config.example.json: Stale format
Used flat/legacy structure with port 5000 and deprecated field layout. Updated to modern nested format matching `PlatformConfig` class: `gateway`, `agents`, `providers`, `channels` sections, port 5005.

---

## P2 Issues (Fixed)

### 5. gateway README: Project table listed non-existent WebUI project
`BotNexus.Gateway.WebUI` doesn't exist under `src/gateway/`. Replaced with `BotNexus.Cli` which is the actual 5th project.

### 6. gateway README: Auth exemption included non-existent static file check
Documented "Static files (paths with file extensions)" as auth-exempt, but `GatewayAuthMiddleware.ShouldSkipAuth()` only checks `/health`, `/webui`, `/swagger`. Removed the incorrect row.

### 7. gateway README: IChannelAdapter snippet property order and parameter names
`IsRunning` property was before capability flags (should be after), and method parameters used `ct` instead of `cancellationToken`. Updated to match actual interface code.

### 8. configuration.md: Wrong project paths
Referenced `src/BotNexus.Gateway/appsettings.json` and `src/BotNexus.Api/appsettings.json`. Actual path is `src/gateway/BotNexus.Gateway.Api/appsettings.json`. Fixed.

### 9. api-reference.md: Error response format
Documented `{ error, code, statusCode, timestamp }` but actual middleware returns `{ error, message }`. Fixed to match `GatewayErrorResponse` record and controller patterns.

---

## P2 Issues (Noted, Not Fixed — Outside Scope)

### 10. api-reference.md: Skills/Doctor endpoints
`GET /api/skills`, `GET /api/agents/{name}/skills`, and `GET /api/doctor` are documented but have no corresponding controllers in `Gateway.Api`. These are likely provided by the main BotNexus application host. Added scope notes to clarify.

### 11. api-reference.md: Create Agent side effects
Docs describe workspace bootstrapping, config backup, and name normalization as side effects of `POST /api/agents`. The controller only calls `_registry.Register()` — side effects likely happen in the registry implementation.

### 12. architecture.md / extension-development.md: BotNexus.Core references
~40 references to `BotNexus.Core` across architecture and extension development docs. These reference the main product's core module (not in Gateway scope). Noted for future review when those modules are refactored.

---

## Code Quality Notes

- **Zero TODO/FIXME/HACK comments** in gateway code
- **Zero BotNexusConfig references** in gateway code — clean separation between PlatformConfig (Gateway) and BotNexusConfig (main product)
- **XML doc coverage** remains near 100% across all gateway interfaces and models
- **All previous P1s remain fixed** from prior reviews
- **Gateway builds cleanly** with 0 errors, 0 warnings (in library code)

---

## Patterns

1. **Session endpoint documentation lag** — Three endpoints (history, suspend, resume) were added in code but never documented. This matches the pattern from Phase 6 where chat endpoints existed for 3 phases before being documented.
2. **Config example staleness** — `platform-config.example.json` reflected the original Phase 1 flat format, never updated when PlatformConfig moved to nested sections.
3. **Phantom project references** — The WebUI project was listed in the README table but doesn't exist in `src/gateway/`, suggesting it was moved or renamed without updating the README.
