---
id: feature-architecture-documentation
title: "Architecture Documentation — arc42, C4, ADRs"
type: feature
priority: medium
status: draft
created: 2025-07-23
author: nova
tags: [documentation, architecture, arc42, c4-model, adr, github-pages]
---

# Architecture Documentation

## Summary

BotNexus needs structured architecture documentation using industry-standard frameworks (arc42 + C4 model) published alongside user docs on GitHub Pages. Architecture decisions should be tracked as ADRs (Architecture Decision Records).

## Problem

Current architecture docs in `docs/architecture/` are good starting points but lack structure:

- `overview.md` — high-level but no standard framework
- `domain-model.md` — detailed DDD model but no visual diagrams
- `extension-guide.md` — extension architecture
- `principles.md` — design principles
- `system-flows.md` — message flow descriptions
- `docs/archive/architecture-old/` — superseded docs still in tree

Missing:
- **No C4 diagrams** — no visual system context, container, or component views
- **No arc42 sections** — no quality requirements, deployment view, cross-cutting concerns, risks
- **No ADRs** — architectural decisions are buried in planning specs or commit messages
- **No deployment architecture** — how the gateway, extensions, agents, and channels compose
- **No runtime view** — sequence diagrams for key flows (message handling, sub-agent spawning, session lifecycle)

## Proposal

### Framework: arc42

Use the [arc42](https://arc42.org/) template adapted for BotNexus:

```
docs/architecture/
  01-introduction-goals.md          # Requirements, quality goals, stakeholders
  02-constraints.md                 # Technical & organizational constraints
  03-context-scope.md               # System context (C4 Level 1)
  04-solution-strategy.md           # Fundamental decisions, technology choices
  05-building-blocks.md             # Container & component views (C4 Levels 2-3)
  06-runtime-view.md                # Key scenarios as sequence diagrams
  07-deployment-view.md             # Infrastructure, deployment topology
  08-crosscutting-concepts.md       # Security, error handling, logging, persistence
  09-architecture-decisions.md      # Index of ADRs (or link to decisions/)
  10-quality-requirements.md        # Quality tree, scenarios
  11-risks-tech-debt.md             # Known risks and technical debt
  12-glossary.md                    # Domain terminology
```

### C4 Model Diagrams

Use [Mermaid C4 syntax](https://mermaid.js.org/syntax/c4.html) for all architecture diagrams — renders natively in GitHub Markdown and DocFX/MkDocs:

#### Level 1 — System Context
- BotNexus Gateway ↔ Users (WebUI, TUI, API) ↔ LLM Providers ↔ External Channels (Telegram, Signal)

#### Level 2 — Container
- Gateway API, Agent Supervisor, Session Store, Extension Loader, Channel Adapters, WebUI, Cron Scheduler

#### Level 3 — Component (selected containers)
- Agent Supervisor internals: AgentHandle, IsolationStrategy, ToolRegistry, PromptPipeline
- Extension Loader: AssemblyLoadContext, DiscoverableContracts, HookDispatcher

#### Level 4 — Code (optional, generated)
- Link to auto-generated API docs from `feature-api-documentation`

### Architecture Decision Records (ADRs)

Store decisions in `docs/decisions/`:

```
docs/decisions/
  README.md                         # Index of all ADRs
  0001-extension-assembly-load-context.md
  0002-session-store-abstraction.md
  0003-agent-isolation-strategies.md
  0004-signalr-for-webui.md
  0005-ddd-domain-primitives.md
  ...
```

**ADR template** (based on [Michael Nygard's format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)):

```markdown
# ADR-NNNN: Title

## Status
Accepted | Superseded by ADR-XXXX | Deprecated

## Context
What is the issue we're seeing that motivates this decision?

## Decision
What is the change we're proposing and/or doing?

## Consequences
What becomes easier or harder because of this change?
```

### Migration from Existing Docs

| Existing File | Target | Action |
|---------------|--------|--------|
| `architecture/overview.md` | `03-context-scope.md` + `04-solution-strategy.md` | Split and restructure |
| `architecture/domain-model.md` | `05-building-blocks.md` | Add C4 diagrams |
| `architecture/extension-guide.md` | `05-building-blocks.md` (extension section) | Merge |
| `architecture/principles.md` | `04-solution-strategy.md` | Merge with strategy |
| `architecture/system-flows.md` | `06-runtime-view.md` | Add sequence diagrams |
| `development/ddd-patterns.md` | `08-crosscutting-concepts.md` | Merge |
| Planning specs (key decisions) | `docs/decisions/` | Extract as ADRs |

### Diagram Tooling

| Tool | Use Case | Notes |
|------|----------|-------|
| Mermaid C4 | Architecture diagrams | Built into GitHub, DocFX, MkDocs |
| Mermaid sequence | Runtime views | Message flows, session lifecycle |
| Mermaid flowchart | Process flows | Extension loading, prompt pipeline |
| Structurizr (optional) | C4 DSL source | If Mermaid C4 proves too limited |

### GitHub Pages Integration

- Architecture docs publish as a section in the MkDocs Material site
- Navigation: Architecture section in main sidebar with arc42 numbering
- ADRs browsable with index page
- Mermaid diagrams render natively (MkDocs Material has built-in Mermaid support)

### Acceptance Criteria

1. arc42 structure established with all 12 sections (content can be minimal initially)
2. C4 Level 1 (System Context) and Level 2 (Container) diagrams created in Mermaid
3. At least 5 ADRs extracted from existing decisions
4. ADR template and numbering convention established
5. Existing architecture docs migrated into arc42 structure
6. Runtime view with at least 3 key sequence diagrams
7. All diagrams render in both GitHub Markdown preview and published site
8. Cross-links between architecture docs and API reference

## Dependencies

- `feature-user-documentation` — shared site generator and GitHub Pages deployment
- `feature-api-documentation` — API reference linked from building blocks view

## References

- [arc42](https://arc42.org/) — Architecture documentation template
- [C4 Model](https://c4model.com/) — Software architecture visualization
- [Mermaid C4 Diagrams](https://mermaid.js.org/syntax/c4.html) — C4 in Mermaid
- [ADR GitHub Organization](https://adr.github.io/) — ADR resources
- [log4brains](https://github.com/thomvaill/log4brains) — Optional ADR site generator
- [arc42 + C4 example](https://github.com/bitsmuggler/arc42-c4-software-architecture-documentation-example) — Combined example
