# BotNexus Extension Development Guide

## Table of Contents

1. [What is a BotNexus Extension?](#what-is-a-botnexus-extension)
2. [Extension Project Structure & Conventions](#extension-project-structure--conventions)
3. [Creating a Channel Extension](#creating-a-channel-extension)
4. [Creating a Provider Extension](#creating-a-provider-extension)
5. [Creating a Tool Extension](#creating-a-tool-extension)
6. [Dependency Injection Patterns](#dependency-injection-patterns)
7. [Extension Metadata with BotNexusExtensionAttribute](#extension-metadata-with-botnexusextensionattribute)
8. [Accessing Configuration](#accessing-configuration)
9. [OAuth Providers](#oauth-providers)
10. [Webhook Handlers](#webhook-handlers)
11. [Testing Extensions in Isolation](#testing-extensions-in-isolation)
12. [Build Pipeline & Output](#build-pipeline--output)
13. [Troubleshooting](#troubleshooting)

---

## What is a BotNexus Extension?

A **BotNexus extension** is a standalone .NET class library that plugs into the BotNexus core framework without requiring recompilation of the main Gateway application. Extensions enable developers to:

- **Channels**: Add messaging platforms (Discord, Slack, Telegram, etc.)
- **Providers**: Add LLM backends (OpenAI, Anthropic, Copilot, local models, etc.)
- **Tools**: Add agent capabilities (GitHub integration, web search, custom APIs, etc.)

The extension system is built on **dynamic assembly loading** with folder conventions. Extensions are loaded at runtime from the `extensions/` folder, discovered via configuration, and registered into the dependency injection container using either:
- **Convention-based registration** (automatic type discovery)
- **Custom registration** (via `IExtensionRegistrar`)

**Key principles:**
- Extensions are isolated in their own `AssemblyLoadContext` for future hot-reload capability
- No default loading — extensions must be explicitly enabled in `appsettings.json`
- Folder structure follows the pattern: `extensions/{type}/{name}/`
- Build artifacts are automatically copied to the extensions folder by the build pipeline

---

## Extension Project Structure & Conventions

### Project Layout

```
extensions/
├── channels/
│   ├── discord/
│   │   ├── BotNexus.Channels.Discord.dll
│   │   ├── Discord.Net.dll
│   │   └── ...dependencies
│   ├── slack/
│   └── telegram/
├── providers/
│   ├── openai/
│   │   ├── BotNexus.Providers.OpenAI.dll
│   │   ├── OpenAI.dll
│   │   └── ...dependencies
│   ├── copilot/
│   └── anthropic/
└── tools/
    ├── github/
    │   ├── BotNexus.Tools.GitHub.dll
    │   └── ...dependencies
    └── web-search/
```

### Project File (.csproj)

Every extension must include metadata properties and import the shared build targets:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    
    <!-- Extension metadata - REQUIRED for build pipeline -->
    <ExtensionType>channels</ExtensionType>
    <ExtensionName>discord</ExtensionName>
    
    <!-- Optional for NuGet packages -->
    <Description>Discord channel integration for BotNexus</Description>
    <Version>1.0.0</Version>
    <Author>Your Name</Author>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\BotNexus.Core\BotNexus.Core.csproj" />
    <ProjectReference Include="..\BotNexus.Channels.Base\BotNexus.Channels.Base.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Discord.Net" Version="3.19.1" />
  </ItemGroup>

  <!-- Import build targets to copy binaries to extensions/ folder -->
  <Import Project="..\Extension.targets" />

</Project>
```

**Key properties:**
- `ExtensionType`: `channels`, `providers`, or `tools`
- `ExtensionName`: Folder name (snake-case recommended)
- `CopyLocalLockFileAssemblies`: Set to `true` if dependencies must be bundled

### Extension.targets Build Behavior

The `Extension.targets` file automatically:
1. After `Build`: Copies all compiled binaries to `extensions/{type}/{name}/`
2. After `Publish`: Copies binaries to `{PublishDir}/extensions/{type}/{name}/`
3. Excludes reference assemblies (`ref/`, `refint/`)

This means you never manually copy DLLs — they appear in the extension folder when you build.

---

## Creating a Channel Extension

**Channels** are messaging platforms where agents receive user messages and send responses. Examples: Discord, Slack, Telegram, WebSocket.

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

Create a strongly-typed config class for your channel. This will be bound from `appsettings.json`.

```csharp
namespace BotNexus.Channels.Discord;

public class DiscordChannelConfig
{
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
    public IReadOnlyList<string>? AllowFrom { get; set; }
}
```

In `appsettings.json`:
```json
{
  "BotNexus": {
    "Channels": {
      "discord": {
        "enabled": true,
        "botToken": "your-discord-bot-token",
        "allowFrom": ["admin-user-id"]
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

### Step 4: Build and Deploy

```bash
# Build the extension
dotnet build src/BotNexus.Channels.Discord

# Binaries automatically appear in:
# extensions/channels/discord/
```

Enable the extension in `appsettings.json`:
```json
{
  "BotNexus": {
    "Extensions": {
      "channels:discord": { "enabled": true }
    }
  }
}
```

---

## Creating a Provider Extension

**Providers** are LLM backends that agents use for intelligence. Examples: OpenAI, Anthropic, GitHub Copilot, local models.

### Step 1: Create a Provider Class

Inherit from `LlmProviderBase` (or implement `ILlmProvider` directly for advanced use cases).

```csharp
using System.Runtime.CompilerServices;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace BotNexus.Providers.OpenAI;

/// <summary>
/// OpenAI-compatible LLM provider.
/// Supports OpenAI, Azure OpenAI, and any OpenAI-compatible endpoint.
/// </summary>
public sealed class OpenAiProvider : LlmProviderBase
{
    private readonly ChatClient _chatClient;
    private readonly string _defaultModel;

    public OpenAiProvider(
        string apiKey,
        string model = "gpt-4o",
        string? apiBase = null,
        ILogger<OpenAiProvider>? logger = null,
        int maxRetries = 3)
        : base(logger ?? NullLogger<OpenAiProvider>.Instance, maxRetries)
    {
        _defaultModel = model;
        Generation = new GenerationSettings { Model = model };

        var options = apiBase is not null
            ? new OpenAIClientOptions { Endpoint = new Uri(apiBase) }
            : null;

        var openAiClient = options is not null
            ? new OpenAIClient(new ApiKeyCredential(apiKey), options)
            : new OpenAIClient(new ApiKeyCredential(apiKey));

        _chatClient = openAiClient.GetChatClient(model);
    }

    public override string DefaultModel => _defaultModel;

    protected override async Task<LlmResponse> ChatCoreAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var messages = BuildMessages(request);
        var options = BuildChatCompletionOptions(request);

        var completion = await _chatClient.CompleteChatAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var result = completion.Value;

        var content = result.Content.Count > 0 ? result.Content[0].Text : string.Empty;
        var finishReason = MapFinishReason(result.FinishReason);
        var toolCalls = MapToolCalls(result.ToolCalls);

        return new LlmResponse(
            content,
            finishReason,
            toolCalls,
            result.Usage?.InputTokenCount,
            result.Usage?.OutputTokenCount);
    }

    /// <summary>Stream chat completions (yields tokens as they arrive).</summary>
    public override async IAsyncEnumerable<string> ChatStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(request);
        var options = BuildChatCompletionOptions(request);

        await foreach (var update in _chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return part.Text;
            }
        }
    }

    private List<ChatMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<ChatMessage>();

        if (request.SystemPrompt is not null)
            messages.Add(new SystemChatMessage(request.SystemPrompt));

        foreach (var msg in request.Messages)
        {
            messages.Add(msg.Role switch
            {
                "system" => new SystemChatMessage(msg.Content),
                "assistant" => new AssistantChatMessage(msg.Content),
                "tool" => new ToolChatMessage(msg.Content, msg.Content),
                _ => (ChatMessage)new UserChatMessage(msg.Content)
            });
        }

        return messages;
    }

    private ChatCompletionOptions BuildChatCompletionOptions(ChatRequest request)
    {
        var settings = request.Settings;
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = settings.MaxTokens,
            Temperature = (float)settings.Temperature,
        };

        if (request.Tools is { Count: > 0 })
        {
            foreach (var tool in request.Tools)
            {
                var toolDef = ChatTool.CreateFunctionTool(
                    tool.Name,
                    tool.Description,
                    BuildParameterSchema(tool));
                options.Tools.Add(toolDef);
            }
        }

        return options;
    }

    private static BinaryData BuildParameterSchema(ToolDefinition tool)
    {
        var required = tool.Parameters
            .Where(p => p.Value.Required)
            .Select(p => p.Key)
            .ToList();

        var properties = tool.Parameters.ToDictionary(
            p => p.Key,
            p => (object)new Dictionary<string, object>
            {
                ["type"] = p.Value.Type,
                ["description"] = p.Value.Description
            });

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };

        return BinaryData.FromObjectAsJson(schema);
    }

    private static FinishReason MapFinishReason(ChatFinishReason? reason) => reason switch
    {
        ChatFinishReason.Stop => FinishReason.Stop,
        ChatFinishReason.ToolCalls => FinishReason.ToolCalls,
        ChatFinishReason.Length => FinishReason.Length,
        ChatFinishReason.ContentFilter => FinishReason.ContentFilter,
        _ => FinishReason.Other
    };

    private static IReadOnlyList<ToolCallRequest>? MapToolCalls(IReadOnlyList<ChatToolCall> toolCalls)
    {
        if (toolCalls is not { Count: > 0 }) return null;

        return toolCalls.Select(tc =>
        {
            var args = new Dictionary<string, object?>();
            if (tc.FunctionArguments.ToString() is { } json && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
                }
                catch { /* Ignore malformed tool args */ }
            }
            return new ToolCallRequest(tc.Id, tc.FunctionName, args);
        }).ToList();
    }
}
```

### Step 2: Define Provider Configuration

```csharp
namespace BotNexus.Providers.OpenAI;

public class OpenAiProviderConfig
{
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
    public string? ApiBase { get; set; }
    public int MaxRetries { get; set; } = 3;
}
```

### Step 3: Create Extension Registrar

```csharp
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Providers.OpenAI;

public sealed class OpenAiExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ILlmProvider>(sp =>
        {
            var botConfig = sp.GetRequiredService<IOptions<BotNexusConfig>>().Value;
            var providerConfig = configuration.Get<OpenAiProviderConfig>() ?? new OpenAiProviderConfig();
            var logger = sp.GetRequiredService<ILogger<OpenAiProvider>>();

            if (string.IsNullOrWhiteSpace(providerConfig.ApiKey))
                throw new InvalidOperationException("OpenAI provider requires ApiKey configuration.");

            return new OpenAiProvider(
                apiKey: providerConfig.ApiKey,
                model: providerConfig.DefaultModel ?? botConfig.Agents.Model,
                apiBase: providerConfig.ApiBase,
                logger: logger,
                maxRetries: providerConfig.MaxRetries);
        });
    }
}
```

### Step 4: Configuration

```json
{
  "BotNexus": {
    "Extensions": {
      "providers:openai": { "enabled": true }
    },
    "Providers": {
      "openai": {
        "apiKey": "sk-...",
        "defaultModel": "gpt-4o",
        "apiBase": null,
        "maxRetries": 3
      }
    }
  }
}
```

---

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

### Step 4: Configuration

```json
{
  "BotNexus": {
    "Extensions": {
      "tools:github": { "enabled": true }
    },
    "Tools": {
      "github": {
        "apiBase": "https://api.github.com",
        "token": "ghp_...",
        "defaultOwner": "your-org"
      }
    }
  }
}
```

---

## Dependency Injection Patterns

### Convention-Based Registration (Automatic)

If your extension assembly contains **exactly one** type implementing `IChannel`, `ILlmProvider`, or `ITool`, the loader will automatically register it without requiring an `IExtensionRegistrar`.

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

Extensions receive configuration from `appsettings.json` scoped to their own section.

### Configuration Binding in IExtensionRegistrar

```csharp
public void Register(IServiceCollection services, IConfiguration configuration)
{
    // configuration is already scoped to this extension's section
    // (e.g., "BotNexus:Providers:openai" for the OpenAI provider)
    
    var config = configuration.Get<MyExtensionConfig>() ?? new MyExtensionConfig();
    
    // Access properties
    var apiKey = config.ApiKey;
    var model = config.DefaultModel;
}
```

### Configuration Shape in appsettings.json

```json
{
  "BotNexus": {
    "ExtensionsPath": "./extensions",
    "Extensions": {
      "channels:discord": { "enabled": true },
      "providers:openai": { "enabled": true },
      "tools:github": { "enabled": true }
    },
    "Channels": {
      "discord": {
        "enabled": true,
        "botToken": "your-token",
        "allowFrom": ["user-id"]
      }
    },
    "Providers": {
      "openai": {
        "apiKey": "sk-...",
        "defaultModel": "gpt-4o",
        "apiBase": null
      }
    },
    "Tools": {
      "github": {
        "token": "ghp_...",
        "defaultOwner": "your-org"
      }
    }
  }
}
```

### Using IOptions<T> Pattern

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

namespace BotNexus.Providers.Copilot;

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
using System.Text.Json;

namespace BotNexus.Providers.Copilot;

public sealed class FileOAuthTokenStore : IOAuthTokenStore
{
    private readonly string _storePath;

    public FileOAuthTokenStore()
    {
        _storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".botnexus", "tokens");
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

### How Extension.targets Works

When you build an extension project:

1. **Compile**: Normal MSBuild compilation to `bin/Release/net10.0/`
2. **Copy**: `Extension.targets` fires an `AfterTargets="Build"` target that copies all binaries to `extensions/{type}/{name}/`
3. **Deploy**: On `Publish`, binaries are copied to the published output

Example:
```bash
# Build discord extension
dotnet build src/BotNexus.Channels.Discord

# Output appears in:
# extensions/channels/discord/
# ├── BotNexus.Channels.Discord.dll
# ├── Discord.Net.dll
# ├── ...dependencies
```

### Build All Extensions

```bash
# Build the entire solution (includes all extensions)
dotnet build BotNexus.slnx

# All extension DLLs are now in extensions/{type}/{name}/
```

### Publishing

```bash
# Publish the Gateway (includes extensions)
dotnet publish src/BotNexus.Gateway -o ./publish

# Extensions are copied to:
# publish/extensions/{type}/{name}/
```

---

## Troubleshooting

### Extension Not Loading

**Symptom:** Extension folder exists but the extension doesn't register.

**Checks:**
1. Is the extension enabled in `appsettings.json`?
   ```json
   "Extensions": {
     "channels:discord": { "enabled": true }
   }
   ```

2. Do the DLLs exist in the correct folder?
   ```
   extensions/channels/discord/BotNexus.Channels.Discord.dll
   ```

3. Check the Gateway logs for loading errors:
   ```
   Extension loader root: ./extensions
   Scanning extension folder: ./extensions/channels/discord
   Rejected extension 'channels/discord': Assembly version mismatch
   ```

4. Verify assembly compatibility:
   - All extensions must target `net10.0`
   - Core dependencies must have matching versions

### "No assemblies found in extension folder"

**Cause:** Extension DLLs weren't copied during build.

**Fix:** 
1. Ensure `.csproj` has `ExtensionType` and `ExtensionName` properties
2. Ensure `.csproj` imports `Extension.targets`
3. Rebuild: `dotnet clean && dotnet build`
4. Verify output: `ls extensions/{type}/{name}/`

### "IExtensionRegistrar not found"

**Cause:** Extension assembly doesn't have an `IExtensionRegistrar` implementation and convention-based registration failed (multiple or no implementations of `IChannel`/`ILlmProvider`/`ITool`).

**Fix:**
1. Create a class implementing `IExtensionRegistrar`
2. Ensure it's public and not abstract
3. Rebuild and redeploy

### "Configuration section not found"

**Cause:** Extension config is missing from `appsettings.json`.

**Fix:**
```json
{
  "BotNexus": {
    "Channels": {
      "discord": {
        "enabled": true,
        "botToken": "your-token"
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
1. Is the Gateway listening on the correct port (default 18790)?
2. Is the webhook route registered? Check Gateway logs:
   ```
   Registered webhook handler: /webhooks/slack
   ```
3. Is the external service (Slack, etc.) configured to send to the correct URL?
4. Are firewall/network rules blocking the connection?

---

## Summary

To create a BotNexus extension:

1. **Create a class library** targeting `net10.0`
2. **Add `ExtensionType` and `ExtensionName`** to `.csproj`
3. **Import `Extension.targets`** to auto-copy binaries
4. **Implement the interface** (`IChannel`, `ILlmProvider`, or `ITool`)
5. **Optionally implement `IExtensionRegistrar`** for advanced DI
6. **Add configuration** to `appsettings.json`
7. **Build** and binaries appear in `extensions/{type}/{name}/`
8. **Enable in config** and the Gateway loads it at startup

Happy extending!
