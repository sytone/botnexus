# Nibbler — Phase 12 Wave 2 Consistency Review

**Date:** 2026-04-06  
**Reviewer:** Nibbler (Consistency Reviewer)  
**Scope:** Phase 12 Wave 2 changes — SupportsThinkingDisplay rename, session metadata endpoints, config version, auth/rate-limiting/correlation-ID middleware, WebUI panels, +24 tests  
**Grade:** Good

---

## Summary

7 P1 issues found and fixed. 0 P0s. 1 P2 noted. Code quality continues to be excellent — all issues were documentation/WebUI alignment gaps, which is the established pattern for this project. Test naming, XML doc coverage, and SupportsThinkingDisplay rename were all clean.

---

## P1 Fixes Applied (7)

### 1. WebUI channels panel: stale field name `supportsThinking`
- **File:** `src/BotNexus.WebUI/wwwroot/app.js` line 1449
- **Issue:** Used old field name `ch.supportsThinking` instead of renamed `ch.supportsThinkingDisplay`
- **Impact:** Thinking capability badge (💭) would never display for any channel
- **Fix:** Changed to `ch.supportsThinkingDisplay`

### 2. api-reference.md: missing session metadata endpoints
- **File:** `docs/api-reference.md`
- **Issue:** GET /api/sessions/{sessionId}/metadata and PATCH /api/sessions/{sessionId}/metadata not documented
- **Fix:** Added full endpoint documentation with request/response examples

### 3. api-reference.md: missing correlation ID header docs
- **File:** `docs/api-reference.md`
- **Issue:** `X-Correlation-Id` header completely undocumented despite being on every request/response
- **Fix:** Added "Request & Response Headers" section documenting header behavior

### 4. api-reference.md: HTTP rate limiting undocumented
- **File:** `docs/api-reference.md`
- **Issue:** Only WebSocket rate limiting mentioned; HTTP REST rate limiting via `RateLimitingMiddleware` not documented. 429 status code only mentioned "concurrency limit"
- **Fix:** Added rate limiting documentation with config example; updated 429 description and error codes table

### 5. Gateway.Api README: missing middleware documentation
- **File:** `src/gateway/BotNexus.Gateway.Api/README.md`
- **Issue:** Middleware section only documented Auth and CORS. Missing RateLimitingMiddleware and CorrelationIdMiddleware. Sessions table missing metadata endpoints.
- **Fix:** Added both middleware sections, updated Middleware & Security type table, added metadata endpoints to sessions table

### 6. Gateway README: project tree stale
- **File:** `src/gateway/README.md`
- **Issue:** File tree listed only `GatewayAuthMiddleware.cs`, missing `RateLimitingMiddleware.cs` and `CorrelationIdMiddleware.cs`
- **Fix:** Added both files to project tree with descriptions

### 7. Config version field undocumented
- **Files:** `docs/configuration.md`, `docs/sample-config.json`, `docs/platform-config.example.json`, `src/gateway/README.md`
- **Issue:** `PlatformConfig.Version` (int, default 1) exists in code but not in any documentation or config examples
- **Fix:** Added to configuration.md Root table, both config example files, and Gateway README config snippet. Also added `rateLimit` config properties to Gateway section of configuration.md.

---

## P2 Noted (1)

### 1. WebSocket vs HTTP rate limit parameters diverge
- api-reference.md WebSocket section says "20 attempts per 300-second window" while HTTP `RateLimitingMiddleware` defaults to "60 requests per 60-second window"
- These may be intentionally different (WebSocket connection attempts vs HTTP requests), but the disconnect could confuse integrators
- **Recommendation:** Add a note clarifying the difference, or unify under the same `RateLimitConfig`

---

## Clean Areas (No Issues)

| Area | Status |
|------|--------|
| SupportsThinking → SupportsThinkingDisplay rename | ✅ Complete across all code, DTOs, tests, docs |
| Test naming (MethodName_Scenario_ExpectedResult) | ✅ All 24+ new tests follow convention |
| XML doc comments on new public APIs | ✅ 100% coverage — all middleware, controllers, DTOs documented |
| Auth middleware constructor injection | ✅ Correctly implemented, accurately documented |
| WebUI extensions panel | ✅ Correct endpoint paths and field names |

---

## Patterns Reinforced

1. **WebUI is a new staleness vector** — The channels panel shipped with the old `supportsThinking` field name because WebUI JS isn't type-checked against C# DTOs. This is the first JavaScript-level drift we've caught.
2. **New middleware = new doc sections** — Rate limiting and correlation ID middleware were fully implemented but completely absent from all READMEs and api-reference.md. Same pattern as every prior phase.
3. **Config fields drift silently** — `Version` field added to `PlatformConfig` but none of the 4 documentation/example files updated. Config examples need a compile-check equivalent.
