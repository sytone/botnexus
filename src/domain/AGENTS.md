# Domain Projects Rules

## Dependency boundary

Projects in `src/domain/` must have **zero** project dependencies. The domain layer is the foundation — every other layer depends on it, never the reverse.

**Allowed dependencies:**
- NuGet packages only (System.Text.Json, etc.)

**Prohibited dependencies:**
- Any `<ProjectReference>` to any other project in the solution
- The domain layer defines primitives, value objects, and shared models — it must remain self-contained

## Project structure

| Project | Purpose |
|---------|---------|
| `BotNexus.Domain` | Domain primitives (`AgentId`, `SessionId`, `ChannelKey`), serialization converters, gateway model records (`AgentStreamEvent`, `SessionStatus`, etc.) |

## Rules

- All types must be pure data — no service dependencies, no I/O
- JSON converters for domain primitives live alongside their types
- Enums sent via SignalR must have `[JsonConverter(typeof(JsonStringEnumConverter))]`
