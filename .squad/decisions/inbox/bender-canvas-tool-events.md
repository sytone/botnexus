# 2026-08-03 — Bender: Canvas tool event contract for Issue #245 Phase 4

## Decision

Canvas updates from runtime now flow through a transport-neutral gateway contract:

- `IAgentCanvasNotifier` lives in `src/gateway/BotNexus.Gateway.Contracts/Agents/IAgentCanvasNotifier.cs`
- Runtime `canvas` tool publishes via notifier with current agent scope only
- SignalR extension implements notifier and emits `CanvasUpdated(agentId, html)` payloads

## Why

This preserves the gateway-extension boundary (no `Gateway.Api` or runtime dependency on SignalR details) while enabling real-time canvas updates for the portal Canvas tab.

## Scope / Security

- `canvas` tool supports `action=render|clear`
- `render` requires HTML payload; `clear` emits empty HTML
- Agent scope is enforced from runtime context (`descriptor.AgentId`), not tool input
- No persistence, no file writes, no report interactions

## Validation

- Gateway tests: canvas tool behavior + SignalR notifier payload broadcast
- Blazor tests: canvas event handling + sandboxed iframe rendering
- Full solution build and tests pass (E2E suite remains intentionally skipped where configured)
