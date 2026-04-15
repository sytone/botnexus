---
id: feature-location-management-research
title: "Location Management — Codebase Research"
type: research
created: 2026-07-14
author: copilot
tags: [locations, configuration, file-access, path-validation]
---

# Location Management — Codebase Research

## Current State Analysis

### 1. Domain Layer: Location and LocationType

**`Location`** (`src/domain/BotNexus.Domain/World/Location.cs`)

A sealed record with four properties:

| Property     | Type                                   | Notes                                    |
|-------------|----------------------------------------|------------------------------------------|
| `Name`      | `string` (required)                    | Unique identifier                        |
| `Type`      | `LocationType` (required)              | Smart enum                               |
| `Path`      | `string?`                              | Generic — holds path, endpoint, or command |
| `Properties`| `IReadOnlyDictionary<string, string>`  | Extensible key-value pairs               |

**Observations:**
- `Path` is overloaded: it stores filesystem paths for `FileSystem`, URLs for `Api`, and command strings for `McpServer`. The feature proposal adds `endpoint` and `connectionString` as type-specific alternatives, which the current model does not distinguish.
- No `Description` property exists — the feature requires one.
- No config-level representation exists. Locations are built programmatically in `WorldDescriptorBuilder`, not read from config.

**`LocationType`** (`src/domain/BotNexus.Domain/World/LocationType.cs`)

A smart enum with case-insensitive string matching and auto-registration:

| Static Value   | String        |
|---------------|---------------|
| `FileSystem`  | `"filesystem"` |
| `Api`         | `"api"`       |
| `McpServer`   | `"mcp-server"` |
| `RemoteNode`  | `"remote-node"` |
| `Database`    | `"database"` |

`FromString()` auto-registers unknown values — extensible without code changes.

### 2. WorldDescriptor and WorldDescriptorBuilder

**`WorldDescriptor`** (`src/domain/BotNexus.Domain/World/WorldDescriptor.cs`)

A sealed record containing `IReadOnlyList<Location> Locations`. This is the domain's view of all known locations in the world (gateway).

**`WorldDescriptorBuilder`** (`src/gateway/BotNexus.Gateway/Configuration/WorldDescriptorBuilder.cs`)

The builder is the **only** source of locations today. It programmatically constructs locations from config properties:

| Location Pattern | Type | Source |
|-----------------|------|--------|
| `agents-directory` | FileSystem | `gateway.agentsDirectory` or `~/.botnexus/agents` |
| `sessions-directory` | FileSystem | `gateway.sessionsDirectory` or `~/.botnexus/sessions` |
| `extensions-directory` | FileSystem | `gateway.extensions.path` or `~/.botnexus/extensions` |
| `gateway-api` | Api | `gateway.listenUrl` |
| `agent:{id}:workspace` | FileSystem | Derived from agents directory + agent ID |
| `provider:{name}` | Api | `providers.{name}.baseUrl` |
| `mcp:{agentId}:{serverId}` | McpServer | Agent extension config for `botnexus-mcp` |

**Key insight:** Locations are **derived**, not **declared**. The feature proposal inverts this — locations become first-class config entries that other features reference.

### 3. File Access Policy Pipeline

**Configuration model** (`PlatformConfig.cs`):
- `GatewaySettingsConfig.FileAccess` — world-level default policy
- `AgentDefinitionConfig.FileAccess` — per-agent override

Both use `FileAccessPolicyConfig` with `AllowedReadPaths`, `AllowedWritePaths`, `DeniedPaths` as `List<string>?`.

**Mapping** (`PlatformConfigAgentSource.cs`):
- `MapFileAccessPolicy()` takes agent-level and world-level configs
- Agent-level takes full precedence if set; otherwise falls back to world-level
- Maps to domain `FileAccessPolicy` record

**Enforcement** (`DefaultPathValidator.cs`):
- Accepts `FileAccessPolicy?` and workspace path
- Policy empty/null → workspace-only mode
- Path resolution: `~` expansion, relative-to-workspace, `Path.GetFullPath()`
- Glob support via `FileSystemName.MatchesSimpleExpression`
- Deny list checked first (takes priority)
- Workspace is always accessible as fallback

**Current paths are raw strings.** There is no `@location-ref` resolution. Adding location reference support requires intercepting paths in `ResolvePolicyPaths()` or a preprocessing step before `DefaultPathValidator` receives the policy.

### 4. CLI Architecture

**`BotNexus.Cli`** (`src/gateway/BotNexus.Cli/`)

Uses `System.CommandLine` library. The `Program.cs` registers:
- `validate` — Config validation
- `init` — First-time setup
- `agents` — Agent management (list, etc.)
- `config` — Config get/set/schema

Commands are registered via DI and composed in `Program.cs`. Each command class has a `Build(Option<bool> verboseOption)` method that returns a `Command`.

**Pattern for adding `locations` command:**
1. Create `LocationsCommand` class
2. Register in DI container
3. Add to root command in `Program.cs`

**No `doctor` command exists yet.** The `validate` command only checks config syntax. A `doctor` sub-command for locations would be new infrastructure.

### 5. WebUI Architecture

**`BotNexus.WebUI`** (`src/BotNexus.WebUI/`)

Static web assets served via ASP.NET:
- `wwwroot/index.html` — SPA shell
- `wwwroot/js/` — JavaScript modules
- `wwwroot/styles.css` — Styling

The WebUI uses SignalR for real-time communication. Adding a locations view requires:
- New JS module for the locations panel
- API endpoints on the gateway for CRUD operations
- SignalR events for real-time status updates (optional)

### 6. Existing Config JSON Schema

**`docs/botnexus-config.schema.json`** — Auto-generated by `botnexus config schema` from the `PlatformConfig` model graph. Adding `LocationConfig` to `GatewaySettingsConfig` will automatically include it in schema regeneration.

## Gaps Between Current State and Feature Requirements

| Requirement | Current State | Gap |
|------------|---------------|-----|
| Named locations in config | Locations built programmatically | Need `LocationConfig` in `GatewaySettingsConfig` |
| `Description` on Location | Not present on domain record | Need to add property to `Location` |
| Type-specific connection properties | Single `Path` field overloaded | Need `endpoint`, `connectionString` as config-level alternatives |
| `@location-ref` in file access policies | Raw paths only | Need resolution layer in `DefaultPathValidator` or policy mapping |
| `botnexus locations` CLI command | No command exists | Need `LocationsCommand` class |
| `botnexus doctor locations` | No doctor command exists | Need `DoctorCommand` with location health checks |
| WebUI locations view | No locations UI | Need JS module + API endpoints |
| Location-aware WorldDescriptorBuilder | Derives locations from scattered config | Need to merge declared + derived locations |

## Compatibility Considerations

1. **Backward compatibility**: Raw paths in `fileAccess` must continue to work. The `@` prefix is unambiguous — no valid filesystem path starts with `@`.
2. **WorldDescriptorBuilder**: Must merge user-declared locations with auto-derived locations (agents, providers, MCP servers). User-declared locations should take precedence on name collision.
3. **Config migration**: Existing configs without `locations` section continue to work. Locations section is optional.
4. **Smart enum extensibility**: `LocationType.FromString()` auto-registers unknown values, so new types can be added in config without code changes.
