# Bender Decision — GatewayAuthMiddleware Path.HasExtension Auth Bypass Fix

**Date:** 2026-04-06  
**Owner:** Bender

## Context
`GatewayAuthMiddleware` had a carried P0/P1 concern where extension-shaped API routes (for example `/api/agents.json`) could be treated as static assets by extension-based skip logic.

## Decision
Use a route-based allowlist for auth bypass and explicit web-root file resolution:
- Skip auth only for:
  - `/health`
  - `/swagger` and subpaths
  - `/webui` and subpaths
  - Existing static files resolved from `wwwroot` via `IWebHostEnvironment.WebRootFileProvider`
- Never bypass auth for `/api/*` routes.

## Why
Extension checks are ambiguous and can classify API routes as static files. Route + file-provider checks tie bypass behavior to known public endpoints and real static assets.

## Validation
- `dotnet build Q:\repos\botnexus --verbosity quiet` ✅
- `dotnet test Q:\repos\botnexus\tests\BotNexus.Gateway.Tests --verbosity quiet` ✅
- Middleware regression subset (`--filter GatewayAuthMiddleware`) passed for all targeted scenarios.

## Regression Coverage Added
`GatewayAuthMiddlewareTests` now explicitly locks:
- `/api/agents.json` requires auth
- `/api/agents` requires auth
- `/health` skips auth
- `/swagger` and `/swagger/v1/swagger.json` skip auth
- Static web-root file requests (for example `/app.js`) skip auth when file exists in `wwwroot`
