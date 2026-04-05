### 2026-04-05: Design Review — Gateway P1 Remediation + WebUI Sprint

**By:** Leela (Lead)
**Scope:** P1 fixes (Bender), WebUI (Fry), Channel stubs (Farnsworth), Test suites (Hermes)

---

## Design Review: Gateway P1 Sprint

### Overall Grade: A-

All six P1 issues from the prior design review are correctly remediated. Clean test coverage added across router, session stores, and WebSocket handler. Channel stubs honor their contracts. WebUI is properly decoupled. One P0 finding (WebSocket streaming history loss) prevents the A grade.

### SOLID Compliance

| Principle | Verdict | Notes |
|-----------|---------|-------|
| **SRP** | ✅ Pass | ChannelManager is now purely a read-only registry. GatewayHost owns lifecycle. DefaultMessageRouter owns routing. GatewayOptions carries config. Clean separation. |
| **OCP** | ✅ Pass | All extension points preserved: IChannelAdapter, ISessionStore, IIsolationStrategy, IMessageRouter, IGatewayAuthHandler. New channels/stores/strategies can be added without touching existing code. |
| **LSP** | ✅ Pass | Channel stubs honor the IChannelAdapter contract. TUI correctly claims `SupportsStreaming = true`, Telegram correctly claims `false`. Both implement all members. |
| **DIP** | ⚠️ Soft violation | ChannelManager is a concrete type in GatewayHost's constructor (no interface). Pragmatic for a single implementation but worth noting. All other dependencies flow through interfaces. |
| **ISP** | ✅ Pass | Interfaces remain focused: IChannelAdapter (6 members), ISessionStore (5 methods), IMessageRouter (1 method). No fat interfaces. |

---

### Findings

#### P0 (Must Fix Before Merge)

**1. WebSocket handler has the same streaming history loss bug that P1-1 just fixed**
- **File:** `src/gateway/BotNexus.Gateway.Api/WebSocket/GatewayWebSocketHandler.cs`, lines 146–178
- **What:** `HandleUserMessageAsync` records the user message to session history (line 149) and streams events to the WebSocket client (lines 155–169), but **never accumulates assistant response content or tool events into session history**. The session is saved (line 178) with only the user message.
- **Impact:** Session history from WebSocket interactions is incomplete. Session replay won't show assistant responses. Follow-up messages in the same session lose context.
- **Root cause:** The WebSocket handler bypasses `GatewayHost.DispatchAsync` — it calls `_supervisor.GetOrCreateAsync` and `handle.StreamAsync` directly. The P1-1 fix in `GatewayHost` doesn't protect this code path.
- **Fix:** Apply the same `streamedContent`/`streamedHistory` accumulation pattern from `GatewayHost.DispatchAsync` (lines 141–180) into `GatewayWebSocketHandler.HandleUserMessageAsync`. Consider extracting a shared `StreamToHistoryAsync` helper.
- **Assignee recommendation:** Bender (owns the P1 fix pattern)

#### P1 (Fix This Sprint)

**1. Channel stubs bypass ChannelAdapterBase**
- **Files:** `TuiChannelAdapter.cs`, `TelegramChannelAdapter.cs`
- **What:** Both implement `IChannelAdapter` directly rather than extending `ChannelAdapterBase` from `Channels.Core`. They miss the base class's allow-list enforcement and standardized lifecycle logging.
- **Why it matters:** When these stubs are promoted to full adapters, they'll need the base class features. Starting from the base class now prevents a rewrite.
- **Recommended fix:** Extend `ChannelAdapterBase` and override `OnStartAsync`/`OnStopAsync`/`SendAsync`/`SendStreamDeltaAsync`. Acceptable to defer if stubs remain purely for testing.

**2. TelegramOptions doesn't use the Options pattern**
- **File:** `TelegramServiceCollectionExtensions.cs`, lines 25–28
- **What:** Creates `new TelegramOptions()`, invokes the callback, and registers the instance directly with `AddSingleton(options)`. Does not participate in `IOptions<T>` or `IOptionsMonitor<T>`.
- **Why it matters:** Inconsistent with the `GatewayOptions` pattern just fixed in this sprint (P1-2). When the Telegram adapter goes live, callers will expect `IOptions<TelegramOptions>` injection. The fix is trivial: use `services.AddOptions<TelegramOptions>()` + `services.Configure(configure)`.

**3. ChannelManager should have an interface (DIP)**
- **File:** `GatewayHost.cs`, line 39; `GatewayServiceCollectionExtensions.cs`, line 46
- **What:** `ChannelManager` is injected as a concrete type. No `IChannelManager` or `IChannelRegistry` interface exists.
- **Why it matters:** Consumers cannot mock or swap the channel registry in tests without constructing a real `ChannelManager`. Minor DIP violation.
- **Recommended fix:** Extract `IChannelRegistry` with `Adapters` and `Get()`. Low priority since there's only one implementation.

