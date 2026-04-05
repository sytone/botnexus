### 2026-04-06: P0 — GatewayWebSocketHandler streaming history loss

**By:** Bender (Runtime Dev)  
**Requested by:** Jon Bullen (Copilot)  
**Status:** Implemented

#### Decision
Apply the same streaming history capture pattern from `GatewayHost.DispatchAsync` directly inside `GatewayWebSocketHandler.HandleUserMessageAsync` instead of introducing a shared helper now.

#### Rationale
A shared helper would cross project boundaries between `BotNexus.Gateway` and `BotNexus.Gateway.Api` and expand the refactor surface during a blocking P0. Duplicating the proven pattern keeps risk low and closes the data-loss bug immediately.

#### Implementation Summary
- Added `StringBuilder streamedContent` to assemble full assistant text from `ContentDelta` events.
- Added `List<SessionEntry> streamedHistory` to capture tool lifecycle entries for `ToolStart` and `ToolEnd`.
- After streaming completes, append tool entries plus final `assistant` entry to `session.History` before `SaveAsync`.

#### Validation
- `dotnet build --nologo` ✅
- `dotnet test tests\BotNexus.Gateway.Tests\ --nologo` ✅ (48 passed)
