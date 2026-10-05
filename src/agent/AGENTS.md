# Agent Projects Rules

## Dependency boundary

Projects in `src/agent/` must not add dependencies outside this folder. The agent layer defines the core agent runtime and LLM provider abstractions. The gateway, extensions, and other layers depend on the agent layer, not the other way around.

The sole existing exception is `BotNexus.Agent.Providers.Core` →
`BotNexus.Domain.Wire`, a dependency-free leaf exposing the secret-redaction seam
used by provider error handling. Do not broaden this exception to other projects
or move host behavior into Wire. Architecture tests fence this exact edge and
separately require Wire to remain dependency-free.

**Allowed dependencies:**
- Other projects within `src/agent/` (e.g., providers reference `Agent.Providers.Core`)
- NuGet packages

**Prohibited dependencies:**
- `src/gateway/` — the agent layer does not know about the gateway
- `src/extensions/` — extensions depend on agents, not vice versa
- `src/common/` — if agent needs shared utilities, they belong in `Agent.Core`
- `src/domain/` — domain primitives are consumed via NuGet-style or must be pulled into Agent.Core if needed

## Project structure

| Project | Purpose |
|---------|---------|
| `BotNexus.Agent.Core` | Core agent runtime — loop, tools, extension points, types, configuration |
| `BotNexus.Agent.Providers.Core` | Shared provider abstractions — `LlmClient`, models, streaming |
| `BotNexus.Agent.Providers.OpenAI` | OpenAI Responses API provider |
| `BotNexus.Agent.Providers.Anthropic` | Anthropic Messages API provider |
| `BotNexus.Agent.Providers.Copilot` | GitHub Copilot provider |
| `BotNexus.Agent.Providers.OpenAICompat` | OpenAI-compatible endpoint provider (vLLM, SGLang, etc.) |
| `BotNexus.Agent.Providers.IntegrationMock` | Deterministic provider for integration, UI, and concurrency tests — returns scripted, key-based responses |

## Core organization and extension contracts

These rules are mandatory for changes to `BotNexus.Agent.Core`:

- Organize public extension contracts by responsibility under `ExtensionPoints/`:
	`Messages/`, `ProviderExecution/`, `ToolExecution/`, `ToolResults/`, and
	`RunCompletion/`. Keep orchestration in `Loop/`, configuration records in
	`Configuration/`, and general runtime data in `Types/`. Existing resilience
	contracts remain in `Loop/` until a dedicated refactor moves them.
- Put each independently consumed public extension contract (delegate, context,
	decision, result, or enum) in its own matching file. Keep its default
	implementation beside that family. Do not add a core `Hooks/` bucket or a
	`Delegates.cs` collection. Namespaces must match project-relative directories.
- Follow single responsibility and interface segregation: one contract answers
	one question or performs one operation. Do not combine unrelated capabilities
	in broad interfaces or create pass-through adapters to satisfy a layout rule.
- Keep a named delegate for a narrow, stateless, single-operation seam. Use a
	narrow interface when state, lifetime, or multiple cohesive operations require
	it. Do not turn existing delegates into interfaces or raw `Func<>`/`Action<>`
	during a layout refactor; do not add raw callbacks for named domain responsibilities.
- The loop owns execution, control-flow decisions, and mutable agent state.
	Policies return decisions, transformers return data, and observers report facts.
	Reuse an existing policy point when its question and contracts fit. Before adding
	contributors, follow the family-specific ordering, aggregation, failure, and
	evidence rules in [Extension-point composition](../../docs/development/agent-core-extension-points.md#define-composition-per-policy-family).
- Contexts and events express immutable intent, but records and `IReadOnlyList<>`
	are shallow: referenced objects can still be mutable. Do not claim deep
	immutability or mutate shared agent state through a context.
- Preserve public signatures, optional defaults, null semantics, cancellation,
	timeout/error behavior, and diagnostic strings in structural refactors. Never
	reorder public positional record parameters, including `AgentOptions`,
	`AgentLoopConfig`, contexts, decisions, and results.
- Group new declarations and configuration documentation by responsibility and
	execution order. Preserve existing positional order and stable invocation order;
	visual grouping must not change either contract.
- XML documentation must describe contractual null/default meaning, cancellation,
	errors, timeouts, and side effects. Keep historical issue narratives in issues
	or developer documentation, not as a substitute for the current contract.
- Behavior changes require failing tests first, then implementation and remote
	authoritative validation under the root rules. Mirror focused unit tests under
	the same family; keep cross-cutting security scenarios in `Security/`.

`AgentExtensionLayoutArchitectureTests` checks public extension declarations with
the C# syntax parser, including matching files, namespaces, responsibility folders,
and retired buckets. `AgentProjectBoundaryArchitectureTests` checks declared project
references, including conditional references and ancestor `Directory.Build.*`
inputs. References must use literal paths; explicit imports fail closed until their
build-input graph is covered. Conditional C# branches are checked across symbol
combinations (bounded to eight symbols per file; extend the inspector beyond that).
Extend their synthetic regression
cases when changing organization rules; do not replace them with formatting regexes
or freeze an exhaustive type inventory. `AgentToolUpdateCallback` remains an exact
runtime-data exception under `Types/`; it does not authorize other callbacks there.

## Adding a new provider

1. Create `src/agent/BotNexus.Agent.Providers.{Name}/`
2. Reference only `BotNexus.Agent.Providers.Core` (sibling project)
3. Implement `IApiProvider` (from `BotNexus.Agent.Providers.Core.Registry`) — declare an `Api` string that uniquely identifies the wire contract (e.g. `"openai-completions"`, `"anthropic-messages"`, `"integration-mock"`)
4. Register the provider and its models in `src/gateway/BotNexus.Gateway.Api/Program.cs`
5. Do not reference gateway, extension, or domain projects
