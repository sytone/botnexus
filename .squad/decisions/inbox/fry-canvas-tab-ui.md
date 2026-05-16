# Fry — Canvas tab UI decision notes

## Context
- Scope: Issue #245 Phase 4 (Blazor client)
- Area: `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient`

## Decision
1. Replace AgentPanel canvas placeholder with `CanvasPanel`.
2. Render canvas HTML using iframe `srcdoc` from agent-scoped state (`AgentState.CanvasHtml`).
3. Support updates from both:
   - Hub `CanvasUpdated` payloads
   - `ToolEnd` events where `toolName == "canvas"` (including JSON payloads containing `html`/`srcdoc`)
4. Clear canvas state when payload/result HTML is empty or whitespace.
5. Sandbox policy: `sandbox="allow-scripts"` only.
   - Explicitly no `allow-same-origin`
   - Explicitly no `allow-top-navigation`
6. Preserve existing query-based deep-linking (`?tab=canvas`) with no route regression to other tabs.
7. Mobile behavior: keep canvas pane scroll-contained to prevent page-level overflow.

## Validation
- Blazor client test project passes with added Canvas, AgentPanel, and gateway handler coverage.
- Full solution build and test run completed successfully.
