using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Linq;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Diagnostics;
using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Types;
using BotNexus.Cron;
using BotNexus.Cron.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Tools;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Extensions.Skills;
using BotNexus.Extensions.Mcp;
using BotNexus.Extensions.McpInvoke;
using BotNexus.Extensions.WebTools;
using BotNexus.Memory;
using BotNexus.Memory.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentCoreUserMessage = BotNexus.AgentCore.Types.UserMessage;
using GatewayBeforeToolCallResult = BotNexus.Gateway.Abstractions.Hooks.BeforeToolCallResult;
using GatewayAfterToolCallResult = BotNexus.Gateway.Abstractions.Hooks.AfterToolCallResult;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// In-process isolation strategy — runs agents directly in the Gateway process
/// by wrapping <see cref="BotNexus.AgentCore.Agent"/>.
/// </summary>
/// <remarks>
/// This is the default and fastest strategy. No process or container boundaries.
/// Suitable for development, testing, and trusted agent deployments.
/// </remarks>
public sealed class InProcessIsolationStrategy : IIsolationStrategy
{
    private readonly LlmClient _llmClient;
    private readonly GatewayAuthManager _authManager;
    private readonly IContextBuilder _contextBuilder;
    private readonly IAgentToolFactory _toolFactory;
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IToolRegistry _toolRegistry;
    private readonly IMemoryStoreFactory _memoryStoreFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InProcessIsolationStrategy> _logger;

