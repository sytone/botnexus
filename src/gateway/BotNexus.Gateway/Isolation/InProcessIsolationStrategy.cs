using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Diagnostics;
using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Types;
using BotNexus.Cron;
using BotNexus.Cron.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Memory;
using BotNexus.Memory.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentCoreUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using GatewayBeforeToolCallResult = BotNexus.Gateway.Abstractions.Hooks.BeforeToolCallResult;
using GatewayAfterToolCallResult = BotNexus.Gateway.Abstractions.Hooks.AfterToolCallResult;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// In-process isolation strategy — runs the agent directly inside the Gateway process
/// by wrapping <see cref="BotNexus.Agent.Core.Agent"/>. No security boundary: the agent
/// shares memory, file handles, and OS identity with the Gateway and can reach anything
/// the Gateway can reach.
/// </summary>
/// <remarks>
/// The default and fastest strategy. Appropriate for development, testing, and trusted
/// single-user deployments where the operator and the agent are in the same trust domain.
/// For untrusted agents, multi-tenant hosts, or workloads that handle data the user must
/// not leak, choose <c>sandbox</c>, <c>container</c>, or <c>remote</c> instead.
/// </remarks>
public sealed class InProcessIsolationStrategy : IIsolationStrategy
{
    private readonly LlmClient _llmClient;
    private readonly GatewayAuthManager _authManager;
    private readonly IContextBuilder _contextBuilder;
    private readonly IAgentToolFactory _toolFactory;
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IToolRegistry _toolRegistry;
    private readonly IEnumerable<IAgentToolContributor> _toolContributors;
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
        IEnumerable<IAgentToolContributor> toolContributors,
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
        _toolContributors = toolContributors;
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

        var enrichedSystemPrompt = await _contextBuilder.BuildSystemPromptAsync(descriptor, context, cancellationToken);

        var workspacePath = _workspaceManager.GetWorkspacePath(descriptor.AgentId.Value);
        var pathValidator = new DefaultPathValidator(descriptor.FileAccess, workspacePath);
        var workspaceTools = _toolFactory.CreateTools(workspacePath, pathValidator);
        var workspaceToolNames = new HashSet<string>(workspaceTools.Select(tool => tool.Name), StringComparer.OrdinalIgnoreCase);

        // Normalise toolIds: ["*"] is a user-friendly alias for [] (all tools).
        var effectiveToolIds = IsWildcardToolIds(descriptor.ToolIds)
            ? (IReadOnlyList<string>)[]
            : descriptor.ToolIds;

