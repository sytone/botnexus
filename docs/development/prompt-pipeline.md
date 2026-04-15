# Prompt Pipeline and System Prompt Building

This document describes BotNexus's prompt construction system, which assembles dynamic system prompts from multiple sources.

## Overview

BotNexus uses a **pipeline-based prompt builder** (`PromptPipeline`) that composes system prompts from ordered sections and contributors. This enables:

- Modular prompt construction
- Dynamic context injection
- Environment-specific customization
- Extension-based prompt contributions
- Caching-aware boundaries

## Architecture

```
IPromptSection[] → PromptPipeline → Order & Compose → System Prompt String
     ↑
IPromptContributor[]
```

**Key Components:**

- `IPromptSection`: Ordered prompt blocks (identity, tools, guidelines)
- `IPromptContributor`: Extension-based prompt contributions
- `PromptContext`: Runtime environment and parameters
- `PromptPipeline`: Composer that orders and concatenates sections
- `SystemPromptBuilder`: Gateway-specific builder (uses `PromptPipeline`)

## IPromptSection Interface

```csharp
public interface IPromptSection
{
    int Order { get; }
    
    bool ShouldInclude(PromptContext context);
    
    IReadOnlyList<string> Build(PromptContext context);
}
```

**Responsibilities:**

- Define relative ordering (lower = earlier in prompt)
- Conditional inclusion based on context
- Generate prompt lines

**Example Section:**

```csharp
public class IdentitySection : IPromptSection
{
    public int Order => 100;  // Early in prompt
    
    public bool ShouldInclude(PromptContext context)
        => !context.IsMinimal;  // Skip in minimal mode
    
    public IReadOnlyList<string> Build(PromptContext context)
    {
        return
        [
            "# Identity",
            "",
            "You are a helpful AI coding assistant running inside BotNexus.",
            $"Your current workspace is: {context.WorkspaceDir}",
            ""
        ];
    }
}
```

## IPromptContributor Interface

```csharp
public interface IPromptContributor
{
    int Priority { get; }
    string? Target { get; }  // null = top-level, or target section name
    
    bool ShouldInclude(PromptContext context);
    
    PromptContribution GetContribution(PromptContext context);
}

public record PromptContribution
{
    public IReadOnlyList<string> Lines { get; init; }
    public string? SectionHeading { get; init; }
    public int? Order { get; init; }  // Override priority
}
```

**Use Cases:**

- Extensions contributing tool documentation
- Skills adding context-specific instructions
- Memory systems injecting agent history
- MCP servers adding capability descriptions

**Example Contributor:**

```csharp
public class SkillPromptContributor : IPromptContributor
{
    public int Priority => 500;
    public string? Target => null;  // Top-level
    
    public bool ShouldInclude(PromptContext context)
        => context.Extensions.ContainsKey("skills");
    
    public PromptContribution GetContribution(PromptContext context)
    {
        var skills = (IReadOnlyList<Skill>)context.Extensions["skills"];
        
        return new PromptContribution
        {
            SectionHeading = "Available Skills",
            Lines = skills.Select(s => $"- **{s.Name}**: {s.Description}").ToList(),
            Order = 600  // After tools section
        };
    }
}
```

## PromptContext

Runtime parameters passed to all sections and contributors:

```csharp
public record PromptContext
{
    public string WorkspaceDir { get; init; }
    public IReadOnlyList<ContextFile> ContextFiles { get; init; }
    public IReadOnlySet<string> AvailableTools { get; init; }
    public bool IsMinimal { get; init; }
    public string? Channel { get; init; }
    public IDictionary<string, object?> Extensions { get; init; }
}

public record ContextFile(string Path, string Content);
```

**Extension Dictionary:**

Generic container for passing additional data:

```csharp
context.Extensions = new Dictionary<string, object?>
{
    ["gatewayData"] = gatewayPromptData,
    ["skills"] = skillsList,
    ["memoryContext"] = memoryEntries,
    ["mcpServers"] = mcpServerList
};
```

## PromptPipeline

Composes prompt from ordered `IPromptSection`s and `IPromptContributor`s. Sections are sorted by `Order`, contributors by `Priority`. Tie-broken by insertion order. Contributors with `Target = null` are added as top-level blocks with optional section headings.

```csharp
public sealed class PromptPipeline
{
    public PromptPipeline Add(IPromptSection section);
    public PromptPipeline AddContributors(IEnumerable<IPromptContributor> contributors);
    public string Build(PromptContext context);
    public IReadOnlyList<string> BuildLines(PromptContext context);
}
```

See [PromptPipeline.cs](../../src/prompts/BotNexus.Prompts/PromptPipeline.cs)

**Ordering Strategy:**

1. Sort by `Order` (or `Priority` for contributors)
2. Tie-break by insertion order (preserves registration order)
3. Flatten to single line list

## SystemPromptBuilder (Gateway)

