---
id: feature-code-documentation
title: "Code & Developer Documentation — Contributing, XML Comments, Dev Guides"
type: feature
priority: medium
status: draft
created: 2025-07-23
author: nova
tags: [documentation, developer, contributing, code-comments, github-pages]
---

# Code & Developer Documentation

## Summary

BotNexus needs comprehensive developer documentation for contributors: how to build, test, debug, and extend the project. This includes XML comment standards for the codebase, a contributing guide, development environment setup, and internal architecture walkthroughs — published alongside other docs on GitHub Pages.

## Problem

Current developer docs are fragmented:

- `dev-guide.md` — exists but thin
- `getting-started-dev.md` — development setup, overlaps with `dev-guide.md`
- `docs/development/` — 9 files covering various internals (good content, poor discoverability)
- `docs/training/` — 13 files covering providers, agent core, tool development (excellent content, no structure)
- `AGENTS.md` — repo conventions for AI agents, but no human contributing guide
- `.github/copilot-instructions.md` — minimal
- No `CONTRIBUTING.md` — no guide for human contributors
- No XML comment coverage standards — some files have thorough comments, others have none
- No code style guide beyond what `AGENTS.md` implies
- No debugging guide (how to attach, how to test extensions in isolation)

### Existing Content Inventory

**`docs/development/` (internal architecture):**
- `agent-execution.md` — How agents execute
- `ddd-patterns.md` — Domain-driven design patterns
- `llm-request-lifecycle.md` — Full LLM request lifecycle
- `message-flow.md` — Message routing
- `prompt-pipeline.md` — Prompt construction
- `session-stores.md` — Session persistence
- `triggers-and-federation.md` — Cross-world communication
- `webui-connection.md` — WebUI SignalR connection
- `workspace-and-memory.md` — Agent workspace and memory

**`docs/training/` (deep-dive guides):**
- `01-providers.md` through `11-provider-development-guide.md`
- `tool-security.md`, `agent-events.md`, `tool-development.md`

This is a lot of good content — it just needs structure and a home in the published site.

## Proposal

### 1. Contributing Guide

Create `CONTRIBUTING.md` at repo root with:

```markdown
# Contributing to BotNexus

## Prerequisites
- .NET 10 SDK
- Git
- Windows (primary) / Linux (CI)

## Getting Started
1. Clone the repo
2. `dotnet build BotNexus.slnx`
3. `dotnet test BotNexus.slnx`
4. Copy `docs/sample-config.json` to `~/.botnexus/config.json`
5. Configure a provider
6. `dotnet run --project src/gateway/BotNexus.Cli -- gateway start`

## Development Workflow
- Branch naming: `feature/`, `fix/`, `docs/`
- All tests must pass before commit (pre-commit hook enforces this)
- No `--no-verify` for code changes
- PRs require passing CI

## Code Style
- C# conventions: PascalCase types/methods, camelCase locals
- XML doc comments required on all public APIs
- Test naming: `MethodName_Scenario_ExpectedResult`

## Project Structure
(overview of solution layout)

## Running Tests
(test commands, how to run specific test projects)

## Debugging
(how to debug the gateway, how to debug extensions)
```

### 2. XML Comment Standards

Establish and enforce XML comment coverage:

**Required on:**
- All `public` types and members in `Gateway.Contracts`, `Gateway.Abstractions`, `Domain`
- All `public` types in extension assemblies
- All tool `ExecuteAsync` methods

**Format standard:**
```csharp
/// <summary>
/// One-sentence description of what this does.
/// </summary>
/// <param name="name">Description of parameter.</param>
/// <returns>Description of return value.</returns>
/// <exception cref="InvalidOperationException">When thrown.</exception>
/// <remarks>
/// Extended explanation, usage patterns, or examples.
/// </remarks>
/// <example>
/// <code>
/// var result = await tool.ExecuteAsync(args, ct);
/// </code>
/// </example>
```

