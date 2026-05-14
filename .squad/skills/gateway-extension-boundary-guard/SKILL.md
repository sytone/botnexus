---
name: "gateway-extension-boundary-guard"
description: "Prevent gateway projects from taking compile-time dependencies on extension assemblies."
domain: "testing"
confidence: "high"
source: "earned"
---

## Context
Use this when a gateway/runtime layer must remain extension-agnostic and regressions can sneak in through csproj references.

## Patterns
- Add an architecture test in gateway tests that scans `src\gateway\**\*.csproj`.
- Fail on any `ProjectReference` path containing `\extensions\`.
- Fail on any `ProjectReference`, `PackageReference`, or `Reference` name starting with `BotNexus.Extensions.`.
- Resolve repository root by walking up from `AppContext.BaseDirectory` until `BotNexus.slnx` exists.
- Keep runtime behavior by introducing a contract seam (e.g., `IAgentChangeNotifier`) that extensions implement.

## Examples
- Guard test: `tests\gateway\BotNexus.Gateway.Tests\Architecture\GatewayProjectDependencyBoundaryTests.cs`
- Contract seam: `src\gateway\BotNexus.Gateway.Contracts\Agents\IAgentChangeNotifier.cs`
- Transport implementation: `src\extensions\BotNexus.Extensions.Channels.SignalR\SignalRAgentChangeNotifier.cs`

## Anti-Patterns
- Referencing extension hub/client types directly from gateway API/controller code.
- Enforcing boundary manually in PR review only (without an executable test gate).