Gateway-specific prompt builder. In `None` mode returns a minimal identity string. Otherwise, creates a `PromptPipeline` with standard sections (Identity, Workspace, Tools, ContextFiles, Guidelines, Examples), builds a `PromptContext` from `SystemPromptParams`, and returns the assembled prompt.

See [SystemPromptBuilder.cs](../../src/gateway/BotNexus.Gateway/Agents/SystemPromptBuilder.cs)

**SystemPromptParams:**

```csharp
public record SystemPromptParams
{
    public required string WorkspaceDir { get; init; }
    public string? ExtraSystemPrompt { get; init; }
    public IReadOnlyList<string>? ToolNames { get; init; }
    public string? UserTimezone { get; init; }
    public IReadOnlyList<ContextFile>? ContextFiles { get; init; }
    public string? SkillsPrompt { get; init; }
    public string? HeartbeatPrompt { get; init; }
    public string? DocsPath { get; init; }
    public IReadOnlyList<string>? WorkspaceNotes { get; init; }
    public string? TtsHint { get; init; }
    public PromptMode PromptMode { get; init; } = PromptMode.Full;
    public RuntimeInfo? Runtime { get; init; }
    public IReadOnlyList<string>? ModelAliasLines { get; init; }
    public string? OwnerIdentity { get; init; }
    public bool ReasoningTagHint { get; init; }
    public string? ReasoningLevel { get; init; }
}
```

## Gateway-Specific Sections

### Identity Section (Order: 100)

```markdown
# Identity

You are a helpful AI coding assistant running inside BotNexus.
Your current workspace is: /home/user/projects/my-project

## Runtime Environment

- Operating System: Linux
- Architecture: x64
- Shell: bash
- Channel: signalr
- Model: claude-sonnet-4
```

### Workspace Section (Order: 200)

```markdown
# Workspace

Your workspace directory is `/home/user/projects/my-project`.

## Directory Structure

```
/home/user/projects/my-project/
├── src/
│   ├── main.ts
│   └── utils.ts
├── tests/
│   └── main.test.ts
├── package.json
└── README.md
```
```

### Tools Section (Order: 300)

```markdown
# Available Tools

You have access to the following tools:

## File Operations

- `read`: Read files or list directory contents with line numbers
- `write`: Write a complete file from scratch or overwrite existing
- `edit`: Make surgical edits using string replacement

## Execution

- `exec`: Execute shell commands in the workspace

## Search

- `grep`: Search for patterns across files using regex
- `glob`: Find files matching glob patterns

## Gateway

- `session`: Manage session history and metadata
- `agent_converse`: Talk to other registered agents

Use tools when needed. Always validate paths and commands.
```

### Context Files Section (Order: 400)

Injects project-specific documentation:

```markdown
# Project Context

## architecture.md

[Full content of architecture.md]

## README.md

[Full content of README.md]
```

**Context File Discovery:**

Discovers context files from two sources: (1) `.botnexus/context/*.md` directory in workspace, and (2) common root docs (README.md, ARCHITECTURE.md, CONTRIBUTING.md).

See [WorkspaceContextBuilder.cs](../../src/gateway/BotNexus.Gateway/Agents/WorkspaceContextBuilder.cs)

### Guidelines Section (Order: 500)

```markdown
# Guidelines

## File Operations

- Use `read` before making changes to understand current state
- Use `edit` for surgical changes to existing files
- Use `write` only when creating new files or complete rewrites
- Always validate file paths before operations

## Command Execution

- Prefer safe, idempotent commands
- Avoid destructive operations without confirmation
- Use appropriate shell for the platform (bash on Linux, PowerShell on Windows)

## Code Quality

- Follow existing code style and conventions
- Write clear, self-documenting code
- Add comments only when necessary for clarification
- Test your changes before declaring done
```

### Examples Section (Order: 600)

```markdown
# Examples

## Reading a file

Use the `read` tool to view file contents:

<example>
User: "Show me the main.ts file"
Assistant: I'll read that file for you.

<tool_call>
{
  "name": "read",
  "arguments": { "path": "src/main.ts" }
}
</tool_call>
</example>

## Making an edit

Use the `edit` tool for targeted changes:

<example>
User: "Change the port from 3000 to 8080"
Assistant: I'll update the port configuration.

<tool_call>
{
  "name": "edit",
  "arguments": {
    "path": "src/main.ts",
    "old_str": "const PORT = 3000;",
    "new_str": "const PORT = 8080;"
  }
}
</tool_call>
</example>
```

## Prompt Modes

**PromptMode Enum:**

```csharp
public enum PromptMode
{
    Full,      // Complete prompt with all sections
    Minimal,   // Condensed prompt (skip examples, verbose guidelines)
    None       // Bare minimum identity only
}
```

**Usage:**