#### P2 (Backlog)

**1. WebUI CDN dependencies (offline risk)**
- **File:** `index.html`, lines 122–123
- `marked.min.js` and `dompurify` loaded from `cdn.jsdelivr.net`. No offline fallback. Air-gapped or restricted-network deployments will fail to render markdown.
- **Recommendation:** Bundle both libraries in `wwwroot/lib/` and reference locally.

**2. GatewayHost redundant null check**
- **File:** `GatewayHost.cs`, line 61
- `_sessions` is constructor-injected and non-nullable. The `if (_sessions is null)` guard is dead code.
- **Recommendation:** Remove the null check. If DI fails, the constructor throws before `ExecuteAsync` is reached.

**3. Channel stub thread safety**
- **Files:** `TuiChannelAdapter.cs`, `TelegramChannelAdapter.cs`
- `_isRunning` and `_dispatcher` are read/written without synchronization. Acceptable for stubs but needs `volatile` or `Interlocked` when promoted to production adapters.

**4. WebUI MSBuild relative paths**
- **File:** `BotNexus.Gateway.Api.csproj`, lines 21–33
- CopyWebUI target uses `$(MSBuildProjectDirectory)\..\..\BotNexus.WebUI\wwwroot\**`. Works but fragile if project layout changes. Consider using a project reference output path or `$(SolutionDir)`.

---

### What Worked Well

1. **P1-1 streaming history fix is comprehensive.** GatewayHost now captures ToolStart, ToolEnd, Error events, and accumulates ContentDelta. The `streamedHistory` list plus final assistant entry pattern is clean and correct.

2. **P1-2 IOptionsMonitor refactor is textbook.** `SetDefaultAgent` uses `PostConfigure<GatewayOptions>` on `IServiceCollection`. Router reads from `IOptionsMonitor<GatewayOptions>.CurrentValue`. Supports hot-reload. No more concrete method on the router.

3. **P1-3 ChannelManager consolidation is clean.** Pure read-only registry with `Adapters` and `Get()`. Zero lifecycle logic. GatewayHost is the single authority for adapter start/stop.

4. **P1-4 default session store registration is correct.** `TryAddSingleton<ISessionStore, InMemorySessionStore>()` allows consumers to override before or after `AddBotNexusGateway()`.

5. **Channel stubs are well-documented.** Every public member has XML docs. Phase 2 limitations are clearly marked in `<remarks>`. Stub vs. full implementation boundary is explicit.

6. **WebUI is properly decoupled.** No C# code in the WebUI project. Connects via public `/api` and `/ws` endpoints. No references to Gateway internals. DOMPurify sanitization prevents XSS.

7. **Test quality is strong.** Router tests cover all three priority tiers plus edge cases. FileSessionStore tests cover CRUD, concurrency, special characters, and cross-instance reload. WebSocket tests validate HTTP/WS handshake edge cases.

---

### Recommendations

1. **Extract a `StreamToHistoryAsync` helper** — The streaming-to-session-history pattern is now needed in two places (GatewayHost, WebSocketHandler). Extract it into a shared method to avoid future divergence.

2. **Standardize Options pattern across all channel adapters** — GatewayOptions uses `IOptionsMonitor`, TelegramOptions uses raw singleton. Establish a convention before more channels are added.

3. **Consider an integration test for WebSocket session persistence** — The P0 bug would have been caught by a test that sends a message over WebSocket and then verifies the session history contains the assistant response.

4. **Track channel stub → production promotion plan** — The stubs are well-structured but need a clear path to ChannelAdapterBase extension, proper async lifecycle, and real protocol integration.

---

### Prior P1 Disposition

| Prior Finding | Status | Verification |
|--------------|--------|-------------|
| P1-1: Streaming history loss | ✅ Fixed in GatewayHost | Lines 141–180: tool events + assistant content captured |
| P1-2: SetDefaultAgent DI smell | ✅ Fixed | Options pattern with IOptionsMonitor |
| P1-3: ChannelManager lifecycle duplication | ✅ Fixed | ChannelManager is read-only registry |
| P1-4: No ISessionStore in default DI | ✅ Fixed | TryAddSingleton<ISessionStore, InMemorySessionStore> |
| P1-5: Test file naming mismatch | ✅ Fixed | Test files now match their subjects |
| Test coverage gaps | ✅ Addressed | FileSessionStore, DefaultMessageRouter, WebSocketHandler all have tests |