    public InProcessIsolationStrategy(
        LlmClient llmClient,
        GatewayAuthManager authManager,
        IContextBuilder contextBuilder,
        IAgentToolFactory toolFactory,
        IAgentWorkspaceManager workspaceManager,
        IToolRegistry toolRegistry,
        IMemoryStoreFactory memoryStoreFactory,
        IServiceProvider serviceProvider,
        ILogger<InProcessIsolationStrategy> logger)
    {
        _llmClient = llmClient;
        _authManager = authManager;
        _contextBuilder = contextBuilder;
        _toolFactory = toolFactory;
        _workspaceManager = workspaceManager;
        _toolRegistry = toolRegistry;
        _memoryStoreFactory = memoryStoreFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "in-process";

    /// <inheritdoc />
    public async Task<IAgentHandle> CreateAsync(AgentDescriptor descriptor, AgentExecutionContext context, CancellationToken cancellationToken = default)
    {
        var model = _llmClient.Models.GetModel(descriptor.ApiProvider, descriptor.ModelId)
            ?? throw new InvalidOperationException($"Model '{descriptor.ModelId}' for provider '{descriptor.ApiProvider}' is not registered.");

        // Override model BaseUrl from auth endpoint or provider config (e.g., enterprise Copilot)
        var apiEndpoint = _authManager.GetApiEndpoint(descriptor.ApiProvider);
        if (!string.IsNullOrWhiteSpace(apiEndpoint))
            model = model with { BaseUrl = apiEndpoint };

        var enrichedSystemPrompt = await _contextBuilder.BuildSystemPromptAsync(descriptor, cancellationToken);

        var workspacePath = _workspaceManager.GetWorkspacePath(descriptor.AgentId);
        var pathValidator = new DefaultPathValidator(descriptor.FileAccess, workspacePath);
        var workspaceTools = _toolFactory.CreateTools(workspacePath, pathValidator);
        var workspaceToolNames = new HashSet<string>(workspaceTools.Select(tool => tool.Name), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<IAgentTool> selectedWorkspaceTools = descriptor.ToolIds.Count > 0
            ? [.. workspaceTools.Where(tool => descriptor.ToolIds.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))]
            : workspaceTools;

        var extensionTools = descriptor.ToolIds.Count > 0
            ? _toolRegistry.ResolveTools(descriptor.ToolIds)
            : _toolRegistry.GetAll();

        var tools = selectedWorkspaceTools
            .Concat(extensionTools.Where(tool => !workspaceToolNames.Contains(tool.Name)))
            .ToList();

        _logger.LogInformation(
            "Tool setup for '{AgentId}': workspace={WorkspaceCount} extension={ExtCount} total={Total} toolIds={ToolIdCount} workspace={WorkspacePath}",
            descriptor.AgentId, workspaceTools.Count, extensionTools.Count(), tools.Count,
            descriptor.ToolIds.Count, workspacePath);

        if (descriptor.Memory?.Enabled == true)
        {
            var memoryStore = _memoryStoreFactory.Create(descriptor.AgentId);
            // Initialize asynchronously — don't block handle creation.
            // Memory tools work immediately; the store initializes in the background.
            _ = memoryStore.InitializeAsync(CancellationToken.None);
            tools.Add(new MemorySearchTool(memoryStore, descriptor.Memory));
            tools.Add(new MemoryGetTool(memoryStore));
        }

        var cronEnabled = descriptor.ToolIds.Count == 0
                          || descriptor.ToolIds.Contains("cron", StringComparer.OrdinalIgnoreCase);
        var hasCronTool = tools.Any(tool => string.Equals(tool.Name, "cron", StringComparison.OrdinalIgnoreCase));
        if (cronEnabled && !hasCronTool)
        {
            var cronStore = _serviceProvider.GetService<ICronStore>();
            var cronScheduler = _serviceProvider.GetService<CronScheduler>();
            if (cronStore is not null && cronScheduler is not null)
            {
                var allowCrossAgentCron = ResolveAllowCrossAgentCron(descriptor);
                tools.Add(new CronTool(cronStore, cronScheduler, descriptor.AgentId, allowCrossAgentCron));
            }
        }

        // Session tool — always available, access level from config
        var sessionStore = _serviceProvider.GetService<ISessionStore>();
        if (sessionStore is not null)
        {
            var (sessionAccessLevel, sessionAllowedAgents) = ResolveSessionAccess(descriptor);
            tools.Add(new SessionTool(sessionStore, descriptor.AgentId, sessionAccessLevel, sessionAllowedAgents));
        }

        var delayToolOptions = _serviceProvider.GetService<IOptions<DelayToolOptions>>() ?? Options.Create(new DelayToolOptions());
        tools.Add(new DelayTool(delayToolOptions));

        var fileWatcherToolOptions = _serviceProvider.GetService<IOptions<FileWatcherToolOptions>>() ?? Options.Create(new FileWatcherToolOptions());
        tools.Add(new FileWatcherTool(fileWatcherToolOptions, pathValidator));

        var subAgentOptions = _serviceProvider.GetService<IOptions<GatewayOptions>>()?.Value.SubAgents;
        var subAgentManager = _serviceProvider.GetService<ISubAgentManager>();
        var isSubAgentSession = context.SessionId.IsSubAgent;
        if (subAgentManager is not null &&
            subAgentOptions is { MaxDepth: > 0 } &&
            !isSubAgentSession)
        {
            var includeSpawn = descriptor.ToolIds.Count == 0
                || descriptor.ToolIds.Contains("spawn_subagent", StringComparer.OrdinalIgnoreCase);
            var includeList = descriptor.ToolIds.Count == 0
                || descriptor.ToolIds.Contains("list_subagents", StringComparer.OrdinalIgnoreCase);
            var includeManage = descriptor.ToolIds.Count == 0
                || descriptor.ToolIds.Contains("manage_subagent", StringComparer.OrdinalIgnoreCase);

            if (includeSpawn)
                tools.Add(new SubAgentSpawnTool(subAgentManager, descriptor.AgentId, context.SessionId));
            if (includeList)
                tools.Add(new SubAgentListTool(subAgentManager, context.SessionId));
            if (includeManage)
                tools.Add(new SubAgentManageTool(subAgentManager, context.SessionId));
        }

        var conversationService = _serviceProvider.GetService<IAgentConversationService>();
        if (conversationService is not null && sessionStore is not null)
        {
            var includeConverse = descriptor.ToolIds.Count == 0
                || descriptor.ToolIds.Contains("agent_converse", StringComparer.OrdinalIgnoreCase);
            if (includeConverse)
                tools.Add(new AgentConverseTool(conversationService, sessionStore, descriptor.AgentId, context.SessionId));
        }

        // TODO: SkillTool is hardcoded here because it needs agent-specific discovery paths.
        // Move to extension loader discovery once extensions can receive per-agent context.
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalSkillsDir = Path.Combine(homeDir, ".botnexus", "skills");
        var agentSkillsDir = Path.Combine(homeDir, ".botnexus", "agents", descriptor.AgentId, "skills");
        var workspaceSkillsDir = Path.Combine(workspacePath, "skills");
        var skillsConfig = ResolveExtensionConfig<BotNexus.Extensions.Skills.SkillsConfig>(descriptor, "botnexus-skills");
        tools.Add(new SkillTool(globalSkillsDir, agentSkillsDir, workspaceSkillsDir, skillsConfig));

        // MCP extension — bridge MCP server tools as native IAgentTool instances.
        // Servers start in the background so the agent responds immediately.
        // Tools are appended to agent.State.Tools as each server comes online.
        McpServerManager? mcpManager = null;
        var mcpConfig = ResolveExtensionConfig<McpExtensionConfig>(descriptor, "botnexus-mcp");
        if (mcpConfig is { Servers.Count: > 0 })
        {
            var mcpLogger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<McpServerManager>();
            mcpManager = new McpServerManager(mcpLogger);
        }

        // MCP Invoke extension — single tool for skill-driven MCP access (lazy server lifecycle)
        McpInvokeTool? mcpInvokeTool = null;
        var mcpInvokeConfig = ResolveExtensionConfig<McpInvokeConfig>(descriptor, "botnexus-mcp-invoke");
        if (mcpInvokeConfig is { Enabled: true, Servers.Count: > 0 })
        {
            var invokeLogger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<McpInvokeTool>();
            mcpInvokeTool = new McpInvokeTool(mcpInvokeConfig, invokeLogger);
            tools.Add(mcpInvokeTool);
        }

        // Web Tools extension — web search and URL fetch
        var webConfig = ResolveExtensionConfig<WebToolsConfig>(descriptor, "botnexus-web");
        if (webConfig is not null)
        {
            var fetchConfig = webConfig.Fetch ?? new WebFetchConfig();
            tools.Add(new WebFetchTool(fetchConfig));

            if (webConfig.Search is { } searchConfig)
            {
                var useCopilotProvider = string.Equals(searchConfig.Provider, "copilot", StringComparison.OrdinalIgnoreCase);
                var hasApiKey = !string.IsNullOrWhiteSpace(searchConfig.ApiKey);

                if (useCopilotProvider || hasApiKey)
                {
                    var copilotApiEndpoint = useCopilotProvider
                        ? ResolveCopilotMcpEndpoint(_authManager.GetApiEndpoint(descriptor.ApiProvider))
                        : null;

                    tools.Add(new WebSearchTool(
                        searchConfig,
                        copilotApiKeyResolver: useCopilotProvider
                            ? ct => _authManager.GetApiKeyAsync(descriptor.ApiProvider, ct)
                            : null,
                        copilotApiEndpoint: copilotApiEndpoint));
                }
            }
        }

        var hookDispatcher = _serviceProvider.GetService<IHookDispatcher>();
        BeforeToolCallDelegate? beforeToolCall = null;
        AfterToolCallDelegate? afterToolCall = null;

        if (hookDispatcher is not null)
        {
            var agentId = descriptor.AgentId;

            beforeToolCall = async (ctx, ct) =>
            {
                var hookEvent = new BeforeToolCallEvent(
                    agentId,
                    ctx.ToolCallRequest.Name,
                    ctx.ToolCallRequest.Id,
                    ctx.ValidatedArgs);

                var results = await hookDispatcher
                    .DispatchAsync<BeforeToolCallEvent, GatewayBeforeToolCallResult>(hookEvent, ct)
                    .ConfigureAwait(false);

                var denied = results.FirstOrDefault(r => r.Denied);
                if (denied is not null)
                {
                    return new AgentCore.Hooks.BeforeToolCallResult(
                        Block: true,
                        Reason: denied.DenyReason);
                }

                return null;
            };

            afterToolCall = async (ctx, ct) =>
            {
                var resultText = ctx.Result.Content.FirstOrDefault()?.ToString();
                var hookEvent = new AfterToolCallEvent(
                    agentId,
                    ctx.ToolCallRequest.Name,
                    ctx.ToolCallRequest.Id,
                    resultText,
                    ctx.IsError);

                await hookDispatcher
                    .DispatchAsync<AfterToolCallEvent, GatewayAfterToolCallResult>(hookEvent, ct)
                    .ConfigureAwait(false);

                return null;
            };
        }

        List<AgentMessage>? initialMessages = null;
        if (context.History.Count > 0)
        {
            // Only inject user and assistant messages from history. Tool-role entries
            // become orphaned ToolResultMessages (no matching tool_use in the preceding
            // assistant message) which causes the LLM provider to reject the conversation.
            // System entries are also excluded — the agent's system prompt is set separately.
            initialMessages = context.History
                .Where(e => e.Role == Domain.Primitives.MessageRole.User
                         || e.Role == Domain.Primitives.MessageRole.Assistant)
                .Select(ConvertSessionEntryToAgentMessage)
                .ToList();

            _logger.LogInformation(
                "Injecting {Count} history messages (of {Total} entries) into agent context for session '{SessionId}'",
                initialMessages.Count, context.History.Count, context.SessionId);
        }

        var options = new AgentOptions(
            InitialState: new AgentInitialState(
                SystemPrompt: enrichedSystemPrompt,
                Model: model,
                Tools: tools,
                Messages: initialMessages),
            Model: model,
            LlmClient: _llmClient,
            ConvertToLlm: null,
            TransformContext: null,
            GetApiKey: (provider, cancellationToken) => _authManager.GetApiKeyAsync(provider, cancellationToken),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Parallel,
            BeforeToolCall: beforeToolCall,
            AfterToolCall: afterToolCall,
            GenerationSettings: new SimpleStreamOptions(),
            SteeringMode: QueueMode.All,
            FollowUpMode: QueueMode.All,
            SessionId: context.SessionId);

        var agent = new Agent(options);
        IAgentHandle handle = new InProcessAgentHandle(
            agent,
            descriptor.AgentId,
            context.SessionId,
            _logger,
            mcpManager,
            mcpInvokeTool,
            tools);

        _logger.LogWarning(
            "Created agent handle for '{AgentId}' session '{SessionId}' with {ToolCount} tools: {ToolNames}",
            descriptor.AgentId, context.SessionId, tools.Count,
            string.Join(", ", tools.Select(t => t.Name)));

        // Start MCP servers in background — tools become available progressively
        if (mcpManager is not null && mcpConfig is { Servers.Count: > 0 })
        {
            _ = StartMcpServersInBackgroundAsync(agent, mcpManager, mcpConfig, descriptor.AgentId, context.SessionId);
        }

        return handle;
    }

    /// <summary>
    /// Starts MCP servers in the background and appends their tools to the agent
    /// as each server comes online. Each server connects independently — a slow or
    /// failed server does not block other servers from providing their tools.
    /// </summary>
    private async Task StartMcpServersInBackgroundAsync(
        Agent agent,
        McpServerManager mcpManager,
        McpExtensionConfig mcpConfig,
        AgentId agentId,
        SessionId sessionId)
    {
        _logger.LogInformation(
            "Starting {Count} MCP server(s) in background for '{AgentId}' session '{SessionId}'",
            mcpConfig.Servers.Count, agentId, sessionId);

        // Start each server independently so fast servers provide tools immediately
        // while slow servers continue connecting in the background.
        var tasks = mcpConfig.Servers.Select(kvp =>
            StartSingleMcpServerAsync(agent, mcpManager, mcpConfig, kvp.Key, kvp.Value, agentId));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task StartSingleMcpServerAsync(
        Agent agent,
        McpServerManager mcpManager,
        McpExtensionConfig mcpConfig,
        string serverId,
        McpServerConfig serverConfig,
        AgentId agentId)
    {
        try
        {
            var tools = await mcpManager.StartSingleServerAsync(serverId, serverConfig, mcpConfig.ToolPrefix, CancellationToken.None)
                .ConfigureAwait(false);

            if (tools.Count > 0)
            {
                agent.State.Tools = [.. agent.State.Tools, .. tools];
                _logger.LogInformation(
                    "MCP server '{ServerId}' loaded {ToolCount} tool(s) for '{AgentId}'. Agent now has {TotalTools} tools.",
                    serverId, tools.Count, agentId, agent.State.Tools.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MCP server '{ServerId}' failed for '{AgentId}'. Agent continues without its tools.",
                serverId, agentId);
        }
    }

    private static bool ResolveAllowCrossAgentCron(AgentDescriptor descriptor)
    {
        if (!descriptor.Metadata.TryGetValue("allowCrossAgentCron", out var raw) || raw is null)
            return false;

        return raw switch
        {
            bool value => value,
            string value when bool.TryParse(value, out var parsed) => parsed,
            _ => false
        };
    }

    private static (SessionAccessLevel level, IReadOnlyList<string>? allowedAgents) ResolveSessionAccess(AgentDescriptor descriptor)
    {
        var level = (descriptor.SessionAccessLevel ?? "own").ToLowerInvariant() switch
        {
            "all" => SessionAccessLevel.All,
            "allowlist" => SessionAccessLevel.Allowlist,
            _ => SessionAccessLevel.Own
        };

        var allowed = descriptor.SessionAllowedAgents is { Count: > 0 }
            ? descriptor.SessionAllowedAgents
            : null;

        // If sub-agents are configured, automatically include them in the allowlist
        if (descriptor.SubAgentIds is { Count: > 0 } && level != SessionAccessLevel.All)
        {
            var combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (allowed is not null)
                foreach (var a in allowed) combined.Add(a);
            foreach (var s in descriptor.SubAgentIds) combined.Add(s);

            if (combined.Count > 0)
            {
                level = SessionAccessLevel.Allowlist;
                allowed = combined.ToList();
            }
        }

        return (level, allowed);
    }

    private static T? ResolveExtensionConfig<T>(AgentDescriptor descriptor, string extensionId) where T : class
    {
        if (descriptor.ExtensionConfig.TryGetValue(extensionId, out var element))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(element.GetRawText());
            }
            catch { /* invalid config — use defaults */ }
        }

        return null;
    }

    private static AgentMessage ConvertSessionEntryToAgentMessage(SessionEntry entry)
    {
        return entry.Role.Value switch
        {
            "user" => new AgentCoreUserMessage(entry.Content),
            "assistant" => new AssistantAgentMessage(entry.Content),
            "system" => new SystemAgentMessage(entry.Content),
            "tool" => new ToolResultAgentMessage(
                entry.ToolCallId ?? string.Empty,
                entry.ToolName ?? "tool",
                new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, entry.Content)])),
            _ => new AgentCoreUserMessage(entry.Content)
        };
    }

    private static string ResolveCopilotMcpEndpoint(string? baseEndpoint)
    {
        const string fallbackEndpoint = "https://api.githubcopilot.com/mcp";
        if (string.IsNullOrWhiteSpace(baseEndpoint))
            return fallbackEndpoint;

        if (Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var absoluteUri))
        {
            var path = absoluteUri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
                return absoluteUri.ToString().TrimEnd('/');

            var builder = new UriBuilder(absoluteUri)
            {
                Path = string.IsNullOrEmpty(path) || path == "/" ? "/mcp" : $"{path}/mcp"
            };

            return builder.Uri.ToString().TrimEnd('/');
        }

        var trimmed = baseEndpoint.TrimEnd('/');
        return trimmed.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/mcp";
    }
}

