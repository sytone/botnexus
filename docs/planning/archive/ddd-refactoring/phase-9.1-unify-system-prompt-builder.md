---
status: deferred
depends-on: Wave 2 (MessageRole, SessionType smart enums)
created: 2026-04-12
---

# Phase 9.1: Unify SystemPromptBuilder

## Summary

Decompose the Gateway's 572-line `SystemPromptBuilder.Build()` into a pipeline of testable section builders. Extract shared prompt primitives into a new `BotNexus.Prompts` project. Enable both Gateway and CodingAgent to compose prompts from shared sections without depending on each other.

## Why Deferred

Large refactor independent of the core DDD alignment. Needs MessageRole smart enum (delivered in Wave 2) and snapshot tests of current output BEFORE any changes.

## Current State

### Gateway SystemPromptBuilder (572 lines)
- `BotNexus.Gateway/Agents/SystemPromptBuilder.cs`
- Static class with one massive `Build()` method
- 15+ internal helper methods (buildToolsSection, buildSkillsSection, buildMessagingSection, etc.)
- Takes `SystemPromptParams` record with 20+ fields
- Outputs a single concatenated string
- Context file ordering, cache boundaries, channel-specific logic all inline

### CodingAgent SystemPromptBuilder (283 lines)
- `BotNexus.CodingAgent/SystemPromptBuilder.cs`
- Separate static class with its own `Build()` method
- Takes `SystemPromptContext` record
- Different parameter types: `PromptContextFile` vs `ContextFile`, `ToolPromptContribution` (unique)
- Simpler - no channels, no skills, no heartbeat

### Shared Concepts
Both builders handle:
- Context file loading and ordering
- Tool name injection
- Environment/runtime info
- Custom/override prompts
- System prompt concatenation

## Target State

### BotNexus.Prompts (new project)

A shared library for prompt composition. Neither Gateway nor CodingAgent - sits alongside BotNexus.Domain as a shared foundation. AgentCore stays lean and does NOT absorb this.

```
BotNexus.Prompts/
    IPromptSection.cs           - interface for a prompt section builder
    PromptPipeline.cs           - orchestrates section builders into a final prompt
    PromptContext.cs             - shared context bag that sections read from
    Sections/
        ToolSection.cs           - tool availability and guidelines
        SafetySection.cs         - safety rules
        ExecutionBiasSection.cs  - action-oriented behavior rules
        ContextFileSection.cs    - workspace context file injection
        RuntimeSection.cs        - runtime info line
    ContextFileOrdering.cs       - shared ordering logic for workspace files
```

### Section Builder Interface

```csharp
public interface IPromptSection
{
    /// Order in the pipeline (lower = earlier in prompt)
    int Order { get; }

    /// Whether this section should be included given the current context
    bool ShouldInclude(PromptContext context);

    /// Build the section's lines
    IReadOnlyList<string> Build(PromptContext context);
}
```

### PromptPipeline

```csharp
public sealed class PromptPipeline
{
    private readonly List<IPromptSection> _sections = [];

    public PromptPipeline Add(IPromptSection section) { _sections.Add(section); return this; }

    public string Build(PromptContext context)
    {
        var lines = _sections
            .Where(s => s.ShouldInclude(context))
            .OrderBy(s => s.Order)
            .SelectMany(s => s.Build(context))
            .ToList();
        return string.Join("\n", lines);
    }
}
```

### Extension-Contributed Sections

Extensions need to inject content into the system prompt (skill descriptions, tool guidelines, MCP server context, etc.). The pipeline handles this through **section registration at startup** - extensions don't modify core sections, they add their own.

**Registration**: Extensions implement `IPromptSection` and register it via DI. The pipeline discovers all registered sections automatically:

```csharp
// In an extension's service registration
services.AddSingleton<IPromptSection, SkillsPromptSection>();
services.AddSingleton<IPromptSection, McpToolsPromptSection>();
```

The pipeline collects all `IPromptSection` implementations from the DI container:

