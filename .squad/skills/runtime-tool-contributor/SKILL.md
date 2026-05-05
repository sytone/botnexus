# Runtime Tool Contributor Pattern

## When to use

Use this pattern when Gateway needs per-agent/session extension tools but must not take compile-time dependencies on extension projects.

## Pattern

1. Define a contract in Gateway contracts:
   - `IAgentToolContributor`
   - `AgentToolContributionContext`
   - `AgentToolContribution`
2. Add the contract to extension loader discoverable service contracts.
3. In extensions, implement `IAgentToolContributor` and build tools from `AgentDescriptor.ExtensionConfig`.
4. In `InProcessIsolationStrategy`, resolve contributors from DI and merge their tools into the runtime tool list.
5. Pass any contributor-provided disposables to the agent handle for session-scoped cleanup.

## Key files

- `src/gateway/BotNexus.Gateway.Contracts/Agents/IAgentToolContributor.cs`
- `src/gateway/BotNexus.Gateway/Extensions/AssemblyLoadContextExtensionLoader.cs`
- `src/gateway/BotNexus.Gateway/Isolation/InProcessIsolationStrategy.cs`
- `src/extensions/*/*ToolContributor.cs`

## Notes

- Keep extension-specific config parsing and provider-specific wiring inside extension projects.
- Gateway remains composition root and runtime orchestrator, not an extension implementation host.
