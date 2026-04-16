---
status: deferred
depends-on: Waves 1-4 complete (all Domain types finalized)
created: 2026-04-12
---

# Phase 7.1: Split Gateway.Abstractions

## Summary

Split `BotNexus.Gateway.Abstractions` into two focused projects: `BotNexus.Domain` (pure domain types) and `BotNexus.Gateway.Contracts` (gateway-specific service interfaces). Then remove `Gateway.Abstractions` entirely.

## Why Deferred

High risk - 13+ downstream projects reference `Gateway.Abstractions`. All Domain types must be stable before splitting assemblies. Requires `[TypeForwardedTo]` for incremental migration.

## Current State

`BotNexus.Gateway.Abstractions` currently contains:

### Models/ (domain types - should move to BotNexus.Domain)
- `AgentDescriptor` - agent configuration
- `AgentInstance` - runtime agent state
- `AgentExecution` - execution metadata
- `AgentHealthResponse` - health check response
- `GatewayActivity` - activity events
- `GatewaySession` / `SessionEntry` / `GatewaySessionStreamEvent` - session types
- `SessionReplayBuffer` - replay buffer
- `SessionStatus` - lifecycle enum (now on BotNexus.Domain already)
- `SessionHistoryResponse` - API response shape
- `SubAgentInfo` / `SubAgentSpawnRequest` - sub-agent types
- `Messages` (InboundMessage, OutboundMessage) - message types
- `MemoryAgentConfig` - memory configuration
- `IModelFilter` - model filtering

### Agents/ (gateway contracts - should move to BotNexus.Gateway.Contracts)
- `IAgentRegistry` - agent registration
- `IAgentSupervisor` - instance lifecycle
- `IAgentHandle` / `IAgentHandleInspector` - agent interaction
- `IAgentCommunicator` - cross-session messaging
- `IAgentConfigurationSource` / `IAgentConfigurationWriter` - config management
- `IAgentToolFactory` - tool creation
- `IAgentWorkspaceManager` / `AgentWorkspace` - workspace management
- `IContextBuilder` - system prompt context
- `ISubAgentManager` - sub-agent orchestration
- `IHealthCheckable` - health checks
- `AgentConcurrencyLimitExceededException` - exception type

### Channels/ (gateway contracts)
- `IChannelAdapter` / `IChannelDispatcher` / `IStreamEventChannelAdapter`
- `IChannelManager`

### Sessions/ (gateway contracts)
- `ISessionStore` - session persistence
- `ISessionCompactor` / `CompactionOptions` / `CompactionResult` - compaction
- `ISessionLifecycleEvents` / `SessionLifecycleEvent` - lifecycle events
- `ISessionWarmupService` / `SessionSummary` - warmup
- `SessionCleanupOptions` - cleanup config (wait, this is in Gateway not Abstractions)

### Other (gateway contracts)
- `IExtensionLoader` / `ExtensionModels` - extension system
- `IMessageRouter` / routing
- `IHookDispatcher` / `IHookHandler` / `HookEvents` - hook system
- `IGatewayAuthHandler` / `ToolPolicy` - security
- `IActivityBroadcaster` - activity broadcasting
- `IConfigPathResolver` - config paths
- `IIsolationStrategy` - isolation

## Target Structure

### BotNexus.Domain (already exists - extend)

Move these types in:
- `AgentDescriptor` (already partially uses Domain primitives)
- `AgentInstance`
- `SubAgentInfo`, `SubAgentSpawnRequest`
- `InboundMessage`, `OutboundMessage`
- `MemoryAgentConfig`
- `AgentExecution`
- Session types that aren't infrastructure (SessionEntry as domain, SessionHistoryResponse as API concern)

### BotNexus.Gateway.Contracts (new project)

All gateway-specific service interfaces:
- `IAgentRegistry`, `IAgentSupervisor`, `IAgentHandle`, etc.
- `IChannelAdapter`, `IChannelDispatcher`, etc.
- `ISessionStore`, `ISessionCompactor`, etc.
- `IMessageRouter`, `IHookDispatcher`, etc.
- `IIsolationStrategy`
- `IExtensionLoader`

### What stays nowhere (delete)

- `BotNexus.Gateway.Abstractions` - deleted once migration complete

## Migration Strategy: TypeForwardedTo

Use `[TypeForwardedTo]` attributes to make this non-breaking:

**Step 1**: Move types to their new homes (Domain or Contracts)

**Step 2**: In `Gateway.Abstractions`, add forwarding:
```csharp
// BotNexus.Gateway.Abstractions/TypeForwards.cs
[assembly: TypeForwardedTo(typeof(BotNexus.Domain.Agent.AgentDescriptor))]
[assembly: TypeForwardedTo(typeof(BotNexus.Domain.Session.SessionEntry))]
// ... etc
```

**Step 3**: `Gateway.Abstractions` now references Domain and Contracts, and forwards all types. Every downstream project still compiles without changes.

**Step 4**: Update downstream projects one at a time to reference Domain/Contracts directly instead of Abstractions.

**Step 5**: Once no project references Abstractions, delete it.

### Downstream Projects (13+)

Migration order (least dependencies first):
1. `BotNexus.Tools` - may not reference Abstractions
2. `BotNexus.Memory` - may only need Domain types
3. `BotNexus.Cron` - needs some contracts
4. `BotNexus.Channels.Core` - needs IChannelAdapter
5. `BotNexus.Channels.Telegram` - needs IChannelAdapter
6. `BotNexus.Channels.Tui` - needs IChannelAdapter
7. `BotNexus.Extensions.*` - varies per extension
8. `BotNexus.Gateway.Sessions` - needs ISessionStore, GatewaySession
9. `BotNexus.Gateway` - the big one, needs everything
10. `BotNexus.Gateway.Api` - needs controllers + hub types
11. `BotNexus.WebUI` - API models
12. `BotNexus.Cli` - config types
13. Test projects

## Proof of Concept

Before starting the full migration, prove the TypeForwardedTo approach works:
1. Pick ONE type (e.g., `AgentDescriptor`)
2. Move it to Domain
3. Add the forwarding attribute in Abstractions
4. Verify all downstream projects still compile
5. Verify serialization still works (JSON, SQLite)
6. Verify runtime type identity is preserved

## Test Requirements

- All existing tests pass at every migration step
- Type forwarding preserves serialization compatibility
- Runtime type checks (is, as, pattern matching) still work
- No binary-breaking changes for extension assemblies

## Risks

1. **Serialization**: `System.Text.Json` uses full type names. TypeForwardedTo should handle this, but verify.
2. **Reflection**: Any code using `typeof(AgentDescriptor).Assembly` will break. Search for assembly-based reflection.
3. **Extension compatibility**: Extensions compiled against Abstractions must still load. TypeForwardedTo handles this if the old assembly still exists during transition.
4. **Merge conflicts**: This touches many projects. Coordinate with other work streams.

## Acceptance Criteria

- [ ] `BotNexus.Domain` contains all domain types
- [ ] `BotNexus.Gateway.Contracts` contains all service interfaces
- [ ] `BotNexus.Gateway.Abstractions` is deleted
- [ ] All 13+ downstream projects reference Domain/Contracts directly
- [ ] All tests pass
- [ ] Extensions compiled against old Abstractions still load (verified)
