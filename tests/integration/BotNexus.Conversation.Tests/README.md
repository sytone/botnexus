# BotNexus Conversation Tests

Live integration tests for the Conversation Model feature. Requires the dev gateway to be running.

## Running

Start the gateway first:
```
dotnet run --project src/gateway/BotNexus.Gateway.Api -- --urls http://0.0.0.0:5006
```

Then run all tests:
```
dotnet test tests/BotNexus.ConversationTests
```

Run only Wave 2 tests:
```
dotnet test tests/BotNexus.ConversationTests --filter "Phase=Wave2"
```

Run only Wave 3 tests:
```
dotnet test tests/BotNexus.ConversationTests --filter "Phase=Wave3"
```

Run only currently-passing tests (skip future-phase tests):
```
dotnet test tests/BotNexus.ConversationTests --filter "Phase!=Wave2&Phase!=Wave3"
```

If gateway is not running, all tests skip cleanly.

## Test Classes

| File | Description | Phase |
|------|-------------|-------|
| `ConversationRestApiTests.cs` | REST API for `/api/conversations` | Wave 3 |
| `ConversationSignalRTests.cs` | SignalR hub behavior + conversation routing | Current + Wave 2 |
| `ConversationBindingTests.cs` | Channel binding CRUD | Wave 2 |

## Phase Traits

- No `Phase` trait → tests existing gateway behavior (run always)
- `[Trait("Phase", "Wave2")]` → requires Wave 2 routing to be live
- `[Trait("Phase", "Wave3")]` → requires Wave 3 REST endpoints (Fry)