```csharp
public sealed class PromptPipeline
{
    private readonly IEnumerable<IPromptSection> _sections;

    public PromptPipeline(IEnumerable<IPromptSection> sections)
    {
        _sections = sections;
    }

    public string Build(PromptContext context)
    {
        var lines = _sections
            .Where(s => s.ShouldInclude(context))
            .OrderBy(s => s.Order)
            .SelectMany(s => s.Build(context))
            .ToList();
        return string.Join("\n", lines);
    }
}
```

**Ordering**: Each section declares its `Order` value. Core sections use well-known ranges:
- 0-99: Preamble (identity, role description)
- 100-199: Tooling and capabilities
- 200-299: Behavioral rules (safety, execution bias, tool call style)
- 300-399: Skills and domain knowledge (extension territory)
- 400-499: Context injection (workspace files, docs)
- 500-599: Channel-specific (messaging, reply tags, voice)
- 600-699: Dynamic context (heartbeat, runtime)
- 900+: Cache boundary and trailing sections

Extensions typically register in the 300-399 range but can use any range to position themselves relative to core sections.

**Data flow**: Extensions may need to pass data to their section builders. Two patterns:

1. **PromptContext.Extensions bag** - for simple key-value data:
```csharp
// Extension populates context before pipeline runs
context.Extensions["skills-prompt"] = skillsMarkdown;

// Section reads it
public IReadOnlyList<string> Build(PromptContext context)
{
    var skills = context.Extensions.GetValueOrDefault("skills-prompt") as string;
    if (string.IsNullOrEmpty(skills)) return [];
    return ["## Available Skills", skills, ""];
}
```

2. **Typed context via DI** - for complex data, the section's constructor receives whatever services it needs:
```csharp
public class McpToolsPromptSection(IMcpServerRegistry registry) : IPromptSection
{
    public IReadOnlyList<string> Build(PromptContext context)
    {
        var servers = registry.GetActiveServers();
        // Build MCP tool descriptions from live server state
    }
}
```

**Conditional inclusion**: `ShouldInclude(context)` lets extensions skip their section when not relevant (e.g., skills section only included when the agent has skills configured, MCP section only when MCP servers are active).

This model means:
- Core prompt sections ship in `BotNexus.Prompts`
- Gateway-specific sections ship in `BotNexus.Gateway`
- Extension-contributed sections ship in their own extension assembly
- Nobody modifies anyone else's code to add prompt content
- The pipeline composes everything at runtime based on what's registered

### PromptContext

Shared bag of data that sections read from:

```csharp
public sealed record PromptContext
{
    public required string WorkspaceDir { get; init; }
    public IReadOnlyList<ContextFile> ContextFiles { get; init; } = [];
    public IReadOnlySet<ToolName> AvailableTools { get; init; } = new HashSet<ToolName>();
    public bool IsMinimal { get; init; }
    public string? Channel { get; init; }
    public IDictionary<string, object?> Extensions { get; init; } = new Dictionary<string, object?>();
    // Extensions bag lets Gateway and CodingAgent add their own context without BotNexus.Prompts knowing about it
}
```

### Gateway Composition

Gateway registers its sections via DI. The pipeline collects all registered `IPromptSection` implementations automatically - core, gateway-specific, and extension-contributed:

```csharp
// In Gateway DI setup - core sections
services.AddSingleton<IPromptSection, ToolSection>();          // from BotNexus.Prompts
services.AddSingleton<IPromptSection, SafetySection>();        // from BotNexus.Prompts
services.AddSingleton<IPromptSection, ExecutionBiasSection>(); // from BotNexus.Prompts
services.AddSingleton<IPromptSection, ContextFileSection>();   // from BotNexus.Prompts
services.AddSingleton<IPromptSection, RuntimeSection>();       // from BotNexus.Prompts

// Gateway-specific sections
services.AddSingleton<IPromptSection, SkillsSection>();
services.AddSingleton<IPromptSection, MessagingSection>();
services.AddSingleton<IPromptSection, ReplyTagsSection>();
services.AddSingleton<IPromptSection, HeartbeatSection>();
services.AddSingleton<IPromptSection, SilentReplySection>();
services.AddSingleton<IPromptSection, CacheBoundarySection>();
services.AddSingleton<IPromptSection, GatewayCliSection>();

// Extensions register their own sections during extension loading
// e.g., BotNexus.Extensions.Skills adds SkillsPromptSection
// e.g., BotNexus.Extensions.Mcp adds McpToolsPromptSection

// Pipeline is injected via DI with all registered sections
services.AddSingleton<PromptPipeline>();
```

