# BotNexus Extension Development Guide

## Table of Contents

1. [What is a BotNexus Extension?](#what-is-a-botnexus-extension)
2. [The `botnexus-extension.json` manifest](#the-botnexus-extensionjson-manifest)
3. [Extension Project Structure & Conventions](#extension-project-structure--conventions)
4. [Creating a Channel Extension](#creating-a-channel-extension)
5. [Provider Extensions](#provider-extensions)
6. [Creating a Tool Extension](#creating-a-tool-extension)
7. [Dependency Injection Patterns](#dependency-injection-patterns)
8. [Extension Metadata with BotNexusExtensionAttribute](#extension-metadata-with-botnexusextensionattribute)
9. [Accessing Configuration](#accessing-configuration)
10. [World-Level Extension Defaults](#world-level-extension-defaults)
11. [OAuth Providers](#oauth-providers)
12. [Webhook Handlers](#webhook-handlers)
13. [Testing Extensions in Isolation](#testing-extensions-in-isolation)
14. [Build Pipeline & Output](#build-pipeline--output)
15. [Troubleshooting](#troubleshooting)

---

## What is a BotNexus Extension?

A **BotNexus extension** is a standalone .NET class library that plugs into the BotNexus core framework without requiring recompilation of the main Gateway application. Extensions enable developers to:

- **Channels**: Add messaging platforms (Discord, Slack, Telegram, etc.)
- **Providers**: Add LLM backends (OpenAI, Anthropic, Copilot, local models, etc.)
- **Tools**: Add agent capabilities (GitHub integration, web search, custom APIs, etc.)

The extension system is built on **dynamic assembly loading** driven by a manifest. Each extension is a
**flat folder** under the extensions root containing its assemblies and a `botnexus-extension.json`
manifest. `AssemblyLoadContextExtensionLoader.DiscoverAsync` enumerates the immediate subdirectories of
that root, and a directory without a `botnexus-extension.json` is skipped silently. There is no
`{type}/{name}` nesting: the extension *type* is declared **inside** the manifest, not encoded in the
path.

**Key principles:**
- The manifest is the contract. No manifest, no extension — see [The `botnexus-extension.json` manifest](#the-botnexus-extensionjson-manifest).
- Extensions are loaded into their own `AssemblyLoadContext`. Extensions declaring `endpoint-contributor` or `api-contributor` are loaded **non-collectible** (ASP.NET uses `Reflection.Emit` for typed hub proxies).
- Discovery is one level deep: `<extensions root>/<extension-folder>/botnexus-extension.json`.
- Extension **configuration** is keyed by the manifest `id` (e.g. `botnexus-exec`), under `gateway.extensions.defaults` or an agent's `extensions` block.

---

## The `botnexus-extension.json` manifest

This is the single most important file in an extension. It is deserialized into `ExtensionManifest`
(`src/gateway/BotNexus.Gateway.Contracts/ExtensionModels.cs`) with case-insensitive property matching,
then validated by `ValidateManifest` before the entry assembly is loaded. A manifest that fails
validation causes the extension to be **skipped with a warning** — the gateway still starts.

### Fields

| Field | Type | Required | Rule |
|-------|------|----------|------|
| `id` | string | **yes** | Non-empty. Unique across all extensions; a duplicate id is treated as already-loaded. Convention: `botnexus-{kebab-name}`. This is the key operators use in config. |
| `name` | string | **yes** | Non-empty human-readable display name. |
| `description` | string | no | One-line summary. Not validated. |
| `version` | string | **yes** | Non-empty. SemVer by convention. |
| `entryAssembly` | string | **yes** | Bare DLL filename, relative to the extension folder. Must exist. Rejected if it contains invalid filename characters, is an absolute path, or resolves outside the extension directory. |
| `extensionTypes` | string[] | **yes** | At least one value, each from the allowed set below. Matched case-insensitively. |
| `dependencies` | string[] | no | Extension ids that must already be loaded. An unresolved dependency fails the load. |
| `enabled` | bool | no | Defaults to `true`. |
| `configSchema` | object[] | no | Declared config fields. Defaults to `[]`. See [configSchema](#configschema). |

### `extensionTypes`

The values are **singular**, and each corresponds to a service contract the loader discovers in the
entry assembly. The allowed set is the `allowedTypes` list in `ValidateManifest`; an unlisted value is
a hard validation failure.

| Value | Contract |
|-------|----------|
| `channel` | `IChannelAdapter` |
| `tool` | `IAgentTool` / `IAgentToolContributor` |
| `command` | `ICommandContributor` |
| `media-handler` | `IMediaHandler` |
| `endpoint-contributor` | `IEndpointContributor` |
| `api-contributor` | `IApiContributor` |
| `hook-handler` | hook handler types |
| `isolation` | `IIsolationStrategy` |
| `session-store` | `ISessionStore` |
| `auth-handler` | `IGatewayAuthHandler` |
| `router` | `IMessageRouter` |
| `agent-registry` | `IAgentRegistry` |
| `agent-supervisor` | `IAgentSupervisor` |
| `agent-communicator` | agent change/notifier contracts |
| `activity-broadcaster` | `IActivityBroadcaster` |

Two values carry behaviour beyond documentation:

- `channel` — an `IHostedService` contributed by a channel extension is registered behind
  `ChannelFaultBarrierHostedService`, so a misconfigured channel costs that channel and not the process.
- `endpoint-contributor` / `api-contributor` — force a non-collectible load context.

### `configSchema`

Each entry declares one **top-level** config field for this extension
(`ExtensionConfigFieldSchema`). `ExtensionConfigValidator` uses it for exactly two things: warn when a
`required` field is absent, and apply `default` when an optional field is absent. It does **not**
type-check values and does **not** descend into nested objects.

| Key | Type | Meaning |
|-----|------|---------|
| `id` | string | Key in the extension's config object. |
| `type` | string | `string`, `integer`, `bool`, `object`, `array`. Declarative only — not enforced. |
| `default` | string | Applied when the field is absent and not required. Always a string in the manifest. |
| `required` | bool | A missing required field produces a startup warning, not a failure. |
| `sensitive` | bool | Masked in logs and in the portal. |
| `description` | string | Shown in the portal's extension view. |

An empty `configSchema: []` is legal and means the extension's config is not manifest-validated.

### Worked example

The smallest real manifest in the tree —
`src/extensions/BotNexus.Extensions.ExecTool/botnexus-extension.json`, deployed verbatim to
`~/.botnexus/extensions/botnexus-exec/botnexus-extension.json`:

```json
{
  "id": "botnexus-exec",
  "name": "Exec Tool",
  "description": "Shell command execution tool for agents",
  "version": "1.0.0",
  "entryAssembly": "BotNexus.Extensions.ExecTool.dll",
  "extensionTypes": ["tool"],
  "enabled": true,
  "configSchema": []
}
```

A channel extension declaring config fields —
`src/extensions/BotNexus.Extensions.Channels.Telegram/botnexus-extension.json` (schema abridged; the
file declares seven fields):

```json
{
  "id": "botnexus-telegram",
  "name": "Telegram Channel",
  "description": "Telegram Bot API channel adapter - long polling or secure webhook, DMs and group topics",
  "version": "1.1.0",
  "entryAssembly": "BotNexus.Extensions.Channels.Telegram.dll",
  "extensionTypes": ["channel"],
  "enabled": true,
  "configSchema": [
    {
      "id": "botToken",
      "type": "string",
      "required": true,
      "sensitive": true,
      "description": "Telegram Bot API token from @BotFather. Required for all operations."
    },
    {
      "id": "pollingTimeoutSeconds",
      "type": "integer",
      "required": false,
      "default": "30",
      "sensitive": false,
      "description": "Long polling timeout in seconds."
    }
  ]
}
```

---

## Extension Project Structure & Conventions

### On-disk layout

One flat folder per extension, named by the manifest `id`. An actual
`~/.botnexus/extensions/` listing:

```text
~/.botnexus/extensions/
├── botnexus-agent365/
├── botnexus-audio-transcription/
├── botnexus-data-store/
├── botnexus-debug-tool/
├── botnexus-exec/
│   ├── botnexus-extension.json
│   ├── BotNexus.Extensions.ExecTool.dll
│   ├── BotNexus.Extensions.ExecTool.deps.json
│   ├── BotNexus.Agent.Core.dll
│   ├── BotNexus.Domain.dll
│   └── runtimes/
├── botnexus-mcp/
├── botnexus-mcp-invoke/
├── botnexus-process/
├── botnexus-qmd/
├── botnexus-servicebus/
├── botnexus-signalr/
├── botnexus-skills/
├── botnexus-telegram/
└── botnexus-web/
```

The extensions root is `gateway.extensions.path` in `config.json`, defaulting to
`~/.botnexus/extensions`. Set `gateway.extensions.enabled` to `false` to disable dynamic loading
entirely.

### Source layout

In the repository, each extension is a project directly under `src/extensions/` — no extra nesting —
with the manifest in the project root:

```text
src/extensions/
├── BotNexus.Extensions.ExecTool/
│   ├── BotNexus.Extensions.ExecTool.csproj
│   ├── botnexus-extension.json
│   └── ...sources
└── BotNexus.Extensions.Channels.Telegram/
```

See `src/extensions/AGENTS.md` for the naming and manifest-formatting rules enforced on this tree.

### Project File (.csproj)

There is no `Extension.targets` and no `ExtensionType`/`ExtensionName` MSBuild property. An extension
project sets `CopyLocalLockFileAssemblies` and declares its own copy target. The real
`BotNexus.Extensions.ExecTool.csproj`, abridged:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!--
      Extension assemblies load into an isolated AssemblyLoadContext, so their managed NuGet
      dependency closure must ship in the extension output directory. Library projects do not copy
      transitive managed dependencies by default, and the ALC cannot resolve one the host has not
      already loaded - it fails with FileNotFoundException at load/dispose time and can crash the
      host. See issues #2184 and #2001.
    -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <RootNamespace>BotNexus.Extensions.ExecTool</RootNamespace>
    <Description>Enhanced shell execution tool with timeout, background mode, and Windows command resolution.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\agent\BotNexus.Agent.Core\BotNexus.Agent.Core.csproj" />
    <ProjectReference Include="..\..\gateway\BotNexus.Gateway.Abstractions\BotNexus.Gateway.Abstractions.csproj" />
  </ItemGroup>

  <!-- Copy extension output + manifest to artifacts/extensions/ for dev-time discovery -->
  <Target Name="CopyExtensionToArtifacts" AfterTargets="Build">
    <PropertyGroup>
      <ExtensionArtifactDir>$(MSBuildThisFileDirectory)..\..\..\artifacts\extensions\botnexus-exec\</ExtensionArtifactDir>
    </PropertyGroup>
    <MakeDir Directories="$(ExtensionArtifactDir)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(ExtensionArtifactDir)" SkipUnchangedFiles="true" />
    <Copy SourceFiles="$(MSBuildThisFileDirectory)botnexus-extension.json" DestinationFolder="$(ExtensionArtifactDir)" SkipUnchangedFiles="true" />
  </Target>

</Project>
```

The manifest must also be copied next to the assembly — the `CopyExtensionToArtifacts` target above
does it for dev-time discovery, and `botnexus serve` re-deploys each project's manifest into
`<extensions root>/<id>/botnexus-extension.json` when it stages extensions.

---

## Creating a Channel Extension

**Channels** are messaging platforms where agents receive user messages and send responses. Examples: Discord, Slack, Telegram, SignalR.

### Step 1: Create a Class Inheriting BaseChannel

All channels inherit from `BaseChannel`, a template method pattern providing common functionality.

```csharp
using BotNexus.Channels.Base;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Discord;

/// <summary>Discord channel implementation using Discord.Net SDK.</summary>
public sealed class DiscordChannel : BaseChannel
{
    private readonly DiscordSocketClient _client;
    private readonly string _botToken;

    public DiscordChannel(
        string botToken,
        IMessageBus messageBus,
        ILogger<DiscordChannel> logger,
        IReadOnlyList<string>? allowList = null)
        : base(messageBus, logger, allowList)
    {
        _botToken = botToken;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Warning
        });
        _client.MessageReceived += OnMessageReceivedAsync;
    }

    /// <summary>Unique channel identifier (e.g., "discord", "slack").</summary>
    public override string Name => "discord";

    /// <summary>Human-readable display name for UI/logs.</summary>
    public override string DisplayName => "Discord";

    /// <summary>Called when the channel should start listening for messages.</summary>
    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        await _client.LoginAsync(TokenType.Bot, _botToken).ConfigureAwait(false);
        await _client.StartAsync().ConfigureAwait(false);
    }

    /// <summary>Called when the channel should stop and clean up resources.</summary>
    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync().ConfigureAwait(false);
        await _client.LogoutAsync().ConfigureAwait(false);
    }

    /// <summary>Send a message to a user/channel on this platform.</summary>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(message.ChatId, out var channelId))
        {
            Logger.LogWarning("Invalid Discord channel ID: {ChatId}", message.ChatId);
            return;
        }

        if (_client.GetChannel(channelId) is IMessageChannel channel)
            await channel.SendMessageAsync(message.Content).ConfigureAwait(false);
        else
            Logger.LogWarning("Discord channel {ChannelId} not found", channelId);
    }

    /// <summary>Internal event handler that publishes incoming messages to the agent loop.</summary>
    private async Task OnMessageReceivedAsync(SocketMessage socketMessage)
    {
        // Ignore bot messages to avoid feedback loops
        if (socketMessage.Author.IsBot) return;

        var inbound = new InboundMessage(
            Channel: Name,
            SenderId: socketMessage.Author.Id.ToString(),
            ChatId: socketMessage.Channel.Id.ToString(),
            Content: socketMessage.Content,
            Timestamp: socketMessage.Timestamp,
            Media: [],
            Metadata: new Dictionary<string, object>
            {
                ["username"] = socketMessage.Author.Username,
                ["message_id"] = socketMessage.Id
            });

        // Publish to the message bus for the agent loop to process
        await PublishMessageAsync(inbound).ConfigureAwait(false);
    }
}
```

### Step 2: Define a Configuration Class

Create a strongly-typed config class for your channel. This will be bound from configuration (`~/.botnexus/config.json` or `appsettings.json`).

```csharp
namespace BotNexus.Channels.Discord;

public class DiscordChannelConfig
{
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
    public IReadOnlyList<string>? AllowFrom { get; set; }
}
```

In configuration:
```json
{
  "BotNexus": {
    "Channels": {
      "Instances": {
        "discord": {
          "enabled": true,
          "botToken": "your-discord-bot-token",
          "allowFrom": ["admin-user-id"]
        }
      }
    }
  }
}
```

### Step 3: Create an Extension Registrar (Optional but Recommended)

Implement `IExtensionRegistrar` to control how your channel is registered in the DI container. This gives you full control over dependencies and validation.

```csharp
using BotNexus.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Discord;

public sealed class DiscordExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var channelConfig = configuration.Get<DiscordChannelConfig>() ?? new DiscordChannelConfig();
        
        // Skip loading if not enabled
        if (!channelConfig.Enabled)
            return;

        // Validate required config
        if (string.IsNullOrWhiteSpace(channelConfig.BotToken))
            throw new InvalidOperationException("Discord channel is enabled but BotToken is missing.");

        // Register as IChannel singleton
        services.AddSingleton<IChannel>(sp => new DiscordChannel(
            channelConfig.BotToken,
            sp.GetRequiredService<IMessageBus>(),
            sp.GetRequiredService<ILogger<DiscordChannel>>(),
            channelConfig.AllowFrom));
    }
}
```

### Step 4: Add the manifest, build and deploy

Add a `botnexus-extension.json` next to the `.csproj`:

```json
{
  "id": "botnexus-discord",
  "name": "Discord Channel",
  "description": "Discord messaging platform integration",
  "version": "1.0.0",
  "entryAssembly": "BotNexus.Extensions.Channels.Discord.dll",
  "extensionTypes": ["channel"],
  "enabled": true,
  "configSchema": [
    {
      "id": "botToken",
      "type": "string",
      "required": true,
      "sensitive": true,
      "description": "Discord bot token."
    }
  ]
}
```

```bash
# Build the extension
dotnet build src/extensions/BotNexus.Extensions.Channels.Discord

# Output (assembly closure + manifest) lands in one flat folder:
# artifacts/extensions/botnexus-discord/
```

Configure the extension by its manifest `id` in `~/.botnexus/config.json`:

```json
{
  "gateway": {
    "extensions": {
      "defaults": {
        "botnexus-discord": { "enabled": true, "botToken": "..." }
      }
    }
  }
}
```

---

## Provider Extensions

**LLM providers are not extensions and are not loaded through the extension pipeline.** They live in
`src/agent/BotNexus.Agent.Providers.*/` projects, implement `IApiProvider` from
`BotNexus.Agent.Providers.Core.Registry`, and are wired directly in
`src/gateway/BotNexus.Gateway.Api/Program.cs`. There is no `provider` value in the manifest's
`extensionTypes` allow-list, so a provider cannot be manifest-loaded at all.

**Authoring a provider is documented in exactly one place:**
[Provider Development Guide](training/11-provider-development-guide.md). This page does not restate it.

Reference implementations:

- `src/agent/BotNexus.Agent.Providers.OpenAICompat/` — working `IApiProvider` implementation.
- `src/agent/BotNexus.Agent.Providers.IntegrationMock/` — deterministic test-only provider.


## Creating a Tool Extension

**Tools** are capabilities that agents can invoke to perform actions. Examples: GitHub API wrapper, web search, custom calculations, email sender.

### Step 1: Create a Tool Class

Implement `ITool` interface with a `Definition` and `ExecuteAsync` method.

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Tools.GitHub;

/// <summary>
/// GitHub tool that exposes read-only GitHub API operations to agents.
/// Actions: get_repo, list_issues, get_issue, list_prs, search_code.
/// </summary>
public sealed class GitHubTool : ITool
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string? _defaultOwner;

    public GitHubTool(GitHubToolConfig config, HttpClient? httpClient = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _defaultOwner = config.DefaultOwner;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(config.ApiBase.TrimEnd('/') + '/');
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.DefaultRequestHeaders.Add("User-Agent", "BotNexus-GitHub-Tool/1.0");

        if (!string.IsNullOrWhiteSpace(config.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
    }

    /// <summary>Define what this tool does and what parameters it accepts.</summary>
    public ToolDefinition Definition { get; } = new(
        Name: "github",
        Description: "Interact with GitHub repositories (read-only). Actions: get_repo, list_issues, get_issue, list_prs, search_code.",
        Parameters: new Dictionary<string, ToolParameterSchema>
        {
            ["action"] = new("string", "Action to perform", 
                Required: true,
                EnumValues: ["get_repo", "list_issues", "get_issue", "list_prs", "search_code"]),
            ["owner"] = new("string", "Repository owner (user or organization)", Required: false),
            ["repo"] = new("string", "Repository name", Required: false),
            ["number"] = new("string", "Issue or PR number", Required: false),
            ["query"] = new("string", "Search query", Required: false),
            ["state"] = new("string", "Filter: open, closed, or all (default: open)", Required: false,
                EnumValues: ["open", "closed", "all"]),
            ["per_page"] = new("string", "Results per page (1-100, default: 10)", Required: false)
        });

    /// <summary>Execute the tool with the given parameters.</summary>
    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing tool '{ToolName}'", Definition.Name);

        try
        {
            var action = GetRequiredString(arguments, "action");
            var owner = GetOptionalString(arguments, "owner", _defaultOwner ?? string.Empty);
            var repo = GetOptionalString(arguments, "repo");
            var state = GetOptionalString(arguments, "state", "open");
            var perPage = GetOptionalInt(arguments, "per_page", 10);

            return await (action.ToLowerInvariant() switch
            {
                "get_repo" => GetRepoAsync(owner, repo, cancellationToken),
                "list_issues" => ListIssuesAsync(owner, repo, state, perPage, cancellationToken),
                "get_issue" => GetIssueAsync(owner, repo, GetRequiredString(arguments, "number"), cancellationToken),
                "list_prs" => ListPrsAsync(owner, repo, state, perPage, cancellationToken),
                "search_code" => SearchCodeAsync(owner, repo, GetRequiredString(arguments, "query"), perPage, cancellationToken),
                _ => throw new ToolArgumentException($"Unknown action '{action}'")
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Tool '{ToolName}' was cancelled", Definition.Name);
            throw;
        }
        catch (ToolArgumentException ex)
        {
            _logger.LogWarning("Tool '{ToolName}' argument error: {Message}", Definition.Name, ex.Message);
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool '{ToolName}' threw an unexpected error", Definition.Name);
            return $"Error executing tool '{Definition.Name}': {ex.Message}";
        }
    }

    private async Task<string> GetRepoAsync(string owner, string repo, CancellationToken ct)
    {
        ValidateOwnerRepo(owner, repo);
        var json = await GetJsonAsync($"repos/{owner}/{repo}", ct).ConfigureAwait(false);
        if (json is not JsonObject obj) return "Repository not found";

        return FormatJson(new
        {
            full_name = obj["full_name"]?.GetValue<string>(),
            description = obj["description"]?.GetValue<string>(),
            language = obj["language"]?.GetValue<string>(),
            stars = obj["stargazers_count"]?.GetValue<int>(),
            forks = obj["forks_count"]?.GetValue<int>(),
            open_issues = obj["open_issues_count"]?.GetValue<int>(),
            default_branch = obj["default_branch"]?.GetValue<string>(),
            html_url = obj["html_url"]?.GetValue<string>(),
            visibility = obj["visibility"]?.GetValue<string>(),
            topics = obj["topics"]?.AsArray()?.Select(t => t?.GetValue<string>()).ToList()
        });
    }

    private async Task<string> ListIssuesAsync(string owner, string repo, string state, int perPage, CancellationToken ct)
    {
        ValidateOwnerRepo(owner, repo);
        var json = await GetJsonAsync($"repos/{owner}/{repo}/issues?state={state}&per_page={perPage}", ct).ConfigureAwait(false);
        if (json is not JsonArray items) return "No issues found";

        var issues = items.OfType<JsonObject>().Select(i => new
        {
            number = i["number"]?.GetValue<int>(),
            title = i["title"]?.GetValue<string>(),
            state = i["state"]?.GetValue<string>(),
            author = i["user"]?["login"]?.GetValue<string>(),
            created_at = i["created_at"]?.GetValue<string>(),
            html_url = i["html_url"]?.GetValue<string>()
        });
        return FormatJson(issues);
    }

    private async Task<string> GetIssueAsync(string owner, string repo, string number, CancellationToken ct)
    {
        ValidateOwnerRepo(owner, repo);
        if (!int.TryParse(number, out _))
            throw new ToolArgumentException("'number' must be a valid integer");
        var json = await GetJsonAsync($"repos/{owner}/{repo}/issues/{number}", ct).ConfigureAwait(false);
        if (json is not JsonObject obj) return "Issue not found";

        return FormatJson(new
        {
            number = obj["number"]?.GetValue<int>(),
            title = obj["title"]?.GetValue<string>(),
            state = obj["state"]?.GetValue<string>(),
            author = obj["user"]?["login"]?.GetValue<string>(),
            body = obj["body"]?.GetValue<string>(),
            labels = obj["labels"]?.AsArray()?.OfType<JsonObject>().Select(l => l["name"]?.GetValue<string>()).ToList(),
            html_url = obj["html_url"]?.GetValue<string>()
        });
    }

    private async Task<string> ListPrsAsync(string owner, string repo, string state, int perPage, CancellationToken ct)
    {
        ValidateOwnerRepo(owner, repo);
        var json = await GetJsonAsync($"repos/{owner}/{repo}/pulls?state={state}&per_page={perPage}", ct).ConfigureAwait(false);
        if (json is not JsonArray items) return "No pull requests found";

        var prs = items.OfType<JsonObject>().Select(p => new
        {
            number = p["number"]?.GetValue<int>(),
            title = p["title"]?.GetValue<string>(),
            state = p["state"]?.GetValue<string>(),
            author = p["user"]?["login"]?.GetValue<string>(),
            head = p["head"]?["label"]?.GetValue<string>(),
            base_branch = p["base"]?["label"]?.GetValue<string>(),
            html_url = p["html_url"]?.GetValue<string>()
        });
        return FormatJson(prs);
    }

    private async Task<string> SearchCodeAsync(string owner, string repo, string query, int perPage, CancellationToken ct)
    {
        var repoFilter = (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo))
            ? $"+repo:{owner}/{repo}" : string.Empty;
        var json = await GetJsonAsync(
            $"search/code?q={Uri.EscapeDataString(query)}{repoFilter}&per_page={perPage}", ct).ConfigureAwait(false);
        if (json is not JsonObject result) return "No results";

        var items = result["items"]?.AsArray()?.OfType<JsonObject>().Select(i => new
        {
            path = i["path"]?.GetValue<string>(),
            repo = i["repository"]?["full_name"]?.GetValue<string>(),
            html_url = i["html_url"]?.GetValue<string>()
        });
        return FormatJson(new
        {
            total_count = result["total_count"]?.GetValue<int>(),
            items
        });
    }

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(body);
    }

    private static void ValidateOwnerRepo(string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ToolArgumentException("'owner' is required (set a default in GitHubToolConfig)");
        if (string.IsNullOrWhiteSpace(repo))
            throw new ToolArgumentException("'repo' is required");
    }

    private static string FormatJson(object? value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> args, string key)
    {
        var value = args.GetValueOrDefault(key)?.ToString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ToolArgumentException($"'{key}' is required and must be nonnempty.");
        return value;
    }

    private static string GetOptionalString(IReadOnlyDictionary<string, object?> args, string key, string defaultValue = "")
        => args.GetValueOrDefault(key)?.ToString() ?? defaultValue;

    private static int GetOptionalInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue = 0)
    {
        var raw = args.GetValueOrDefault(key);
        if (raw is null) return defaultValue;
        if (raw is int i) return i;
        if (raw is long l) return (int)l;
        return int.TryParse(raw.ToString(), out var parsed) ? parsed : defaultValue;
    }
}

internal sealed class ToolArgumentException(string message) : Exception(message);
```

### Step 2: Tool Configuration Class

```csharp
namespace BotNexus.Tools.GitHub;

public class GitHubToolConfig
{
    public string ApiBase { get; set; } = "https://api.github.com";
    public string? Token { get; set; }
    public string? DefaultOwner { get; set; }
}
```

### Step 3: Extension Registrar

```csharp
using BotNexus.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Tools.GitHub;

public sealed class GitHubExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GitHubToolConfig>(configuration);
        services.AddSingleton<ITool>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<GitHubToolConfig>>().Value;
            var logger = sp.GetService<ILogger<GitHubTool>>();
            return new GitHubTool(config, logger: logger);
        });
    }
}
```

### Step 4: Manifest and configuration

```json
{
  "id": "botnexus-github",
  "name": "GitHub Tools",
  "description": "GitHub API tools for agents",
  "version": "1.0.0",
  "entryAssembly": "BotNexus.Extensions.GitHub.dll",
  "extensionTypes": ["tool"],
  "enabled": true,
  "configSchema": [
    { "id": "token", "type": "string", "required": true, "sensitive": true, "description": "GitHub PAT." },
    { "id": "defaultOwner", "type": "string", "required": false, "sensitive": false, "description": "Default repository owner." }
  ]
}
```

Operator config, keyed by the manifest `id`:

```json
{
  "gateway": {
    "extensions": {
      "defaults": {
        "botnexus-github": { "enabled": true, "token": "ghp_...", "defaultOwner": "your-org" }
      }
    }
  }
}
```

---

## Dependency Injection Patterns

> **Emitting telemetry?** Extensions get the same telemetry seam the platform core uses — metrics auto-prefixed to `botnexus.ext.<id>.*` and durable usage isolated to your extension id in the shared store. See [Extension Telemetry](extensions/telemetry.md).

### Convention-Based Registration (Automatic)

If your extension assembly contains **exactly one** type implementing `IChannel` or `ITool`, the loader will automatically register it without requiring an `IExtensionRegistrar`. (LLM providers are not extension-loaded at all — see [Provider Extensions](#provider-extensions).)

```csharp
// Extension loader discovers this automatically
public sealed class MyChannel : IChannel { ... }

// No need for IExtensionRegistrar — it's registered as IChannel
```

**Limitations:**
- Only works for single implementations
- No configuration binding
- No validation
- No logging

### Custom Registration with IExtensionRegistrar (Recommended)

Implement `IExtensionRegistrar` for full control:

```csharp
public sealed class MyExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Bind configuration from this extension's section
        services.Configure<MyConfig>(configuration);
        
        // Validate configuration
        var config = configuration.Get<MyConfig>();
        if (config?.ApiKey is null)
            throw new InvalidOperationException("MyExtension requires ApiKey in config");
        
        // Register as singleton or transient
        services.AddSingleton<IMyInterface>(sp => 
            new MyImplementation(
                sp.GetRequiredService<IMyDependency>(),
                sp.GetRequiredService<ILogger<MyImplementation>>(),
                config));
        
        return services;
    }
}
```

**Benefits:**
- Full DI control
- Configuration validation
- Multiple implementations
- Logging and diagnostics
- Per-request vs singleton control

### Hybrid Approach: Service Extension Methods

For complex registrations, use static extension methods alongside `IExtensionRegistrar`:

```csharp
public static class MyServiceExtensions
{
    public static IServiceCollection AddMyServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<MyConfig>(configuration);
        services.AddSingleton<ITool, MyTool>();
        return services;
    }
}

public sealed class MyExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
        => services.AddMyServices(configuration);
}
```

---

## Extension Metadata with BotNexusExtensionAttribute

Optionally, decorate your assembly with `BotNexusExtensionAttribute` for metadata (informational only — not required for loading).

```csharp
using BotNexus.Core.Abstractions;

[assembly: BotNexusExtension(
    name: "Discord Channel",
    Version = "1.0.0",
    Author = "Your Name",
    Description = "Discord messaging platform integration for BotNexus agents")]

namespace BotNexus.Channels.Discord;
```

Query at runtime:
```csharp
var attr = assembly.GetCustomAttribute<BotNexusExtensionAttribute>();
Console.WriteLine($"{attr?.Name} v{attr?.Version} by {attr?.Author}");
```

---

## Accessing Configuration

Extensions receive configuration scoped to their own section (from `~/.botnexus/config.json` or `appsettings.json`).

### Configuration Binding in IExtensionRegistrar

```csharp
public void Register(IServiceCollection services, IConfiguration configuration)
{
    // configuration is already scoped to this extension's section,
    // keyed by the manifest id (e.g. "botnexus-web")
    
    var config = configuration.Get<MyExtensionConfig>() ?? new MyExtensionConfig();
    
    // Access properties
    var apiKey = config.ApiKey;
    var model = config.DefaultModel;
}
```

### Configuration Shape in config.json

Extension config is keyed by the manifest `id` — there are no `channels:`/`providers:`/`tools:`
prefixes. World-level defaults live under `gateway.extensions.defaults`; per-agent overrides live under
`agents.<id>.extensions`.

```json
{
  "gateway": {
    "extensions": {
      "path": "~/.botnexus/extensions",
      "enabled": true,
      "defaults": {
        "botnexus-exec": { "enabled": true },
        "botnexus-web": { "enabled": true, "search.provider": "brave" }
      }
    }
  },
  "agents": {
    "my-agent": {
      "extensions": {
        "botnexus-web": { "search.maxResults": 10 }
      }
    }
  }
}
```

### Using IOptions&lt;T&gt; Pattern

For best practices, inject `IOptions<T>` in your services:

```csharp
public sealed class MyTool
{
    public MyTool(IOptions<MyToolConfig> options, ILogger<MyTool> logger)
    {
        var config = options.Value;
        Logger = logger;
    }
}
```

---

## World-Level Extension Defaults

To avoid repeating extension configuration across multiple agents, operators can define shared extension defaults at the gateway level. This is particularly useful when many agents need identical extension settings.

### Configuration Structure

Extension defaults are defined in the `gateway.extensions.defaults` section of `config.json`:

```json
{
  "gateway": {
    "extensions": {
      "defaults": {
        "botnexus-skills": { "enabled": true, "maxLoadedSkills": 20 },
        "botnexus-exec": { "enabled": true }
      }
    }
  },
  "agents": {
    "my-agent": {
      "extensions": {
        "botnexus-skills": { "maxLoadedSkills": 30 }
      }
    },
    "assistant": {}
  }
}
```

### Before and After Example

**Before (duplicated per agent):**
```json
{
  "agents": {
    "my-agent": {
      "extensions": {
        "botnexus-skills": { "enabled": true, "maxLoadedSkills": 20 },
        "botnexus-exec": { "enabled": true }
      }
    },
    "assistant": {
      "extensions": {
        "botnexus-skills": { "enabled": true, "maxLoadedSkills": 20 },
        "botnexus-exec": { "enabled": true }
      }
    }
  }
}
```

**After (DRY with world defaults):**
```json
{
  "gateway": {
    "extensions": {
      "defaults": {
        "botnexus-skills": { "enabled": true, "maxLoadedSkills": 20 },
        "botnexus-exec": { "enabled": true }
      }
    }
  },
  "agents": {
    "my-agent": {
      "extensions": {
        "botnexus-skills": { "maxLoadedSkills": 30 }
      }
    },
    "assistant": {}
  }
}
```

### Merge Semantics

When an agent's extension configuration is merged with world defaults:

- **Objects merge recursively** — Keys from both default and agent are combined, with agent values taking precedence on conflicts
- **Scalars and arrays are replaced wholesale** — If an agent provides a scalar or array value, it completely replaces the default (no partial merging)
- **Inheritance is tri-state** - each key in an agent's extension configuration is in exactly one of three states:

| Agent-side shape | Meaning | Merged result |
| --- | --- | --- |
| Key absent | Inherit | The world-level default value |
| Key present with explicit `null` | Suppress | The key is **removed** - it does not appear in the merged output at all |
| Key present with a value | Override | The agent's value |

This matches the semantics of agent configuration sections (`memory`, `search`, `heartbeat`, and friends), so `null`
means the same thing everywhere: *do not inherit this*. It never means "set this to null".

```json
{
  "gateway": {
    "extensions": {
      "defaults": {
        "botnexus-skills": { "enabled": true, "maxLoadedSkills": 20, "skillsPath": "~/.botnexus/skills" }
      }
    }
  },
  "agents": {
    "my-agent": {
      "extensions": {
        "botnexus-skills": {
          "maxLoadedSkills": 30,
          "skillsPath": null
        }
      }
    }
  }
}
```

`my-agent` resolves to `{ "enabled": true, "maxLoadedSkills": 30 }` - `enabled` is inherited, `maxLoadedSkills` is
overridden, and `skillsPath` is suppressed so the extension falls back to its own built-in default.

Suppression works at every depth, including the extension id itself. Setting `"botnexus-skills": null` on an agent
drops that extension's inherited configuration entirely. An explicit `null` for a key that has no world-level
counterpart is a no-op rather than a null leaf.

> **Extension authors:** because `null` is reserved for suppression, a bare JSON `null` will never reach your
> configuration binder. If your extension needs to persist a genuine null-valued setting, model it explicitly
> (for example as an object with a discriminator) instead of relying on `null` surviving the merge.
- **Missing agent config inherits defaults** — Agents without an `extensions` block receive all world defaults unchanged
- **Explicit disabling** — An agent can disable a world-default extension by setting `"enabled": false`
- **Null overrides** — An explicit `null` from an agent removes a value from the merged result

### Merge Examples

| World Default | Agent Override | Effective Config |
|---|---|---|
| `{ "a": 1, "b": 2 }` | `{ "b": 3, "c": 4 }` | `{ "a": 1, "b": 3, "c": 4 }` |
| `{ "a": 1 }` | (absent) | `{ "a": 1 }` |
| (absent) | `{ "b": 2 }` | `{ "b": 2 }` |
| `{ "nested": { "x": 1 } }` | `{ "nested": { "y": 2 } }` | `{ "nested": { "x": 1, "y": 2 } }` |
| `[1, 2]` | `[3, 4]` | `[3, 4]` (arrays replace) |

### Backward Compatibility

Existing configurations without a `gateway.extensions.defaults` section continue to work unchanged. World-level defaults are entirely optional and do not affect deployments that don't use them.

---

## OAuth Providers

Some LLM providers (like GitHub Copilot) require OAuth instead of API keys. Use `IOAuthProvider` to implement OAuth flows.

### OAuth Interfaces

```csharp
namespace BotNexus.Core.Abstractions;

public interface IOAuthProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    bool HasValidToken { get; }
}

public interface IOAuthTokenStore
{
    Task<OAuthToken?> GetTokenAsync(string key);
    Task SaveTokenAsync(string key, OAuthToken token);
    Task DeleteTokenAsync(string key);
}

public record OAuthToken(string AccessToken, DateTime ExpiresAt, string? RefreshToken = null)
{
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt.AddMinutes(-5);
}
```

### Example: GitHub Device Code Flow (Copilot)

```csharp
using BotNexus.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Copilot;

public sealed class CopilotOAuthProvider : IOAuthProvider
{
    private readonly string _clientId;
    private readonly IOAuthTokenStore _tokenStore;
    private readonly ILogger _logger;

    public CopilotOAuthProvider(string clientId, IOAuthTokenStore tokenStore, ILogger logger)
    {
        _clientId = clientId;
        _tokenStore = tokenStore;
        _logger = logger;
    }

    public bool HasValidToken { get; private set; }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await _tokenStore.GetTokenAsync("copilot") ?? await AcquireTokenAsync(cancellationToken);
        HasValidToken = token is not null && !token.IsExpired;
        return token?.AccessToken ?? throw new InvalidOperationException("No valid Copilot token");
    }

    private async Task<OAuthToken?> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        // Implement GitHub device code flow
        _logger.LogInformation("Starting GitHub device code flow for Copilot...");
        
        using var http = new HttpClient();
        
        // 1. Request device code
        var deviceRequest = new { client_id = _clientId, scopes = "read:user" };
        var deviceResponse = await http.PostAsJsonAsync(
            "https://github.com/login/device/code", 
            deviceRequest, 
            cancellationToken);
        
        var deviceData = await deviceResponse.Content.ReadAsAsync<dynamic>();
        var deviceCode = deviceData.device_code;
        var userCode = deviceData.user_code;
        var verificationUri = deviceData.verification_uri;
        
        _logger.LogInformation("Visit {Url} and enter code: {UserCode}", verificationUri, userCode);
        Console.WriteLine($"Visit {verificationUri} and enter: {userCode}");
        
        // 2. Poll for token
        while (true)
        {
            await Task.Delay(5000, cancellationToken);
            
            var tokenRequest = new { client_id = _clientId, device_code = deviceCode, grant_type = "urn:ietf:params:oauth:grant-type:device_code" };
            var tokenResponse = await http.PostAsJsonAsync(
                "https://github.com/login/oauth/access_token",
                tokenRequest,
                cancellationToken);
            
            var tokenData = await tokenResponse.Content.ReadAsAsync<dynamic>();
            
            if (tokenData.access_token is not null)
            {
                var token = new OAuthToken(
                    AccessToken: tokenData.access_token,
                    ExpiresAt: DateTime.UtcNow.AddHours(8),
                    RefreshToken: tokenData.refresh_token);
                
                await _tokenStore.SaveTokenAsync("copilot", token);
                return token;
            }
        }
    }
}
```

### File-Based Token Store

```csharp
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using System.Text.Json;

namespace BotNexus.Agent.Providers.Copilot;

public sealed class FileOAuthTokenStore : IOAuthTokenStore
{
    private readonly string _storePath;

    public FileOAuthTokenStore(string? tokenDirectory = null)
    {
        _storePath = tokenDirectory
            ?? Path.Combine(BotNexusHome.ResolveHomePath(), "tokens");
        Directory.CreateDirectory(_storePath);
    }

    public async Task<OAuthToken?> GetTokenAsync(string key)
    {
        var filePath = Path.Combine(_storePath, $"{key}.json");
        if (!File.Exists(filePath)) return null;

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<OAuthToken>(json);
    }

    public async Task SaveTokenAsync(string key, OAuthToken token)
    {
        var filePath = Path.Combine(_storePath, $"{key}.json");
        var json = JsonSerializer.Serialize(token);
        await File.WriteAllTextAsync(filePath, json);
    }

    public Task DeleteTokenAsync(string key)
    {
        var filePath = Path.Combine(_storePath, $"{key}.json");
        if (File.Exists(filePath)) File.Delete(filePath);
        return Task.CompletedTask;
    }
}
```

---

## Webhook Handlers

If your extension needs to receive incoming webhooks (e.g., Slack request URL callbacks), implement `IWebhookHandler`.

```csharp
using BotNexus.Core.Abstractions;
using Microsoft.AspNetCore.Http;

namespace BotNexus.Channels.Slack;

public sealed class SlackWebhookHandler : IWebhookHandler
{
    private readonly IChannel _slackChannel;
    private readonly ILogger _logger;

    public SlackWebhookHandler(IChannel slackChannel, ILogger<SlackWebhookHandler> logger)
    {
        _slackChannel = slackChannel;
        _logger = logger;
    }

    /// <summary>Route path where this handler listens (e.g., /webhooks/slack).</summary>
    public string Path => "/webhooks/slack";

    /// <summary>Process incoming webhook request.</summary>
    public async Task<IResult> HandleAsync(HttpContext context)
    {
        try
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            _logger.LogDebug("Received Slack webhook: {Body}", body);

            var slackEvent = JsonSerializer.Deserialize<SlackEventWrapper>(body);

            // Handle Slack verification challenge
            if (slackEvent?.Type == "url_verification")
            {
                return Results.Ok(new { challenge = slackEvent.Challenge });
            }

            // Handle event
            if (slackEvent?.Event is not null)
            {
                var inbound = new InboundMessage(
                    Channel: "slack",
                    SenderId: slackEvent.Event.User,
                    ChatId: slackEvent.Event.Channel,
                    Content: slackEvent.Event.Text,
                    Timestamp: DateTime.UtcNow,
                    Media: [],
                    Metadata: new Dictionary<string, object> { ["ts"] = slackEvent.Event.Ts });

                // Publish to agent loop
                // await _slackChannel.PublishMessageAsync(inbound);
            }

            return Results.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Slack webhook");
            return Results.StatusCode(500);
        }
    }
}
```

Register webhook handlers in your extension registrar:

```csharp
services.AddSingleton<IWebhookHandler>(sp => 
    new SlackWebhookHandler(
        sp.GetRequiredService<IChannel>(),
        sp.GetRequiredService<ILogger<SlackWebhookHandler>>()));
```

The Gateway automatically registers all `IWebhookHandler` instances at startup.

---

## Testing Extensions in Isolation

Test your extension without the full Gateway by mocking dependencies.

### Example: Unit Test for a Tool

```csharp
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Tools.GitHub;
using Xunit;

public sealed class GitHubToolTests
{
    [Fact]
    public async Task GetRepo_WithValidOwnerAndRepo_ReturnsRepositoryData()
    {
        // Arrange
        var config = new GitHubToolConfig { DefaultOwner = "microsoft" };
        var tool = new GitHubTool(config);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "get_repo",
            ["owner"] = "microsoft",
            ["repo"] = "vscode"
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("vscode", result);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingRequiredArg_ReturnsErrorMessage()
    {
        // Arrange
        var config = new GitHubToolConfig();
        var tool = new GitHubTool(config);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "get_repo"
            // Missing "owner" and "repo"
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        Assert.Contains("Error", result);
    }
}
```

### Example: DI Integration Test

```csharp
using BotNexus.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class GitHubExtensionTests
{
    [Fact]
    public void GitHubExtensionRegistrar_Registers_ITool()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DefaultOwner"] = "microsoft",
                ["Token"] = "ghp_test"
            })
            .Build();

        // Act
        var registrar = new GitHubExtensionRegistrar();
        registrar.Register(services, config);

        // Assert
        var provider = services.BuildServiceProvider();
        var tool = provider.GetService<ITool>();
        Assert.NotNull(tool);
        Assert.Equal("github", tool.Definition.Name);
    }
}
```

---

## Build Pipeline & Output

### How the copy target works

Each extension project declares its own `AfterTargets="Build"` copy target (there is no shared
`Extension.targets`). When you build an extension project:

1. **Compile**: normal MSBuild compilation to `bin/<Config>/net10.0/`, including the transitive managed
   dependency closure because `CopyLocalLockFileAssemblies` is `true`.
2. **Copy**: the project's `CopyExtensionToArtifacts` target copies the entry assembly and the
   `botnexus-extension.json` manifest into `artifacts/extensions/<manifest-id>/`.
3. **Deploy**: `botnexus serve` stages each extension project's output plus its manifest into
   `<extensions root>/<manifest-id>/`, pruning stale files from earlier generations.

Example:
```bash
# Build the exec tool extension
dotnet build src/extensions/BotNexus.Extensions.ExecTool

# Output appears in one flat folder named by the manifest id:
# artifacts/extensions/botnexus-exec/
# ├── botnexus-extension.json
# ├── BotNexus.Extensions.ExecTool.dll
# └── ...dependency closure
```

### Build All Extensions

```bash
# Build the entire solution (includes all extensions)
dotnet build BotNexus.slnx
```

### Deploying

```bash
# Stage extensions into the gateway's extensions root
botnexus serve

# Extensions land in:
# ~/.botnexus/extensions/<manifest-id>/
```

---

## Troubleshooting

### Extension Not Loading

**Symptom:** Extension folder exists but the extension doesn't register.

**Checks:**
1. Does `<extensions root>/<folder>/botnexus-extension.json` exist? A directory without a manifest is
   skipped silently at Debug level — this is the single most common cause.

2. Is `enabled` `true` in the manifest, and is the extension enabled in config by its manifest `id`?
   ```json
   "gateway": { "extensions": { "defaults": { "botnexus-discord": { "enabled": true } } } }
   ```

3. Does the file named by `entryAssembly` exist in that same folder? A missing entry assembly logs a
   manifest-validation warning and skips the extension.

4. Are the `extensionTypes` values valid and **singular** (`"tool"`, `"channel"`, not `"tools"`)? An
   unrecognised value fails validation outright.

5. Check the Gateway logs for loading errors:
   ```
   Skipping '~/.botnexus/extensions/foo' because botnexus-extension.json is missing.
   Skipping extension in '...' due to manifest or assembly validation failure.
   Extension 'botnexus-exec' from '...': discovered 1 implementation(s): IAgentTool->ExecTool
   ```

6. Verify assembly compatibility:
   - All extensions must target `net10.0`
   - Core dependencies must have matching versions

### "No assemblies found in extension folder"

**Cause:** Extension DLLs weren't copied during build.

**Fix:**
1. Ensure `.csproj` sets `CopyLocalLockFileAssemblies=true`
2. Ensure `.csproj` has a copy target that also copies `botnexus-extension.json`
3. Rebuild: `dotnet clean && dotnet build`
4. Verify output: `ls artifacts/extensions/<manifest-id>/`

### "IExtensionRegistrar not found"

**Cause:** Extension assembly doesn't have an `IExtensionRegistrar` implementation and convention-based registration failed (multiple or no implementations of `IChannel`/`ITool`).

**Fix:**
1. Create a class implementing `IExtensionRegistrar`
2. Ensure it's public and not abstract
3. Rebuild and redeploy

### "Configuration section not found"

**Cause:** Extension config is missing from `~/.botnexus/config.json`, or is keyed by something other
than the manifest `id`.

**Fix:**
```json
{
  "gateway": {
    "extensions": {
      "defaults": {
        "botnexus-discord": { "enabled": true, "botToken": "your-token" }
      }
    }
  }
}
```

### Dependency Version Conflicts

**Symptom:** `System.Net.Http version 4.3.4 was referenced by two extensions`

**Cause:** Extensions have conflicting transitive dependencies.

**Fix:**
1. Use `AssemblyLoadContext` isolation (extensions are already isolated)
2. Align dependency versions across extensions
3. Use `CopyLocalLockFileAssemblies=false` if a shared assembly is available in the runtime

### OAuth Token Expiration

**Symptom:** `IOAuthProvider.HasValidToken` is false after some hours.

**Fix:**
1. Implement token refresh logic in `IOAuthProvider.GetAccessTokenAsync()`
2. Check `OAuthToken.IsExpired` before use
3. Persist refresh tokens in `IOAuthTokenStore`

### Webhook Not Receiving Events

**Symptom:** `IWebhookHandler` is registered but webhook events aren't arriving.

**Checks:**
1. Is the Gateway listening on the correct port (default 5005)?
2. Is the webhook route registered? Check Gateway logs:
   ```
   Registered webhook handler: /webhooks/slack
   ```
3. Is the external service (Slack, etc.) configured to send to the correct URL?
4. Are firewall/network rules blocking the connection?

---

## Summary

To create a BotNexus extension:

1. **Create a class library** targeting `net10.0` under `src/extensions/`
2. **Add a `botnexus-extension.json`** declaring `id`, `name`, `version`, `entryAssembly` and singular `extensionTypes`
3. **Set `CopyLocalLockFileAssemblies=true`** and add a copy target that also copies the manifest
4. **Implement the contract** for your declared type (`IChannelAdapter`, `IAgentTool`, …; LLM providers are **not** extensions — see [Provider Extensions](#provider-extensions))
5. **Optionally implement `IExtensionRegistrar`** for advanced DI
6. **Add configuration** keyed by the manifest `id` in `~/.botnexus/config.json`
7. **Build** and the flat extension folder appears in `artifacts/extensions/<manifest-id>/`
8. **Enable in config** and the Gateway loads it at startup

Happy extending!