        IReadOnlyList<IAgentTool> selectedWorkspaceTools = effectiveToolIds.Count > 0
            ? [.. workspaceTools.Where(tool => effectiveToolIds.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))]
            : workspaceTools;

        var extensionTools = effectiveToolIds.Count > 0
            ? _toolRegistry.ResolveTools(effectiveToolIds)
            : _toolRegistry.GetAll();

        var tools = selectedWorkspaceTools
            .Concat(extensionTools.Where(tool => !workspaceToolNames.Contains(tool.Name)))
            .ToList();

        _logger.LogInformation(
            "Tool setup for '{AgentId}': workspace={WorkspaceCount} extension={ExtCount} total={Total} toolIds={ToolIdCount} workspace={WorkspacePath}",
            descriptor.AgentId, workspaceTools.Count, extensionTools.Count(), tools.Count,
            effectiveToolIds.Count, workspacePath);

        if (descriptor.Memory?.Enabled == true)
        {
            var memoryStore = _memoryStoreFactory.Create(descriptor.AgentId.Value);
            // Initialize asynchronously ΓÇö don't block handle creation.
            // Memory tools work immediately; the store initializes in the background.
            _ = memoryStore.InitializeAsync(CancellationToken.None);
            tools.Add(new MemorySaveTool(_workspaceManager, descriptor.AgentId.Value, descriptor.Memory.Path));
            tools.Add(new MemorySearchTool(memoryStore, descriptor.Memory));
            tools.Add(new MemoryGetTool(memoryStore));
        }

        var cronEnabled = effectiveToolIds.Count == 0
                          || effectiveToolIds.Contains("cron", StringComparer.OrdinalIgnoreCase);
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

        // Session tool ΓÇö always available, access level from config
        var sessionStore = _serviceProvider.GetService<ISessionStore>();
        if (sessionStore is not null)
        {
            var (sessionAccessLevel, sessionAllowedAgents) = ResolveSessionAccess(descriptor);
            tools.Add(new SessionTool(sessionStore, descriptor.AgentId, sessionAccessLevel, sessionAllowedAgents));
        }

        var conversationStore = _serviceProvider.GetService<IConversationStore>();
        if (conversationStore is not null)
        {
            var conversationId = await ResolveConversationIdAsync(conversationStore, sessionStore, descriptor.AgentId, context.SessionId, cancellationToken)
                .ConfigureAwait(false);
            var (conversationAccessLevel, conversationAllowedAgents) = ResolveConversationAccess(descriptor);
            var conversationDispatcher = _serviceProvider.GetService<IChannelDispatcher>();
            tools.Add(new ConversationTool(
                conversationStore,
                descriptor.AgentId,
                conversationId,
                conversationAccessLevel,
                conversationAllowedAgents,
                sessionStore,
                conversationDispatcher));
        }

        var includeAskUser = effectiveToolIds.Count == 0
            || effectiveToolIds.Contains("ask_user", StringComparer.OrdinalIgnoreCase);
        var askUserRegistry = _serviceProvider.GetService<IAskUserResponseRegistry>();
        if (includeAskUser && askUserRegistry is not null)
        {
            var askUserConversationId = conversationStore is not null
                ? await ResolveConversationIdAsync(conversationStore, sessionStore, descriptor.AgentId, context.SessionId, cancellationToken).ConfigureAwait(false)
                : null;
            tools.Add(new AskUserTool(
                askUserRegistry,
                descriptor.AgentId,
                context.SessionId,
                askUserConversationId));
        }
        var delayToolOptions = _serviceProvider.GetService<IOptions<DelayToolOptions>>() ?? Options.Create(new DelayToolOptions());
        tools.Add(new DelayTool(delayToolOptions));
        var platformConfig = _serviceProvider.GetService<IOptions<PlatformConfig>>();
        var serverTimezone = platformConfig?.Value.Gateway?.DefaultTimezone;
        tools.Add(new DateTimeTool(descriptor.Soul?.Timezone ?? serverTimezone));

        var fileWatcherToolOptions = _serviceProvider.GetService<IOptions<FileWatcherToolOptions>>() ?? Options.Create(new FileWatcherToolOptions());
        tools.Add(new FileWatcherTool(fileWatcherToolOptions, pathValidator));

        var subAgentOptions = _serviceProvider.GetService<IOptions<GatewayOptions>>()?.Value.SubAgents;
        var subAgentManager = _serviceProvider.GetService<ISubAgentManager>();
        // Phase 5 / F-6 part 1: primary signal is the typed descriptor.Kind (AgentKind.SubAgent
        // is set exactly once by DefaultSubAgentManager.SpawnAsync). The SessionId.IsSubAgent
        // substring check is retained as defense-in-depth so the gate fails CLOSED if a future
        // path registers a sub-agent descriptor without going through SpawnAsync (or if a
        // legacy ::subagent:: session is replayed against a Kind-defaulted descriptor). The
        // architecture fence in AgentKindArchitectureTests deliberately allowlists this file
        // as the one production callsite of SessionId.IsSubAgent outside the legacy
        // SessionStoreBase read-path bucketing.
        var isSubAgentSession =
            descriptor.Kind == AgentKind.SubAgent
            || context.SessionId.IsSubAgent;

        // Defense-in-depth observability: if the typed and substring signals disagree,
        // an invariant has drifted (a sub-agent descriptor was registered without
        // Kind = SubAgent, or a sub-agent SessionId was attached to a Named descriptor).
        // Either case means a future migration removed the OR fallback would break this
        // call. Log at Warning so operators can alert on it.
        if (descriptor.Kind == AgentKind.SubAgent && !context.SessionId.IsSubAgent)
        {
            _logger.LogWarning(
                "Isolation gate: descriptor.Kind=SubAgent but SessionId '{SessionId}' is not a sub-agent shape " +
                "for agent '{AgentId}'. Spawn tools will be blocked (correct), but this indicates an invariant " +
                "drift — typed and substring signals must agree.",
                context.SessionId,
                descriptor.AgentId);
        }
        else if (descriptor.Kind != AgentKind.SubAgent && context.SessionId.IsSubAgent)
        {
            _logger.LogWarning(
                "Isolation gate: SessionId '{SessionId}' is a sub-agent shape but descriptor.Kind={Kind} for " +
                "agent '{AgentId}'. The substring fallback is correctly blocking spawn tools, but the typed " +
                "signal should also be SubAgent — this indicates the descriptor was registered outside of " +
                "DefaultSubAgentManager.SpawnAsync.",
                context.SessionId,
                descriptor.Kind,
                descriptor.AgentId);
        }
        if (subAgentManager is not null &&
            subAgentOptions is { MaxDepth: > 0 } &&
            !isSubAgentSession)
        {
            var includeSpawn = effectiveToolIds.Count == 0
                || effectiveToolIds.Contains("spawn_subagent", StringComparer.OrdinalIgnoreCase);
            var includeList = effectiveToolIds.Count == 0
                || effectiveToolIds.Contains("list_subagents", StringComparer.OrdinalIgnoreCase);
            var includeManage = effectiveToolIds.Count == 0
                || effectiveToolIds.Contains("manage_subagent", StringComparer.OrdinalIgnoreCase);
            if (includeSpawn)
            {
                var spawnConversationId = conversationStore is not null
                    ? await ResolveConversationIdAsync(conversationStore, sessionStore, descriptor.AgentId, context.SessionId, cancellationToken).ConfigureAwait(false)
                    : null;
                if (spawnConversationId is { } resolvedSpawnConversationId)
                {
                    tools.Add(new SubAgentSpawnTool(subAgentManager, descriptor.AgentId, context.SessionId, resolvedSpawnConversationId));
                }
                else
                {
                    _logger.LogInformation(
                        "Skipping spawn_subagent tool for session '{SessionId}' (agent '{AgentId}'): no conversation is bound to this session. " +
                        "Sub-agent sessions must inherit a parent conversation to remain visible in the portal thread.",
                        context.SessionId,
                        descriptor.AgentId);
                }
            }
            if (includeList)
                tools.Add(new SubAgentListTool(subAgentManager, context.SessionId));
            if (includeManage)
                tools.Add(new SubAgentManageTool(subAgentManager, context.SessionId));
        }

        var conversationService = _serviceProvider.GetService<IAgentExchangeService>();
        if (conversationService is not null && sessionStore is not null)
        {
            var includeConverse = effectiveToolIds.Count == 0
                || effectiveToolIds.Contains("agent_converse", StringComparer.OrdinalIgnoreCase);
            if (includeConverse)
                tools.Add(new AgentConverseTool(conversationService, sessionStore, descriptor.AgentId, context.SessionId));
        }

        // Phase 8 (F-11): register finish_agent_exchange when this is an agent-to-agent session.
        // Bypasses effectiveToolIds because this is a system control tool, not an agent-configured
        // capability — without it the substring-based completion heuristic that issue #379 patched
        // around would have nothing to replace it.
        if (sessionStore is not null)
        {
            var sessionForFinishTool = await sessionStore.GetAsync(context.SessionId, cancellationToken).ConfigureAwait(false);
            if (sessionForFinishTool is not null && sessionForFinishTool.SessionType == SessionType.AgentAgent)
            {
                tools.Add(new FinishAgentExchangeTool(sessionStore, context.SessionId));
            }
        }

        var agentRegistry = _serviceProvider.GetService<IAgentRegistry>();
        if (agentRegistry is not null)
        {
            var includeListAgents = effectiveToolIds.Count == 0
                || effectiveToolIds.Contains("list_agents", StringComparer.OrdinalIgnoreCase);
            if (includeListAgents)
                tools.Add(new ListAgentsTool(agentRegistry, descriptor.AgentId));
        }

        var includeCanvas = effectiveToolIds.Count == 0
                            || effectiveToolIds.Contains("canvas", StringComparer.OrdinalIgnoreCase);
        if (includeCanvas)
        {
            var canvasNotifiers = _serviceProvider.GetServices<IAgentCanvasNotifier>().ToArray();
            ConversationId? canvasConversationId = null;
            var canvasConvStore = _serviceProvider.GetService<IConversationStore>();
            if (canvasConvStore is not null)
            {
                canvasConversationId = await ResolveConversationIdAsync(canvasConvStore, sessionStore, descriptor.AgentId, context.SessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            tools.Add(new CanvasTool(descriptor.AgentId, canvasConversationId, canvasNotifiers));
        }

        List<object> extensionResourcesToDispose = [];
        var toolContributionContext = new AgentToolContributionContext(
            descriptor,
            context,
            workspacePath,
            pathValidator,
            provider => _authManager.GetApiEndpoint(provider),
            (provider, ct) => _authManager.GetApiKeyAsync(provider, ct));

        foreach (var contributor in _toolContributors)
        {
            var contribution = await contributor.ContributeAsync(toolContributionContext, cancellationToken).ConfigureAwait(false);
            if (contribution.Tools.Count > 0)
                tools.AddRange(contribution.Tools);
            if (contribution.ResourcesToDispose is { Count: > 0 })
                extensionResourcesToDispose.AddRange(contribution.ResourcesToDispose);
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
                    return new BotNexus.Agent.Core.Hooks.BeforeToolCallResult(
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
            // The cold-start resume projection — what survives a session hydration without
            // breaking the LLM provider — is owned by SessionContextProjector. Tool entries
            // are dropped there because Anthropic rejects orphaned tool_result blocks
            // (the Assistant SessionEntry persists response text but not the paired
            // tool_use). Phase 3a/#531 added IsHistory; Phase 3b/#534 centralised the
            // filter so all isolation strategies share it.
            initialMessages = SessionContextProjector
                .ProjectForResume(context.History)
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
            SessionId: context.SessionId.Value,
            ToolTimeout: ResolveToolTimeout(descriptor));

        var agent = new BotNexus.Agent.Core.Agent(options);
        IAgentHandle handle = new InProcessAgentHandle(
            agent,
            descriptor.AgentId,
            context.SessionId,
            _logger,
            tools,
            extensionResourcesToDispose);

        _logger.LogWarning(
            "Created agent handle for '{AgentId}' session '{SessionId}' with {ToolCount} tools: {ToolNames}",
            descriptor.AgentId, context.SessionId, tools.Count,
            string.Join(", ", tools.Select(t => t.Name)));

        return handle;
    }

    /// <summary>
    /// Returns true when <paramref name="toolIds"/> represents the all-tools wildcard ΓÇö either an
    /// empty list (legacy behaviour) or a list whose sole entry is <c>"*"</c> (intuitive form).
    /// </summary>
    private static bool IsWildcardToolIds(IReadOnlyList<string> toolIds)
        => toolIds.Count == 0 || (toolIds.Count == 1 && toolIds[0] == "*");
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

    private TimeSpan? ResolveToolTimeout(AgentDescriptor descriptor)
    {
        if (!descriptor.Metadata.TryGetValue("toolTimeoutSeconds", out var raw) || raw is null)
            return null;

        if (TryConvertPositiveSeconds(raw, out var seconds))
        {
            _logger.LogDebug("Applying tool timeout for '{AgentId}': {ToolTimeoutSeconds}s", descriptor.AgentId, seconds);
            return TimeSpan.FromSeconds(seconds);
        }

        _logger.LogWarning(
            "Ignoring invalid tool timeout metadata for '{AgentId}'. Expected positive seconds but got '{ToolTimeoutSecondsRaw}'.",
            descriptor.AgentId,
            raw);
        return null;
    }

    private static bool TryConvertPositiveSeconds(object raw, out int seconds)
    {
        seconds = 0;
        var parsed = raw switch
        {
            int value => value,
            long value when value <= int.MaxValue => (int)value,
            double value when value <= int.MaxValue && value == Math.Truncate(value) => (int)value,
            string value when int.TryParse(value, out var parsedValue) => parsedValue,
            JsonElement { ValueKind: JsonValueKind.Number } jsonNumber when jsonNumber.TryGetInt32(out var parsedValue) => parsedValue,
            JsonElement { ValueKind: JsonValueKind.String } jsonString when int.TryParse(jsonString.GetString(), out var parsedValue) => parsedValue,
            _ => -1
        };

        if (parsed <= 0)
            return false;

        seconds = parsed;
        return true;
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

    private static (ConversationAccessLevel level, IReadOnlyList<string>? allowedAgents) ResolveConversationAccess(AgentDescriptor descriptor)
    {
        var level = (descriptor.ConversationAccessLevel ?? "own").ToLowerInvariant() switch
        {
            "all" => ConversationAccessLevel.All,
            "allowlist" => ConversationAccessLevel.Allowlist,
            _ => ConversationAccessLevel.Own
        };

        var allowed = descriptor.ConversationAllowedAgents is { Count: > 0 }
            ? descriptor.ConversationAllowedAgents
            : null;

        if (descriptor.SubAgentIds is { Count: > 0 } && level != ConversationAccessLevel.All)
        {
            var combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (allowed is not null)
                foreach (var agent in allowed) combined.Add(agent);
            foreach (var subAgentId in descriptor.SubAgentIds) combined.Add(subAgentId);

            if (combined.Count > 0)
            {
                level = ConversationAccessLevel.Allowlist;
                allowed = combined.ToList();
            }
        }

        return (level, allowed);
    }

    private static async Task<ConversationId?> ResolveConversationIdAsync(
        IConversationStore conversationStore,
        ISessionStore? sessionStore,
        AgentId agentId,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionStore is not null)
        {
            var session = await sessionStore.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session?.Session.ConversationId is { } conversationId)
                return conversationId;
        }

        var conversations = await conversationStore.ListAsync(agentId, cancellationToken).ConfigureAwait(false);
        return conversations.FirstOrDefault(conversation => conversation.ActiveSessionId == sessionId)?.ConversationId;
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

}

/// <summary>
/// Agent handle that wraps an in-process <see cref="BotNexus.Agent.Core.Agent"/> instance.
/// </summary>
internal sealed class InProcessAgentHandle : IAgentHandle, IHealthCheckable, IAgentHandleInspector
{
    private readonly BotNexus.Agent.Core.Agent _agent;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<object> _disposableResources;
    private readonly IReadOnlyDictionary<string, IAgentTool> _toolsByName;

    public InProcessAgentHandle(
        BotNexus.Agent.Core.Agent agent,
        AgentId agentId,
        SessionId sessionId,
        ILogger logger,
        IReadOnlyList<IAgentTool>? tools = null,
        IReadOnlyList<object>? resourcesToDispose = null)
    {
        _agent = agent;
        AgentId = agentId;
        SessionId = sessionId;
        _logger = logger;
        _disposableResources = (tools ?? [])
            .Where(static tool => tool is IAsyncDisposable || tool is IDisposable)
            .Cast<object>()
            .Concat((resourcesToDispose ?? [])
                .Where(static resource => resource is IAsyncDisposable || resource is IDisposable))
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToList();
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
        => string.Equals(AgentId.Value, agentId.Value, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(SessionId.Value, sessionId.Value, StringComparison.OrdinalIgnoreCase)
            ? this
            : null;

    /// <inheritdoc />
    public IAgentTool? ResolveTool(AgentId agentId, SessionId sessionId, string toolName)
    {
        if (!string.Equals(AgentId.Value, agentId.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SessionId.Value, sessionId.Value, StringComparison.OrdinalIgnoreCase) ||
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

        var userAssistantChars = state.Messages.Sum(static message => message switch
        {
            AgentCoreUserMessage user => user.Content?.Length ?? 0,
            AssistantAgentMessage assistant => assistant.Content?.Length ?? 0,
            SystemAgentMessage system => system.Content?.Length ?? 0,
            SubAgentCompletionMessage subAgent => subAgent.Content?.Length ?? 0,
            _ => 0
        });

        var toolResultChars = state.Messages.Sum(static message => message switch
        {
            ToolResultAgentMessage tool => tool.Result.Content.Sum(static c => c.Value?.Length ?? 0),
            _ => 0
        });

        var historyChars = userAssistantChars + toolResultChars;
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
            UserAssistantChars = userAssistantChars,
            UserAssistantTokens = userAssistantChars / 4,
            ToolResultChars = toolResultChars,
            ToolResultTokens = toolResultChars / 4,
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
    public async Task<AgentResponse> PromptAsync(AgentCoreUserMessage message, CancellationToken cancellationToken = default)
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
    public IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, CancellationToken cancellationToken = default)
        => StreamCoreAsync(ct => _agent.PromptAsync(message, ct), cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentCoreUserMessage message, CancellationToken cancellationToken = default)
        => StreamCoreAsync(ct => _agent.PromptAsync(message, ct), cancellationToken);

    private async IAsyncEnumerable<AgentStreamEvent> StreamCoreAsync(
        Func<CancellationToken, Task> runPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
                    TurnEndEvent
                        => new AgentStreamEvent { Type = AgentStreamEventType.TurnEnd, MessageId = messageId },
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
                await runPrompt(promptCancellation.Token);
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

        foreach (var resource in _disposableResources)
        {
            if (resource is IAsyncDisposable asyncDisposable)
            {
                try { await asyncDisposable.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disposing async resource {ResourceType}", resource.GetType().Name); }
                continue;
            }

            if (resource is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disposing resource {ResourceType}", resource.GetType().Name); }
            }
        }
    }
}


