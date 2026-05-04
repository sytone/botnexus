# Decision: Gateway runtime tool contribution contract

- **Date:** 2026-05-04
- **Owner:** Farnsworth
- **Status:** Proposed (implemented in branch)

## Context

`BotNexus.Gateway` had compile-time references to extension projects (`Skills`, `Mcp`, `McpInvoke`, `WebTools`) and directly instantiated extension tool types in `InProcessIsolationStrategy`, violating the documented dependency direction.

## Decision

Introduce a runtime contribution contract in Gateway contracts:

- `IAgentToolContributor`
- `AgentToolContributionContext`
- `AgentToolContribution`

`AssemblyLoadContextExtensionLoader` now discovers and registers `IAgentToolContributor` implementations from extension assemblies. `InProcessIsolationStrategy` invokes contributors per agent/session and appends returned tools/resources, with lifecycle cleanup handled by `InProcessAgentHandle`.

## Consequences

- Gateway no longer needs compile-time `ProjectReference` to those extension projects.
- Extensions self-register runtime tools while keeping dependency direction extension → gateway contracts.
- Special runtime behaviors (skills paths, MCP startup, web-search auth wiring) live with the extension code that owns them.
