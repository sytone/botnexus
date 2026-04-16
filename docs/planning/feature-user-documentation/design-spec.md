---
id: feature-user-documentation
title: "User Documentation — Getting Started, Guides, and Tutorials"
type: feature
priority: high
status: delivered
created: 2025-07-23
updated: 2026-04-16
author: nova
tags: [documentation, user-docs, github-pages, diataxis]
---

# User Documentation

## Summary

BotNexus needs polished, structured user documentation published via GitHub Pages. Users (both end-users configuring agents and operators deploying the gateway) need clear getting-started guides, how-tos, configuration references, and troubleshooting — organized using the Diátaxis framework.

## Problem

Current user docs are scattered markdown files in `docs/`:

- `getting-started.md`, `getting-started-dev.md`, `getting-started-release.md` — three competing entry points
- `docs/user-guide/` exists with 5 files but no clear progression or navigation
- `configuration.md` exists at both root and user-guide level
- No published site — users read raw markdown on GitHub
- No search, no sidebar navigation, no versioning
- No tutorial (step-by-step "build your first agent" experience)
- No how-to collection (common tasks like "add an extension", "configure a channel", "set up cron")

## Proposal

### Framework: Diátaxis

Structure all user docs using the [Diátaxis](https://diataxis.fr/) framework:

| Type | Purpose | BotNexus Examples |
|------|---------|-------------------|
| **Tutorials** | Learning-oriented, step-by-step | "Build your first agent", "Your first extension" |
| **How-to Guides** | Task-oriented, practical steps | "Add a Telegram channel", "Configure file access", "Set up cron jobs" |
| **Reference** | Information-oriented, precise | Configuration schema, CLI reference, extension manifest format |
| **Explanation** | Understanding-oriented, concepts | "How sessions work", "The extension model", "Agent isolation strategies" |

### Site Generator: MkDocs + Material Theme

**Primary choice: [MkDocs Material](https://squidfunk.github.io/mkdocs-material/)**
- Best-in-class theme — polished, responsive, dark mode, instant navigation
- Built-in search (lunr.js, no external service needed)
- Versioning via [mike](https://github.com/jimporter/mike)
- Huge plugin ecosystem (redirects, social cards, tags, blog)
- Mermaid diagram rendering built-in
- GitHub Pages deployment with one-line GitHub Actions workflow
- Markdown-based authoring with extensions (admonitions, tabs, code annotations)
- Python dependency (`pip install mkdocs-material`) — lightweight, well-documented

**For .NET API reference:** DocFX generates API metadata from XML comments → converted to Markdown → consumed by MkDocs Material. This gives us the polished theme everywhere while still getting auto-generated API docs (see `feature-api-documentation`).

### Site Structure

```
docs/
  index.md                          # Landing page / overview
  tutorials/
    first-agent.md                  # Tutorial: Build your first agent
    first-extension.md              # Tutorial: Write your first extension
    first-channel.md                # Tutorial: Add a chat channel
  how-to/
    configure-agents.md             # How to configure agents
    add-extension.md                # How to add/enable extensions
    setup-telegram.md               # How to set up Telegram channel
    configure-cron.md               # How to set up cron jobs
    deploy-github-pages.md          # How to publish your docs
    file-access-policy.md           # How to configure file access
    multi-agent-setup.md            # How to run multiple agents
  reference/
    configuration.md                # Full config.json reference
    cli.md                          # CLI command reference
    extension-manifest.md           # Extension manifest format
    environment-variables.md        # Environment variable reference
    websocket-protocol.md           # WebSocket/SignalR protocol
  explanation/
    sessions.md                     # How sessions work
    extension-model.md              # The extension architecture
    agent-isolation.md              # Isolation strategies explained
    prompt-pipeline.md              # How prompts are built
    memory-system.md                # How agent memory works
  troubleshooting.md                # Common issues and fixes
  faq.md                            # Frequently asked questions
```

### Templates

Use templates from [The Good Docs Project](https://gitlab.com/tgdp/templates):
- Tutorial template
- How-to template
- Reference template
- Troubleshooting template
- Installation guide template

### GitHub Pages Integration

- **Source**: `docs/` folder in main branch
- **Config**: `mkdocs.yml` at repo root
- **Build**: `mkdocs build` via GitHub Actions on push
- **Deploy**: `mkdocs gh-deploy` or `mike deploy` for versioned releases
- **Custom domain**: Optional (e.g., `docs.botnexus.dev`)
- **Search**: Built-in MkDocs Material search (lunr.js)

### Migration from Existing Docs

| Existing File | Target Location | Action |
|---------------|-----------------|--------|
| `getting-started.md` | `tutorials/first-agent.md` | Rewrite as tutorial |
| `getting-started-dev.md` | Split | Tutorial + How-to |
| `getting-started-release.md` | `how-to/install-release.md` | Rewrite as how-to |
| `configuration.md` | `reference/configuration.md` | Keep, enhance |
| `cli-reference.md` | `reference/cli.md` | Keep, enhance |
| `cron-and-scheduling.md` | `how-to/configure-cron.md` | Rewrite as how-to |
| `extension-development.md` | `tutorials/first-extension.md` | Rewrite as tutorial |
| `skills.md` | `how-to/manage-skills.md` | Rewrite as how-to |
| `user-guide/*.md` | Distribute across Diátaxis categories | Evaluate each |
| `troubleshooting.md` | `troubleshooting.md` | Keep, enhance |
| `websocket-protocol.md` | `reference/websocket-protocol.md` | Keep |

### Acceptance Criteria

1. MkDocs Material project initialized with `mkdocs.yml` and GitHub Actions deployment
2. GitHub Pages site live with navigation, search, dark mode, and responsive theme
3. All existing user-facing docs migrated to Diátaxis structure
4. At least 1 tutorial, 3 how-tos, and 3 reference pages complete
5. Templates established for each Diátaxis category
6. Landing page with quick-start and navigation to all sections
7. Build integrates with existing CI (no broken doc links)

## Dependencies

- `feature-api-documentation` — API reference pages may be cross-linked
- `feature-architecture-documentation` — Architecture pages cross-linked from explanation section

## References

- [Diátaxis](https://diataxis.fr/) — Documentation framework
- [The Good Docs Project](https://gitlab.com/tgdp/templates) — Templates
- [DocFX](https://dotnet.github.io/docfx/) — .NET documentation generator
- [MkDocs Material](https://squidfunk.github.io/mkdocs-material/) — Alternative site generator
- [awesome-documentation](https://github.com/pengqun/awesome-documentation) — Curated resource list
