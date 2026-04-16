---
status: deferred
depends-on: Phase 1.1 (BotNexus.Domain)
created: 2026-04-12
---

# Phase 4: World as a Domain Object

## Summary

Formalize the concept of a World - the environment in which agents exist. A World is a Gateway plus its accessible resources and boundaries. This phase makes the implicit explicit: what an agent can reach, where it runs, and what's beyond its borders.

## Why It Was Deferred

YAGNI - no consumer existed. The `ExecutionStrategy` smart enum (delivered in Wave 1) provides sufficient isolation modeling for now.

## When to Pick This Up

When one of these features is needed:
- Multi-gateway federation (Phase 6)
- Cross-world agent communication
- Agent sandboxing with explicit resource boundaries
- Admin UI showing "what does this agent have access to?"

## Detailed Design

### WorldDescriptor

The static description of a World - what it is and what it offers:

```csharp
public sealed record WorldDescriptor
{
    // Identity
    public required string WorldId { get; init; }        // Unique gateway identifier
    public required string DisplayName { get; init; }    // Human-readable name
    public string? Description { get; init; }

    // What lives here
    public IReadOnlyList<AgentId> HostedAgents { get; init; } = [];

    // What's available
    public IReadOnlyList<Location> Locations { get; init; } = [];
    public IReadOnlyList<ExecutionStrategy> AvailableStrategies { get; init; } = [];

    // Boundaries
    public IReadOnlyList<CrossWorldPermission> CrossWorldPermissions { get; init; } = [];
}
```

### Location

A resource accessible from this World:

```csharp
public sealed record Location
{
    public required string Name { get; init; }           // Human-readable identifier
    public required LocationType Type { get; init; }     // Smart enum: FileSystem, Api, RemoteNode, etc.
    public string? Path { get; init; }                   // URI, file path, or endpoint
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

public sealed class LocationType : SmartEnum<LocationType>
{
    public static readonly LocationType FileSystem = Register("filesystem");
    public static readonly LocationType Api = Register("api");
    public static readonly LocationType McpServer = Register("mcp-server");
    public static readonly LocationType RemoteNode = Register("remote-node");
    public static readonly LocationType Database = Register("database");
}
```

### CrossWorldPermission

Explicit grant to communicate with another World:

```csharp
public sealed record CrossWorldPermission
{
    public required string TargetWorldId { get; init; }
    public IReadOnlyList<AgentId>? AllowedAgents { get; init; }  // null = all agents
    public bool AllowInbound { get; init; } = true;
    public bool AllowOutbound { get; init; } = true;
}
```

### Configuration Mapping

The WorldDescriptor is primarily built from existing config:

| WorldDescriptor field | Current config source |
|----------------------|----------------------|
| WorldId | New - generated or configured gateway ID |
| HostedAgents | `PlatformConfig.Agents.Keys` |
| Locations (FileSystem) | Agent workspace paths, `AgentsDirectory` |
| Locations (Api) | MCP server configs from extension settings |
| AvailableStrategies | Registered `IIsolationStrategy` instances |
| CrossWorldPermissions | New config section (not yet needed) |

### Minimal Viable World

To satisfy the YAGNI concern, the first consumer should be simple. Candidates:
- **Gateway status API**: `GET /api/gateway/world` returns the WorldDescriptor - useful for admin/debugging
- **Agent context injection**: Include a summary of the agent's World in its system prompt so it knows what it can reach
- **Session metadata**: Store the WorldId on sessions so cross-world sessions are identifiable

### Where It Lives

`BotNexus.Domain/World/` - pure domain types, no dependencies.
`BotNexus.Gateway` - `WorldBuilder` that constructs a `WorldDescriptor` from current config and runtime state.

## Migration Plan

1. Add types to `BotNexus.Domain/World/`
2. Add `WorldBuilder` to Gateway that builds from existing config (read-only, no new config needed)
3. Expose via Gateway status API
4. Optional: inject World summary into agent system prompts

No breaking changes. Purely additive.

## Test Requirements

- WorldBuilder correctly aggregates agents, locations, and strategies from config
- WorldDescriptor serialization/deserialization
- Gateway status API returns valid WorldDescriptor

## Acceptance Criteria

- [ ] `WorldDescriptor`, `Location`, and `CrossWorldPermission` types exist in `BotNexus.Domain`
- [ ] Gateway can build a WorldDescriptor from its current configuration
- [ ] At least one consumer uses the WorldDescriptor (status API or system prompt)