At runtime, `PromptPipeline` receives all sections via constructor injection, orders them, and builds the prompt. No manual wiring needed.

### CodingAgent Composition

CodingAgent uses the same pattern but with fewer sections and no extension system:

```csharp
// CodingAgent registers just the sections it needs
services.AddSingleton<IPromptSection, ToolSection>();          // from BotNexus.Prompts
services.AddSingleton<IPromptSection, SafetySection>();        // from BotNexus.Prompts
services.AddSingleton<IPromptSection, ContextFileSection>();   // from BotNexus.Prompts
services.AddSingleton<IPromptSection, RuntimeSection>();       // from BotNexus.Prompts
services.AddSingleton<IPromptSection, EnvironmentSection>();   // CodingAgent-specific
services.AddSingleton<IPromptSection, ToolGuidelinesSection>();// CodingAgent-specific
services.AddSingleton<PromptPipeline>();
```

### What Moves Where

| Current (Gateway) | Target | Project |
|-------------------|--------|---------|
| `buildToolsSection` logic | `ToolSection` | BotNexus.Prompts |
| Safety rules | `SafetySection` | BotNexus.Prompts |
| Execution bias rules | `ExecutionBiasSection` | BotNexus.Prompts |
| Context file ordering/injection | `ContextFileSection` + `ContextFileOrdering` | BotNexus.Prompts |
| Runtime line | `RuntimeSection` | BotNexus.Prompts |
| Skills section | `SkillsSection` | BotNexus.Gateway |
| Messaging section | `MessagingSection` | BotNexus.Gateway |
| Reply tags | `ReplyTagsSection` | BotNexus.Gateway |
| Voice/TTS | `VoiceSection` | BotNexus.Gateway |
| Heartbeat | `HeartbeatSection` | BotNexus.Gateway |
| Silent reply | `SilentReplySection` | BotNexus.Gateway |
| Cache boundary | `CacheBoundarySection` | BotNexus.Gateway |
| CLI reference | `GatewayCliSection` | BotNexus.Gateway |
| Approval guidance | Part of `ToolSection` or `ApprovalSection` | BotNexus.Gateway |

## Snapshot Tests (Required BEFORE Refactoring)

Before any code changes, capture the exact prompt output for:
1. Full mode with all features (tools, skills, context files, heartbeat, voice)
2. Minimal mode (sub-agent prompts)
3. No tools mode
4. CodingAgent prompt with git context and tool contributions

These snapshots become the regression tests. After the refactor, the pipeline must produce identical output for the same inputs.

## Migration Plan

1. **Write snapshot tests** for both SystemPromptBuilders
2. **Create `BotNexus.Prompts` project** with `IPromptSection`, `PromptPipeline`, `PromptContext`
3. **Extract shared sections** one at a time (start with SafetySection - simplest, no dependencies)
4. **Replace Gateway builder** to use the pipeline internally (output must match snapshots)
5. **Replace CodingAgent builder** to use shared sections where applicable
6. **Delete old static builders** once both consumers use the pipeline
7. **Verify snapshots pass** at every step

## Risks

1. **Prompt sensitivity**: LLMs are sensitive to exact prompt wording. Even whitespace changes can affect behavior. Snapshot tests are critical.
2. **Section ordering**: The current builder has implicit ordering. The pipeline makes ordering explicit via `Order` property, but initial values must match current output exactly.
3. **Conditional sections**: Some sections depend on others (e.g., messaging section references tool names). The `PromptContext.Extensions` bag handles this but needs careful coordination.

## Acceptance Criteria

- [ ] `BotNexus.Prompts` project exists with section pipeline
- [ ] Gateway uses pipeline, produces identical output to current builder (snapshot verified)
- [ ] CodingAgent uses shared sections where applicable
- [ ] Each section is independently testable
- [ ] Old static SystemPromptBuilder classes are deleted
- [ ] Neither Gateway nor CodingAgent depends on the other
- [ ] AgentCore is not touched
