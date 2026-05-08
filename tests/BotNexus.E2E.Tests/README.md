# BotNexus E2E Tests

Playwright-based end-to-end tests for the BotNexus Blazor portal and dev gateway.

## Prerequisites

- Dev gateway running at `http://localhost:5006`
- `probe` agent configured with a fast model (e.g. `gpt-4.1-nano`)

## Run

```bash
# Start gateway first (example — adjust path to your config)
dotnet run --project src/gateway/BotNexus.Cli -- gateway start

# Run E2E tests
dotnet test tests/BotNexus.E2ETests
```

All tests skip cleanly if the gateway is not running — no credentials or mocks required.

## Tests

| Class | Tests |
|---|---|
| `PortalLoadTests` | Hard refresh resolves, agent in dropdown, default conversation visible |
| `MessageFlowTests` | Send message gets response, streaming indicator lifecycle |
| `ConversationE2ETests` | New conversation adds to list, switch conversations shows different titles |

## Selectors

The tests use CSS class selectors aligned with the Blazor portal markup. If the portal markup changes these selectors may need updating:

| Selector | Element |
|---|---|
| `.main-sidebar` | Outer sidebar container |
| `.agent-dropdown-select` | Agent selection `<select>` |
| `.conversation-list-item` | Each conversation row |
| `.conversation-new-btn` | New Conversation button |
| `.conversation-title` | Current conversation title |
| `.chat-input textarea` / `.chat-input input` | Message composer |
| `.message-assistant` | Assistant message bubbles |
