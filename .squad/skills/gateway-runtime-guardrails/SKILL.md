# Gateway Runtime Guardrails

## When to use
- Adding or reviewing Gateway API runtime protections around auth, capacity, and connection lifecycle.
- Wiring security handlers that already exist in core runtime into ASP.NET request flow.

## Pattern
1. **Boundary-first auth**
   - Add middleware in API host before endpoint mapping.
   - Bypass only explicitly public paths (`/health`, `/webui`, static assets, `/swagger`).
   - Reuse `IGatewayAuthHandler` to preserve dev-mode behavior (no keys => allow all).
2. **Capacity enforcement at supervisor**
   - Enforce `AgentDescriptor.MaxConcurrentSessions` in `DefaultAgentSupervisor.GetOrCreateAsync`.
   - Use dedicated exception (`AgentConcurrencyLimitExceededException`) so API can map to `429`.
3. **Config validation before spawn**
   - Validate descriptor and isolation strategy against registered strategy names before `CreateAsync`.
   - Include available strategy names in error messages.
4. **Session-level WS lock**
   - Track active `sessionId` connections with `ConcurrentDictionary`.
   - Reject duplicate connections with WebSocket close code `4409`.

## Implementation anchors
- `src/gateway/BotNexus.Gateway.Api/GatewayAuthMiddleware.cs`
- `src/gateway/BotNexus.Gateway/Agents/DefaultAgentSupervisor.cs`
- `src/gateway/BotNexus.Gateway.Api/WebSocket/GatewayWebSocketHandler.cs`
