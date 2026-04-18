---
id: feature-api-documentation
title: "API Documentation — REST, SignalR, and .NET API Reference"
type: feature
priority: medium
status: draft
created: 2025-07-23
author: nova
tags: [documentation, api, openapi, docfx, github-pages, swagger]
---

# API Documentation

## Summary

BotNexus exposes a REST API, a SignalR hub, and a .NET extension API. All three need auto-generated, searchable, cross-referenced documentation published on GitHub Pages — with hand-written guides for context.

## Problem

Current API docs are minimal and manual:

- `api-reference.md` — hand-written markdown listing endpoints, quickly goes stale
- `docs/api/openapi.json` — exists but not linked to a rendered UI
- `websocket-protocol.md` — SignalR protocol docs, hand-maintained
- No generated API reference from C# XML comments
- No interactive "try it" experience for REST endpoints
- No extension developer API reference (IAgentTool, ICommandContributor, IChannelAdapter, etc.)
- Controllers have XML doc comments but they're not extracted or published

### Three API Surfaces

| Surface | Consumers | Current State |
|---------|-----------|---------------|
| REST API (`/api/*`) | WebUI, external integrations, CLI | `api-reference.md` (manual) + `openapi.json` |
| SignalR Hub (`/hub/gateway`) | WebUI, future TUI | `websocket-protocol.md` (manual) |
| .NET Extension API | Extension developers | XML comments in source, never published |

## Proposal

### 1. REST API — OpenAPI + Interactive Docs

**Generate OpenAPI spec from controllers:**
- Use [Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) or [NSwag](https://github.com/RicoSuter/NSwag) to auto-generate OpenAPI 3.0 spec from controller attributes and XML comments
- Serve Swagger UI at `/api/docs` in development mode (already common pattern)
- Export `openapi.json` at build time for static site integration

**Publish to GitHub Pages:**
- Embed [Scalar](https://github.com/scalar/scalar) or [Redoc](https://github.com/Redocly/redoc) in the docs site for beautiful, interactive API docs
- Alternative: Use DocFX's REST API doc support with the OpenAPI spec

**Endpoints to document:**
- `GET /api/agents` — List agents
- `GET /api/sessions` — List sessions
- `POST /api/commands/execute` — Execute a slash command
- `GET /api/commands` — List available commands
- `GET /api/extensions` — List loaded extensions
- `GET /api/version` — Gateway version
- `PUT /api/agents/{id}` — Update agent config
- All other controllers

### 2. SignalR Hub — Protocol Reference

**Hand-written but structured:**
- SignalR doesn't have an OpenAPI equivalent, so this stays hand-authored
- Use a structured format: method name, direction (client→server / server→client), parameters, payload schema, examples

**Structure:**
```markdown
## Client → Server Methods
### SendMessage
- **Parameters**: `agentId`, `channelType`, `content`, `sessionId?`
- **Response**: Stream of `ContentDelta`, `ToolCallStart`, `ToolCallEnd`, `MessageEnd`

## Server → Client Events
### ReceiveContentDelta
- **Payload**: `{ sessionId, contentDelta }`
- **When**: During LLM response streaming
```

### 3. .NET Extension API — DocFX Metadata → MkDocs Material

**Pipeline:** DocFX extracts API metadata from assemblies + XML comments → generates Markdown → MkDocs Material renders with the same theme as all other docs.

This hybrid approach gives us auto-generated API reference with the polished MkDocs Material theme, rather than DocFX's default styling.

| Assembly | Contents | Priority |
|----------|----------|----------|
| `BotNexus.Gateway.Contracts` | `IAgentTool`, `ICommandContributor`, `IChannelAdapter`, `CommandDescriptor`, etc. | High — extension devs need this |
| `BotNexus.Gateway.Abstractions` | `IAgentSupervisor`, `ISessionStore`, `IMessageRouter`, etc. | High — architecture consumers |
| `BotNexus.Domain` | Domain primitives: `AgentId`, `SessionId`, `ChannelKey`, etc. | Medium |
| `BotNexus.Agent.Core` | Agent execution: `AgentMessage`, `LlmClient`, tool infrastructure | Medium |
| `BotNexus.Agent.Providers.Core` | Provider contracts | Low (internal) |

**DocFX metadata extraction + Markdown conversion:**
```json
{
  "metadata": [{
    "src": [{
      "files": ["src/gateway/BotNexus.Gateway.Contracts/**/*.csproj",
                "src/gateway/BotNexus.Gateway.Abstractions/**/*.csproj",
                "src/domain/BotNexus.Domain/**/*.csproj"]
    }],
    "dest": "api"
  }],
  "build": {
    "content": [
      { "files": ["api/**.yml", "api/index.md"] },
      { "files": ["docs/**/*.md"] }
    ]
  }
}
```

**Cross-referencing:** DocFX's `xref:` syntax lets user docs link directly to API types:
```markdown
Implement the <xref:BotNexus.Gateway.Abstractions.Extensions.ICommandContributor> interface...
```

### Extension Developer Guide (Conceptual + API)

Complement auto-generated API docs with hand-written guides:

```
docs/extension-development/
  overview.md                       # Extension model concepts
  creating-a-tool.md                # Tutorial: Build an IAgentTool
  creating-a-command.md             # Tutorial: Build an ICommandContributor
  creating-a-channel.md             # Tutorial: Build an IChannelAdapter
  manifest-reference.md             # botnexus-extension.json format
  assembly-loading.md               # How assembly load contexts work
  testing-extensions.md             # How to test extensions
```

### GitHub Pages Integration

- REST API docs: Interactive Scalar/Redoc page embedded in MkDocs Material site
- .NET API docs: DocFX-generated Markdown rendered by MkDocs Material
- SignalR docs: Hand-written protocol reference page
- All three linked from main navigation under "API Reference" section
- Consistent theme across all documentation (MkDocs Material)
- OpenAPI spec downloadable from the docs site

### Build Pipeline

```
dotnet docfx metadata         → Extract XML comments to .yml
DocFX yml → Markdown            → Convert API metadata to .md pages
Swashbuckle/NSwag build step  → Generate openapi.json
mkdocs build                  → Build full site (user + arch + API + dev docs)
GitHub Actions                → Deploy to GitHub Pages
```

### Acceptance Criteria

1. OpenAPI spec auto-generated from controllers at build time
2. Interactive REST API docs (Scalar or Redoc) in published site
3. .NET API reference generated by DocFX for Gateway.Contracts, Gateway.Abstractions, and Domain
4. SignalR protocol reference structured and complete
5. Cross-references between conceptual docs and API types working (`xref:`)
6. Extension developer guide with at least 2 tutorials (tool + command)
7. API docs update automatically when code changes (CI pipeline)
8. OpenAPI spec downloadable from docs site

## Dependencies

- `feature-user-documentation` — shared site generator, GitHub Pages deployment, navigation
- `feature-code-documentation` — XML comment quality in source code

## References

- [DocFX](https://dotnet.github.io/docfx/) — .NET API documentation generator
- [Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) — OpenAPI from ASP.NET Core
- [Scalar](https://github.com/scalar/scalar) — Interactive API docs from OpenAPI
- [Redoc](https://github.com/Redocly/redoc) — OpenAPI documentation renderer
- [API Reference template (Good Docs Project)](https://gitlab.com/tgdp/templates/-/blob/main/api-reference/template-api-reference.md)