/// <summary>
/// Agent handle that wraps an in-process <see cref="BotNexus.AgentCore.Agent"/> instance.
/// </summary>
internal sealed class InProcessAgentHandle : IAgentHandle, IHealthCheckable, IAgentHandleInspector
{
    private readonly Agent _agent;
    private readonly ILogger _logger;
    private readonly McpServerManager? _mcpManager;
    private readonly McpInvokeTool? _mcpInvokeTool;
    private readonly IReadOnlyList<object> _disposableTools;
    private readonly IReadOnlyDictionary<string, IAgentTool> _toolsByName;

    public InProcessAgentHandle(
        Agent agent,
        AgentId agentId,
        SessionId sessionId,
        ILogger logger,
        McpServerManager? mcpManager = null,
        McpInvokeTool? mcpInvokeTool = null,
        IReadOnlyList<IAgentTool>? tools = null)
    {
        _agent = agent;
        AgentId = agentId;
        SessionId = sessionId;
        _logger = logger;
        _mcpManager = mcpManager;
        _mcpInvokeTool = mcpInvokeTool;
        _disposableTools = tools?
            .Where(tool => !ReferenceEquals(tool, mcpInvokeTool))
            .Where(static tool => tool is IAsyncDisposable || tool is IDisposable)
            .Cast<object>()
            .ToList()
            ?? [];
        _toolsByName = (tools ?? [])
            .GroupBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public AgentId AgentId { get; }

    /// <inheritdoc />
    public SessionId SessionId { get; }

    /// <inheritdoc />
    public bool IsRunning => _agent.Status == AgentStatus.Running;

    /// <inheritdoc />
    public IAgentHandle? GetHandle(AgentId agentId, SessionId sessionId)
        => string.Equals(AgentId, agentId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
            ? this
            : null;

    /// <inheritdoc />
    public IAgentTool? ResolveTool(AgentId agentId, SessionId sessionId, string toolName)
    {
        if (!string.Equals(AgentId, agentId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        return _toolsByName.TryGetValue(toolName, out var tool) ? tool : null;
    }

    /// <inheritdoc />
    public ContextDiagnostics? GetContextDiagnostics()
    {
        var state = _agent.State;
        var systemPromptChars = state.SystemPrompt?.Length ?? 0;
        var toolDefinitions = state.Tools
            .Select(static t => new ToolDiagInfo(
                t.Name,
                t.Definition.Description,
                t.Definition.Parameters.GetRawText().Length))
            .ToList();
        var historyEntries = state.Messages.Count;
        var historyChars = state.Messages.Sum(static message => message switch
        {
            AgentCoreUserMessage user => user.Content?.Length ?? 0,
            AssistantAgentMessage assistant => assistant.Content?.Length ?? 0,
            SystemAgentMessage system => system.Content?.Length ?? 0,
            ToolResultAgentMessage tool => tool.Result.Content.Sum(static c => c.Value?.Length ?? 0),
            SubAgentCompletionMessage subAgent => subAgent.Content?.Length ?? 0,
            _ => 0
        });

        var totalChars = systemPromptChars
            + toolDefinitions.Sum(static t => t.SchemaChars + t.Name.Length + (t.Description?.Length ?? 0))
            + historyChars;
        var estimatedTokens = totalChars / 4;

        return new ContextDiagnostics
        {
            SystemPromptChars = systemPromptChars,
            SystemPromptTokens = systemPromptChars / 4,
            ToolCount = state.Tools.Count,
            ToolDefinitionChars = toolDefinitions.Sum(static t => t.SchemaChars),
            ToolDefinitionTokens = toolDefinitions.Sum(static t => t.SchemaChars) / 4,
            Tools = toolDefinitions,
            HistoryEntryCount = historyEntries,
            HistoryChars = historyChars,
            HistoryTokens = historyChars / 4,
            TotalEstimatedTokens = estimatedTokens,
            SystemPrompt = state.SystemPrompt
        };
    }

    /// <inheritdoc />
    public async Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
    {
        using var activity = AgentDiagnostics.Source.StartActivity("agent.prompt", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", AgentId);
        activity?.SetTag("botnexus.session.id", SessionId);
        activity?.SetTag("botnexus.correlation.id", System.Diagnostics.Activity.Current?.TraceId.ToString());
        try
        {
            var messages = await _agent.PromptAsync(message, cancellationToken);
            var lastAssistant = messages.OfType<AssistantAgentMessage>().LastOrDefault();

            var response = new AgentResponse
            {
                Content = lastAssistant?.Content ?? string.Empty,
                Usage = lastAssistant?.Usage is { } u ? new AgentResponseUsage(u.InputTokens, u.OutputTokens) : null,
                ToolCalls = messages.OfType<ToolResultAgentMessage>()
                    .Select(t => new AgentToolCallInfo(t.ToolCallId, t.ToolName, t.IsError))
                    .ToList()
            };

            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = AgentDiagnostics.Source.StartActivity("agent.stream", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", AgentId);
        activity?.SetTag("botnexus.session.id", SessionId);
        activity?.SetTag("botnexus.correlation.id", System.Diagnostics.Activity.Current?.TraceId.ToString());

        var messageId = Guid.NewGuid().ToString("N");
        var events = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();
        using var promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using var subscription = _agent.Subscribe(async (agentEvent, cancellationToken) =>
        {
            try
            {
                var streamEvent = agentEvent switch
                {
                    MessageStartEvent start when start.Message is AssistantAgentMessage
                        => new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = messageId },
                    MessageUpdateEvent update when update.ContentDelta is not null => update.IsThinking
                        ? new AgentStreamEvent
                        {
                            Type = AgentStreamEventType.ThinkingDelta,
                            ThinkingContent = update.ContentDelta,
                            MessageId = messageId
                        }
                        : new AgentStreamEvent
                        {
                            Type = AgentStreamEventType.ContentDelta,
                            ContentDelta = update.ContentDelta,
                            MessageId = messageId
                        },
                    ToolExecutionStartEvent toolStart => new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ToolStart,
                        ToolCallId = toolStart.ToolCallId,
                        ToolName = toolStart.ToolName,
                        ToolArgs = toolStart.Args,
                        MessageId = messageId
                    },
                    ToolExecutionEndEvent toolEnd => new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ToolEnd,
                        ToolCallId = toolEnd.ToolCallId,
                        ToolName = toolEnd.ToolName,
                        ToolResult = toolEnd.Result.Content.FirstOrDefault()?.ToString(),
                        ToolIsError = toolEnd.IsError,
                        MessageId = messageId
                    },
                    MessageEndEvent end when end.Message is AssistantAgentMessage
                        => new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, MessageId = messageId },
                    _ => null
                };

                if (streamEvent is not null)
                    await events.Writer.WriteAsync(streamEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing agent event in stream for '{AgentId}' session '{SessionId}'", AgentId, SessionId);
                try
                {
                    await events.Writer.WriteAsync(new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.Error,
                        ErrorMessage = $"Internal streaming error: {ex.Message}",
                        MessageId = messageId
                    }, cancellationToken);
                }
                catch
                {
                    // Best-effort error notification.
                }

                events.Writer.TryComplete(ex);
            }
        });

        async Task RunPromptAsync()
        {
            try
            {
                await _agent.PromptAsync(message, promptCancellation.Token);
            }
            catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Agent prompt cancelled for '{AgentId}' session '{SessionId}'", AgentId, SessionId);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Agent prompt failed for '{AgentId}' session '{SessionId}'", AgentId, SessionId);
                try
                {
                    await events.Writer.WriteAsync(new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.Error,
                        ErrorMessage = $"Agent prompt failed: {ex.Message}",
                        MessageId = messageId
                    }, CancellationToken.None);
                }
                catch
                {
                    // Best-effort error notification.
                }

                events.Writer.TryComplete(ex);
                return;
            }
            activity?.SetStatus(ActivityStatusCode.Ok);
            events.Writer.TryComplete();
        }

        var promptTask = RunPromptAsync();

        try
        {
            await foreach (var evt in events.Reader.ReadAllAsync(cancellationToken))
                yield return evt;
        }
        finally
        {
            promptCancellation.Cancel();

            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _agent.AbortAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error aborting agent after stream cancellation for '{AgentId}' session '{SessionId}'", AgentId, SessionId);
                }
            }

            try
            {
                await promptTask;
            }
            catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                // Expected when caller cancels stream.
            }
        }
    }

    /// <inheritdoc />
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        await _agent.AbortAsync();
    }

    /// <inheritdoc />
    public Task SteerAsync(string message, CancellationToken cancellationToken = default)
    {
        _agent.Steer(new AgentCoreUserMessage(message));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task FollowUpAsync(string message, CancellationToken cancellationToken = default)
    {
        _agent.FollowUp(new AgentCoreUserMessage(message));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task FollowUpAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        _agent.FollowUp(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> PingAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_agent.Status != AgentStatus.Aborting);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _agent.AbortAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error aborting agent during dispose"); }

        foreach (var tool in _disposableTools)
        {
            if (tool is IAsyncDisposable asyncDisposable)
            {
                try { await asyncDisposable.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disposing async tool {ToolType}", tool.GetType().Name); }
                continue;
            }

            if (tool is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disposing tool {ToolType}", tool.GetType().Name); }
            }
        }

        if (_mcpManager is not null)
        {
            try { await _mcpManager.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing MCP server manager"); }
        }

        if (_mcpInvokeTool is not null)
        {
            try { await _mcpInvokeTool.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing MCP invoke tool"); }
        }
    }
}
