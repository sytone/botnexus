# BotNexus Blazor Client — Agent Notes

## Architecture

**MainLayout.razor** — structural shell for all pages
- Banner (logo + title)
- Dismissible announcements bar (UI shell ready; API wire-up pending)
- Two-column body: sidebar + main canvas
- Sidebar owns: connection status, nav links, agent dropdown, session list, restart button
- Agent dropdown + session list persist across page navigation
- Subscribes to `Manager.OnStateChanged` for re-render on state updates

**Home.razor** — chat page (renders in MainLayout's `@Body`)
- Chat panels only — no agent list, no nav
- Calls `Manager.InitializeAsync` to connect hub (once)
- Check `Manager.Hub.IsConnected` before init to avoid double-connect if MainLayout already connected (future-proofing)

**Configuration.razor** — platform config page (renders in MainLayout's `@Body`)
- Independent of agent/session state

## State Management

**AgentSessionManager** — injected into MainLayout and pages
- `Sessions` — IReadOnlyDictionary<string, AgentSessionState>
- `ActiveAgentId` — string? (nullable)
- `OnStateChanged` — event fired on any state mutation (messages, sub-agents, session ID change)
- All components that subscribe MUST unsubscribe in `Dispose()` to avoid leaks

**OnStateChanged pattern:**
```csharp
protected override void OnInitialized()
{
    Manager.OnStateChanged += HandleStateChanged;
}

private void HandleStateChanged() => InvokeAsync(StateHasChanged);

public void Dispose()
{
    Manager.OnStateChanged -= HandleStateChanged;
}
```

## Layout Decisions

**Why agent list is in MainLayout, not Home:**
- Agent selection should persist across page navigation (Chat vs Configuration)
- Dropdown + session list in sidebar = global state visible everywhere
- Home.razor only needs to render chat panels for the selected agent

**"Expired" session filter:**
- Sub-agents with status `Killed` or `Failed` are hidden from the session list
- Only `Running` and `Completed` sub-agents are shown
- Filter: `.Where(s => s.Status is "Running" or "Completed")`

## CSS

**Single stylesheet:** `wwwroot/css/app.css` (no scoped CSS)
- Design tokens in `:root` — `--bg-primary`, `--accent`, `--text-muted`, etc.
- `.app-shell` — root layout container (full viewport height)
- `.app-banner` — top banner (gradient background)
- `.announcement-bar` — hidden unless `_announcements.Count > 0`
- `.app-body` — two-column flex (sidebar + main-canvas)
- `.main-sidebar` — fixed 240px width, flex column
- `.main-canvas` — flex:1, renders `@Body`

**Agent dropdown CSS:**
- `.agent-dropdown-container` — flex-shrink:0 (doesn't collapse)
- `.agent-session-list` — flex:1, scrollable (shows sub-agents below dropdown)

## Announcements (Not Yet Wired)

**UI shell ready:**
- `_announcements` list in MainLayout
- `Announcement(Id, Text, Type)` record with dismissal logic
- Dismissible via `announcement-dismiss` button

**TODO (future):**
- Fetch from `/world` API on connect
- Parse `announcements[]` field if present
- Populate `_announcements` and re-render

## Hub Connection

**InitializeAsync called in Home.razor:**
- Check `Manager.Hub.IsConnected` before calling to avoid double-connect
- Failures are logged to console.error (no modal, graceful degradation)

**GatewayHubConnection.IsConnected:**
- Property on line 69 of `GatewayHubConnection.cs`
- Returns `_connection?.State == HubConnectionState.Connected`

## Gotchas

**Agent dropdown `@onchange`:**
- Uses `ChangeEventArgs` (not `@bind` with async)
- Empty option value is `""`, not `null`

**Session ID truncation in session list:**
- `sub.SubAgentId[..Math.Min(8, sub.SubAgentId.Length)]` — safe truncation to avoid index errors
- Shortest sub-agent IDs may be < 8 chars

**Restart button:**
- POSTs to `/api/gateway/shutdown` (expected to drop connection)
- Catch is empty — connection drop is normal behavior
- `_restarting` flag prevents double-click

**Empty state message:**
- "Select an agent from the sidebar to start chatting." — wording updated to reflect new layout
- Old version said "Select an agent to start chatting" (ambiguous about where to select)

## File Ownership

- **MainLayout.razor** — layout shell, agent dropdown, restart button
- **Home.razor** — chat panels only (no agent list, no sidebar controls)
- **Configuration.razor** — platform config (independent)
- **AgentSessionManager.cs** — session state, hub events, active agent selection
- **GatewayHubConnection.cs** — SignalR hub wrapper, connection lifecycle
- **app.css** — all styles (no scoped CSS)