**Enforcement:**
- Enable `CS1591` (missing XML comment) as warning for public API projects
- Add to CI: `dotnet build /p:TreatWarningsAsErrors=true` for contract assemblies
- Gradual rollout: start with Contracts and Abstractions, expand

### 3. Developer Guide Structure

Reorganize existing content into a clear developer section:

```
docs/developer/
  getting-started.md                # Dev environment setup (merge getting-started-dev.md + dev-guide.md)
  building.md                       # Build commands, solution structure
  testing.md                        # Test strategy, running tests, writing tests
  debugging.md                      # Debugging gateway, extensions, WebUI
  architecture-internals/
    agent-execution.md              # From docs/development/
    llm-request-lifecycle.md        # From docs/development/
    message-flow.md                 # From docs/development/
    prompt-pipeline.md              # From docs/development/
    session-stores.md               # From docs/development/
    triggers-and-federation.md      # From docs/development/
    webui-connection.md             # From docs/development/
    workspace-and-memory.md         # From docs/development/
  deep-dives/
    providers.md                    # From docs/training/ (merged)
    agent-core.md                   # From docs/training/
    tool-development.md             # From docs/training/
    tool-security.md                # From docs/training/
    provider-development.md         # From docs/training/
    agent-events.md                 # From docs/training/
    thinking-levels.md              # From docs/training/
    context-file-discovery.md       # From docs/training/
  code-standards/
    xml-comments.md                 # XML comment standards
    naming-conventions.md           # Code naming and style
    testing-conventions.md          # Test naming, structure, patterns
    ddd-patterns.md                 # From docs/development/
```

### 4. README Enhancement

Update root `README.md` with:
- Project overview and key features
- Quick-start (3 steps to running)
- Link to published docs site
- Link to contributing guide
- Architecture overview diagram (C4 Level 1 from architecture docs)
- Badges (build status, test count, license)

### Migration from Existing Docs

| Existing | Target | Action |
|----------|--------|--------|
| `dev-guide.md` | `developer/getting-started.md` | Merge with getting-started-dev |
| `getting-started-dev.md` | `developer/getting-started.md` | Merge |
| `docs/development/*.md` (9 files) | `developer/architecture-internals/` | Move, light restructure |
| `docs/training/*.md` (13 files) | `developer/deep-dives/` | Consolidate and restructure |
| `AGENTS.md` | Keep as-is (AI agent conventions) | Add link from contributing guide |

### GitHub Pages Integration

- Developer docs publish as a section in the MkDocs Material site
- Navigation: "Developer Guide" section in sidebar
- Deep-dive and architecture-internal pages linked from architecture docs
- Consistent theme with all other documentation sections

### Acceptance Criteria

1. `CONTRIBUTING.md` at repo root with complete contributor guide
2. XML comment standard documented and enforced on `Gateway.Contracts` and `Gateway.Abstractions`
3. All `docs/development/` and `docs/training/` content migrated to structured `developer/` section
4. Debugging guide covering gateway, extension, and WebUI debugging
5. Root `README.md` updated with overview, quick-start, badges, and links
6. Developer section navigable in published docs site
7. Code standards section with XML comments, naming, and testing conventions

## Dependencies

- `feature-user-documentation` — shared site generator, GitHub Pages deployment
- `feature-api-documentation` — API reference generated from XML comments (depends on comment quality)

## References

- [CONTRIBUTING template (Good Docs Project)](https://gitlab.com/tgdp/templates/-/blob/main/contributing-guide/template-contributing-guide.md)
- [Best-README-Template](https://github.com/othneildrew/Best-README-Template) — README structure
- [Best practices for writing code comments](https://stackoverflow.blog/2021/12/23/best-practices-for-writing-code-comments/)
- [C# XML Documentation Comments](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/)
- [DocFX](https://dotnet.github.io/docfx/) — Generates API docs from XML comments