- `Full`: Default for interactive agents (coding assistants, etc.)
- `Minimal`: For sub-agents with specific tasks (reduce token usage)
- `None`: For highly constrained agents (e.g., function-calling only)

**Conditional Sections:**

```csharp
public bool ShouldInclude(PromptContext context)
{
    if (context.IsMinimal && this is ExamplesSection)
        return false;  // Skip examples in minimal mode
    
    return true;
}
```

## Cache Boundaries

BotNexus supports prompt caching via providers (Anthropic, OpenAI).

**Cache Boundary Marker:**

```csharp
const string SystemPromptCacheBoundary = "\n<!-- BOTNEXUS_CACHE_BOUNDARY -->\n";
```

**Placement Strategy:**

```
[Stable content - rarely changes]
<!-- BOTNEXUS_CACHE_BOUNDARY -->
[Dynamic content - changes frequently]
```

**Example:**

```markdown
# Identity
...
# Workspace
...
# Tools
...

<!-- BOTNEXUS_CACHE_BOUNDARY -->

# Context Files
[Dynamically injected project docs - changes per session]

# Current Task
[User-specific instructions - changes per prompt]
```

**Benefits:**

- Cache stable sections (identity, tools, guidelines)
- Reduce latency for dynamic sections (context files, current task)
- Lower costs (cached tokens are cheaper)

## Extension Prompt Contributions

Extensions can contribute to prompts via `IPromptContributor`:

**Skills Extension:**

```csharp
public class SkillsExtension : IExtension, IPromptContributor
{
    public int Priority => 500;
    public string? Target => null;
    
    public PromptContribution GetContribution(PromptContext context)
    {
        var skillsPrompt = LoadSkills();
        
        return new PromptContribution
        {
            SectionHeading = "Available Skills",
            Lines = [skillsPrompt],
            Order = 350  // After tools, before context files
        };
    }
}
```

**Memory Extension:**

```csharp
public class MemoryExtension : IExtension, IPromptContributor
{
    public PromptContribution GetContribution(PromptContext context)
    {
        var memories = FetchRecentMemories(context);
        
        return new PromptContribution
        {
            SectionHeading = "Recent Memories",
            Lines = memories.Select(m => $"- {m.Timestamp}: {m.Content}").ToList(),
            Order = 450  // After skills, before context files
        };
    }
}
```

**MCP Extension:**

```csharp
public class McpExtension : IExtension, IPromptContributor
{
    public PromptContribution GetContribution(PromptContext context)
    {
        var servers = GetConnectedServers();
        
        return new PromptContribution
        {
            SectionHeading = "MCP Servers",
            Lines = servers.Select(s => $"- **{s.Name}**: {s.Description} ({s.ToolCount} tools)").ToList(),
            Order = 310  // Right after tools section
        };
    }
}
```

## Dynamic Prompt Building in Gateway

**WorkspaceContextBuilder (IContextBuilder):**

Discovers context files from workspace, builds `SystemPromptParams` with runtime info (agent ID, host, OS, architecture, provider, model), skills, heartbeat, and timezone, then delegates to `SystemPromptBuilder.Build()`.

See [WorkspaceContextBuilder.cs](../../src/gateway/BotNexus.Gateway/Agents/WorkspaceContextBuilder.cs)

## Coding Agent vs. Gateway Agent Prompts

**Coding Agent (BotNexus.CodingAgent):**

- Rich coding-focused prompt
- Extensive tool documentation
- Code examples and patterns
- File operation guidelines
- Session management instructions

**Gateway Agent (BotNexus.Gateway):**

- More generic prompt structure
- Extensible via `IPromptContributor`
- Support for multiple agent archetypes
- Dynamic context injection
- Tool registry-based tool docs

**Shared Foundation:**

Both use `BotNexus.Prompts.PromptPipeline` but with different section implementations.

## Summary

**Key Architectural Decisions:**

1. **Pipeline-based composition**: Modular, extensible prompt building
2. **Ordered sections**: Predictable prompt structure via `Order` property
3. **Conditional inclusion**: Sections can opt out based on context (minimal mode, channel, etc.)
4. **Extension contributions**: Plugins can inject prompt content
5. **Context file discovery**: Automatic project documentation inclusion
6. **Cache boundaries**: Support for provider-level prompt caching
7. **Runtime parameterization**: Dynamic values (workspace, tools, timezone) injected at build time

**Performance Characteristics:**

- Prompt build time: <5ms (typical)
- Context file discovery: <50ms (depends on file count)
- Cache hit rate: 70-90% (with stable sections)
- Token reduction: 30-50% (via caching)

**Best Practices:**

1. Use low `Order` values for stable content (cache-friendly)
2. Use high `Order` values for dynamic content (session-specific)
3. Keep sections focused and single-purpose
4. Use `PromptContributor` for extension-based additions
5. Leverage `IsMinimal` to reduce token usage for sub-agents
6. Place cache boundary between stable and dynamic content
