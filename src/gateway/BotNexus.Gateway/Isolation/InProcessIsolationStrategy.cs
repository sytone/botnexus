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
using BotNexus.Agent.Providers.Core.Resolution;
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
using BotNexus.Gateway.Dispatching;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Telemetry;
using BotNexus.Gateway.Tools;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Contracts.Memory;
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
    private readonly IAgentMemoryFactory _agentMemoryFactory;
    private readonly ISharedMemoryStoreRegistry? _sharedMemoryRegistry;
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
        IAgentMemoryFactory agentMemoryFactory,
        IServiceProvider serviceProvider,
        ILogger<InProcessIsolationStrategy> logger,
        ISharedMemoryStoreRegistry? sharedMemoryRegistry = null)
    {
        _llmClient = llmClient;
        _authManager = authManager;
        _contextBuilder = contextBuilder;
        _toolFactory = toolFactory;
        _workspaceManager = workspaceManager;
        _toolRegistry = toolRegistry;
        _toolContributors = toolContributors;
        _memoryStoreFactory = memoryStoreFactory;
        _agentMemoryFactory = agentMemoryFactory;
        _sharedMemoryRegistry = sharedMemoryRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "in-process";

    /// <inheritdoc />
    public async Task<IAgentHandle> CreateAsync(AgentDescriptor descriptor, AgentExecutionContext context, CancellationToken cancellationToken = default)
    {
        // #1382 Finding 2: resolve the conversation id at most once per CreateAsync call, shared by
        // both the #1706 conversation-override layer and the conversation-aware tools below. The
        // lookup is a read-only function of (store, sessionStore, agentId, sessionId) fixed for this
        // call, so it is safe to memoise; first invocation resolves, later ones return the cache.
        var conversationIdResolved = false;
        ConversationId? resolvedConversationId = null;
        async Task<ConversationId?> GetConversationIdAsync(IConversationStore store, ISessionStore? sessionStoreForResolve)
        {
            if (conversationIdResolved)
                return resolvedConversationId;
            resolvedConversationId = await ResolveConversationIdAsync(
                store,
                sessionStoreForResolve,
                descriptor.AgentId,
                context.SessionId,
                cancellationToken).ConfigureAwait(false);
            conversationIdResolved = true;
            return resolvedConversationId;
        }

        // #1704 / #1706: resolve the effective model through the centralized three-layer override
        // resolver (model defaults -> agent -> conversation) instead of reading descriptor.ModelId
        // ad hoc. The agent layer carries descriptor.ModelId; the conversation layer (PBI5) carries
        // the per-conversation override stored on the bound conversation and, being most-specific,
        // beats the agent default. An unset conversation override falls through unchanged. The
        // conversation-id lookup reuses the memoised GetConversationIdAsync so this adds no second
        // DB round-trip.
        var conversationOverrideLayer = await ResolveConversationOverrideLayerAsync(
            conversationStore => GetConversationIdAsync(conversationStore, _serviceProvider.GetService<ISessionStore>()),
            cancellationToken).ConfigureAwait(false);
        var effectiveModel = ModelOverrideResolver.Resolve(
            modelDefaults: default,
            agent: new ModelOverrideLayer(
                Model: descriptor.ModelId,
                Thinking: ParseAgentThinking(descriptor.Thinking),
                ContextWindow: descriptor.ContextWindow),
            conversation: conversationOverrideLayer);
        var resolvedModelId = effectiveModel.Model ?? descriptor.ModelId;

        // #1639: the model is already registered with the correct per-provider endpoint (enterprise
        // vs individual GitHub Copilot resolved at registration in BuiltInModels/discovery), so no
        // consumer-side BaseUrl patch is needed here anymore.
        var model = _llmClient.Models.GetModel(descriptor.ApiProvider, resolvedModelId)
            ?? throw new InvalidOperationException($"Model '{resolvedModelId}' for provider '{descriptor.ApiProvider}' is not registered.");

        var enrichedSystemPrompt = await _contextBuilder.BuildSystemPromptAsync(descriptor, context, cancellationToken);

        var workspacePath = _workspaceManager.GetWorkspacePath(descriptor.AgentId.Value);
        var pathValidator = new DefaultPathValidator(descriptor.FileAccess, workspacePath);
        var workspaceTools = _toolFactory.CreateTools(workspacePath, pathValidator, descriptor.ShellCommand);
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
            var agentMemory = _agentMemoryFactory.Create(descriptor.AgentId.Value);
            tools.Add(new MemorySaveTool(agentMemory, descriptor.AgentId.Value, _sharedMemoryRegistry));
            tools.Add(new MemorySearchTool(agentMemory, descriptor.AgentId.Value, descriptor.Memory, _sharedMemoryRegistry));
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
            var conversationId = await GetConversationIdAsync(conversationStore, sessionStore)
                .ConfigureAwait(false);
            var (conversationAccessLevel, conversationAllowedAgents) = ResolveConversationAccess(descriptor);
            var conversationChangeNotifier = _serviceProvider.GetService<IConversationChangeNotifier>();
            var messageOrchestrator = _serviceProvider.GetService<IInboundMessageOrchestrator>();
            tools.Add(new ConversationTool(
                conversationStore,
                descriptor.AgentId,
                conversationId,
                conversationAccessLevel,
                conversationAllowedAgents,
                sessionStore,
                messageOrchestrator,
                conversationChangeNotifier));
        }

        var includeAskUser = effectiveToolIds.Count == 0
            || effectiveToolIds.Contains("ask_user", StringComparer.OrdinalIgnoreCase);
        var askUserRegistry = _serviceProvider.GetService<IAskUserResponseRegistry>();
        if (includeAskUser && askUserRegistry is not null)
        {
            var askUserConversationId = conversationStore is not null
                ? await GetConversationIdAsync(conversationStore, sessionStore).ConfigureAwait(false)
                : null;
            tools.Add(new AskUserTool(
                askUserRegistry,
                descriptor.AgentId,
                context.SessionId,
                askUserConversationId,
                conversationStore));
        }
        var delayToolOptions = _serviceProvider.GetService<IOptions<DelayToolOptions>>() ?? Options.Create(new DelayToolOptions());
        tools.Add(new DelayTool(delayToolOptions));
        var platformConfig = _serviceProvider.GetService<IOptions<PlatformConfig>>();
        var serverTimezone = platformConfig?.Value.Gateway?.DefaultTimezone;
        tools.Add(new DateTimeTool(descriptor.Soul?.Timezone ?? serverTimezone));

        var fileWatcherToolOptions = _serviceProvider.GetService<IOptions<FileWatcherToolOptions>>() ?? Options.Create(new FileWatcherToolOptions());
        tools.Add(new FileWatcherTool(fileWatcherToolOptions, pathValidator));
        // Pull-based AGENTS.md discovery: the agent calls get_agent_files with a path to load
        // the conventions that apply there, instead of always-on injection that could exhaust context.
        tools.Add(new AgentFilesTool(pathValidator, _serviceProvider.GetService<System.IO.Abstractions.IFileSystem>()));

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
                    ? await GetConversationIdAsync(conversationStore, sessionStore).ConfigureAwait(false)
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
            {
                var converseExchangeOptions = _serviceProvider.GetService<IOptions<AgentExchangeOptions>>()?.Value;
                tools.Add(new AgentConverseTool(conversationService, sessionStore, descriptor.AgentId, context.SessionId, converseExchangeOptions));
            }
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
            {
                var exchangeOptions = _serviceProvider.GetService<IOptions<AgentExchangeOptions>>()?.Value;
                tools.Add(new ListAgentsTool(agentRegistry, descriptor.AgentId, exchangeOptions));
            }
        }

        var configurationWriter = _serviceProvider.GetService<IAgentConfigurationWriter>();
        var botNexusHome = _serviceProvider.GetService<BotNexusHome>();
        var changeNotifiers = _serviceProvider.GetServices<IAgentChangeNotifier>();
        if (agentRegistry is not null && configurationWriter is not null && botNexusHome is not null)
        {
            var apiProviderRegistry = _serviceProvider.GetService<ApiProviderRegistry>();
            var includeCreateAgent = effectiveToolIds.Count == 0
                || effectiveToolIds.Contains("create_agent", StringComparer.OrdinalIgnoreCase);
            if (includeCreateAgent)
            {
                var platformConfigOptions = _serviceProvider.GetService<IOptions<PlatformConfig>>();
                tools.Add(new CreateAgentTool(agentRegistry, configurationWriter, changeNotifiers, botNexusHome, platformConfigOptions, apiProviderRegistry, _llmClient.Models));
            }

            var includeUpdateAgent = effectiveToolIds.Count == 0
                || effectiveToolIds.Contains("update_agent", StringComparer.OrdinalIgnoreCase);
            if (includeUpdateAgent)
                tools.Add(new UpdateAgentTool(agentRegistry, configurationWriter, changeNotifiers, apiProviderRegistry, _llmClient.Models));
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
                canvasConversationId = await GetConversationIdAsync(canvasConvStore, sessionStore)
                    .ConfigureAwait(false);
            }
            tools.Add(new CanvasTool(descriptor.AgentId, canvasConversationId, canvasConvStore, canvasNotifiers));
        }

        var includeTodo = effectiveToolIds.Count == 0
                          || effectiveToolIds.Contains("todo", StringComparer.OrdinalIgnoreCase);
        if (includeTodo)
        {
            // Per-conversation todo list (#1464 step 2). Resolved per conversation exactly like the
            // canvas tool so the plan state is anchored to the bound conversation; no-ops safely when
            // there is no conversation context.
            var todoNotifiers = _serviceProvider.GetServices<IAgentTodoNotifier>().ToArray();
            ConversationId? todoConversationId = null;
            var todoConvStore = _serviceProvider.GetService<IConversationStore>();
            if (todoConvStore is not null)
            {
                todoConversationId = await GetConversationIdAsync(todoConvStore, sessionStore)
                    .ConfigureAwait(false);
            }
            tools.Add(new TodoTool(todoConversationId, todoConvStore, descriptor.AgentId, todoNotifiers));
        }

        List<object> extensionResourcesToDispose = [];
        var toolContributionContext = new AgentToolContributionContext(
            descriptor,
            context,
            workspacePath,
            pathValidator,
            _authManager.GetCopilotMcpEndpoint(descriptor.ApiProvider),
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
        var subAgentWriteAhead = descriptor.Kind == AgentKind.SubAgent
            ? new SubAgentToolWriteAhead(
                sessionStore,
                _serviceProvider.GetService<ISecretRedactor>() ?? new SecretRedactor(),
                context.SessionId,
                _logger)
            : null;

        if (hookDispatcher is not null || subAgentWriteAhead is not null)
        {
            var agentId = descriptor.AgentId;

            beforeToolCall = async (ctx, ct) =>
            {
                if (subAgentWriteAhead is not null)
                {
                    await subAgentWriteAhead.PersistAsync(
                        ctx.ToolCallRequest.Id,
                        ctx.ToolCallRequest.Name,
                        ctx.ValidatedArgs,
                        ct).ConfigureAwait(false);
                }

                if (hookDispatcher is null)
                    return null;

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

            afterToolCall = hookDispatcher is null ? null : async (ctx, ct) =>
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
        var resumeSystemPrompt = enrichedSystemPrompt;
        if (context.History.Count > 0)
        {
            // The cold-start resume projection — what survives a session hydration without
            // breaking the LLM provider — is owned by SessionContextProjector. Tool entries
            // are dropped there because Anthropic rejects orphaned tool_result blocks
            // (the Assistant SessionEntry persists response text but not the paired
            // tool_use). Phase 3a/#531 added IsHistory; Phase 3b/#534 centralised the
            // filter so all isolation strategies share it.
            var resumeEntries = SessionContextProjector.ProjectForResume(context.History);

            // Compaction summaries are System entries. The default message converter
            // (agent-core) deliberately drops System messages from the LLM message list
            // because system context belongs in Context.SystemPrompt, not the timeline.
            // So a summary materialised into the list never reaches the model -- the agent
            // resumes blind (#1693/#1698-adjacent: lost-context-on-resume). Fold summaries
            // into the system prompt so the folded context survives the converter contract.
            var summaries = resumeEntries
                .Where(e => e.Role.Equals(MessageRole.System) && e.IsCompactionSummary)
                .Select(e => e.Content)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();
            if (summaries.Count > 0)
            {
                var summaryBlock = string.Join("\n\n", summaries);
                resumeSystemPrompt = string.IsNullOrWhiteSpace(enrichedSystemPrompt)
                    ? summaryBlock
                    : $"{enrichedSystemPrompt}\n\n## Prior conversation (compacted summary)\n{summaryBlock}";
            }

            initialMessages = resumeEntries
                .Select(ConvertSessionEntryToAgentMessage)
                .OfType<AgentMessage>()
                .ToList();

            _logger.LogInformation(
                "Injecting {Count} history messages ({Summaries} summary folded into prompt, of {Total} entries) into agent context for session '{SessionId}'",
                initialMessages.Count, summaries.Count, context.History.Count, context.SessionId);
        }

        // #1710: best-effort mid-loop auto-compaction hook. ShouldCompact ran ONLY pre-turn at the
        // gateway, so a single long dispatch (cron / autonomous follow-up loop) grew the transcript
        // past the token threshold unchecked until provider overflow. The loop now re-checks between
        // outer iterations: when over threshold, compact and resync history via the coordinator (the
        // existing TryReplaceHistoryFromSnapshot apply + handle eviction). Mirrors PrepareTurnAsync.
        // CompactionOptions and the compactor are consumed read-only (#1687). Null when the supporting
        // services are unavailable, preserving prior behaviour.
        Func<CancellationToken, Task>? maybeCompactAsync = null;
        var compactor = _serviceProvider.GetService<ISessionCompactor>();
        var compactionCoordinator = _serviceProvider.GetService<ISessionCompactionCoordinator>();
        var compactionOptions = _serviceProvider.GetService<IOptionsMonitor<CompactionOptions>>();
        if (compactor is not null && compactionCoordinator is not null && compactionOptions is not null && sessionStore is not null)
        {
            var compactSessionId = context.SessionId;
            var compactAgentId = descriptor.AgentId;
            maybeCompactAsync = async cancellationToken =>
            {
                var liveSession = await sessionStore.GetAsync(compactSessionId, cancellationToken).ConfigureAwait(false);
                if (liveSession is null || !compactor.ShouldCompact(liveSession.Session, compactionOptions.CurrentValue))
                {
                    return;
                }

                await compactionCoordinator.CompactAsync(compactAgentId, liveSession, cancellationToken).ConfigureAwait(false);
            };
        }

        var options = new AgentOptions(
            InitialState: new AgentInitialState(
                SystemPrompt: resumeSystemPrompt,
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
            GenerationSettings: new SimpleStreamOptions
            {
                // Parse per-agent cacheRetentionMode string ("none", "short", "long").
                // Falls back to Short when absent or unrecognised.
                CacheRetention = Enum.TryParse<BotNexus.Agent.Providers.Core.Models.CacheRetention>(
                    descriptor.CacheRetentionMode, ignoreCase: true, out var parsedRetention)
                    ? parsedRetention
                    : BotNexus.Agent.Providers.Core.Models.CacheRetention.Short,
                // #1705: apply the effective thinking/context resolved through the centralized
                // three-layer resolver. Null means "provider default" and leaves the option unset.
                Reasoning = effectiveModel.Thinking,
                ContextWindow = effectiveModel.ContextWindow
            },
            SteeringMode: QueueMode.All,
            FollowUpMode: QueueMode.All,
            SessionId: context.SessionId.Value,
            ToolTimeout: ResolveToolTimeout(descriptor),
            ClaimAudit: ResolveClaimAuditOptions(platformConfig?.Value.Gateway?.ClaimAudit),
            MaybeCompactAsync: maybeCompactAsync);

        var agent = new BotNexus.Agent.Core.Agent(options);

        // PBI3 #1851: attach the hot-path metrics listener so turn/tool/provider instruments
        // actually fire for this agent. The listener subscribes to the agent's event stream and
        // is added to the handle's dispose list so its subscription is released with the handle.
        // Resolved defensively (GetService) so unit tests that construct the strategy without the
        // telemetry graph are unaffected; metrics recording never throws on the hot path.
        var hotPathMetrics = _serviceProvider.GetService<HotPathMetrics>();
        if (hotPathMetrics is not null)
        {
            var channel = context.Parameters.TryGetValue("channel", out var channelValue)
                ? channelValue as string
                : null;
            var hotPathListener = new HotPathMetricsAgentListener(
                agent,
                hotPathMetrics,
                descriptor.AgentId.Value,
                channel ?? HotPathMetrics.Unknown,
                descriptor.ApiProvider,
                resolvedModelId,
                _logger);
            extensionResourcesToDispose.Add(hotPathListener);
        }

        var inProcessHandle = new InProcessAgentHandle(
            agent,
            descriptor.AgentId,
            context.SessionId,
            _logger,
            tools,
            extensionResourcesToDispose,
            _serviceProvider.GetService<IActivityTracker>())
        {
            RenderedSystemPrompt = resumeSystemPrompt
        };
        IAgentHandle handle = inProcessHandle;

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

    // Parse the descriptor's wire-form thinking string ("minimal".."max", plus "xhigh") into the
    // ThinkingLevel enum for the resolver's agent layer. Unset / unrecognised => null (fall through
    // to the model default). Capability validity is enforced at registration; this is a lenient read.
    private static BotNexus.Agent.Providers.Core.Models.ThinkingLevel? ParseAgentThinking(string? thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking))
            return null;
        return AgentDescriptorValidator.TryParseThinking(thinking, out var level) ? level : null;
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

    /// <summary>
    /// Builds the post-turn claim-auditor options (#1600) from gateway configuration. When the
    /// <c>gateway:claimAudit</c> section is absent the auditor is enabled in warn mode (matching
    /// the documented config defaults), so fabricated artifact claims are caught out of the box.
    /// Returns <see langword="null"/> only when the section explicitly disables the auditor, which
    /// turns it off entirely (no scan).
    /// </summary>
    private static BotNexus.Agent.Core.Diagnostics.ClaimAuditOptions? ResolveClaimAuditOptions(ClaimAuditConfig? config)
    {
        // Absent section => on-by-default (warn). Explicitly disabled => null (off).
        if (config is { Enabled: false })
        {
            return null;
        }

        var mode = string.Equals(config?.Mode, "block", StringComparison.OrdinalIgnoreCase)
            ? BotNexus.Agent.Core.Diagnostics.ClaimAuditMode.Block
            : BotNexus.Agent.Core.Diagnostics.ClaimAuditMode.Warn;

        return BotNexus.Agent.Core.Diagnostics.ClaimAuditOptions.CreateDefault() with { Enabled = true, Mode = mode };
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

    // #1706: build the conversation-level override layer for the resolver from the conversation
    // bound to this session. Returns default (all-null) when there is no conversation store, no
    // bound conversation, or the conversation carries no overrides - so the resolver falls through
    // to the agent layer. The bound conversation id is resolved via the caller-supplied memoised
    // delegate (shared with the tool wiring) so this does not add a second DB round-trip. The
    // thinking token is parsed back to the provider enum here; an unrecognised persisted token is
    // treated as unset rather than throwing, because the API boundary validates tokens before they
    // are stored.
    private async Task<ModelOverrideLayer> ResolveConversationOverrideLayerAsync(
        Func<IConversationStore, Task<ConversationId?>> resolveConversationId,
        CancellationToken cancellationToken)
    {
        var conversationStore = _serviceProvider.GetService<IConversationStore>();
        if (conversationStore is null)
            return default;

        var conversationId = await resolveConversationId(conversationStore).ConfigureAwait(false);
        if (conversationId is not { } id)
            return default;

        var conversation = await conversationStore.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return default;

        ThinkingLevel? thinking = null;
        if (!string.IsNullOrWhiteSpace(conversation.ThinkingOverride)
            && TryParseThinkingToken(conversation.ThinkingOverride, out var parsed))
            thinking = parsed;

        return new ModelOverrideLayer(
            Model: string.IsNullOrWhiteSpace(conversation.ModelOverride) ? null : conversation.ModelOverride,
            Thinking: thinking,
            ContextWindow: conversation.ContextWindowOverride);
    }

    private static bool TryParseThinkingToken(string token, out ThinkingLevel level)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "minimal": level = ThinkingLevel.Minimal; return true;
            case "low": level = ThinkingLevel.Low; return true;
            case "medium": level = ThinkingLevel.Medium; return true;
            case "high": level = ThinkingLevel.High; return true;
            case "xhigh": level = ThinkingLevel.ExtraHigh; return true;
            case "max": level = ThinkingLevel.Max; return true;
            default: level = default; return false;
        }
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
            if (session is not null && session.ConversationId.IsInitialized())
                return session.ConversationId;
        }

        var conversations = await conversationStore.ListAsync(agentId, cancellationToken).ConfigureAwait(false);
        return conversations.FirstOrDefault(conversation => conversation.ActiveSessionId == sessionId)?.ConversationId;
    }

    // Compaction-summary System entries are folded into the system prompt on resume
    // (see CreateAsync); they are not materialised into the timeline because the
    // default converter drops list-level System messages. Any other System entry is
    // excluded too -- the converter would discard it anyway -- to avoid a phantom
    // injected-count. Returns null for entries that must not appear in the message list.
    private static AgentMessage? ConvertSessionEntryToAgentMessage(SessionEntry entry)
    {
        return entry.Role.Value switch
        {
            "user" => new AgentCoreUserMessage(entry.Content),
            "assistant" => new AssistantAgentMessage(entry.Content),
            "system" => null,
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

    // Liveness: the handle is the single choke point through which every agent
    // execution flows — interactive StreamAsync turns AND blocking PromptAsync
    // runs (cron, soul, heartbeat, sub-agents). Recording activity here means the
    // watchdog's "no activity" window reflects genuine in-flight work regardless
    // of entry path, instead of only the arrival of a new inbound message at
    // GatewayHost.ProcessAsync. Optional so unit tests can construct the handle
    // without the gateway DI graph. (#1320)
    private readonly IActivityTracker? _activityTracker;

    public InProcessAgentHandle(
        BotNexus.Agent.Core.Agent agent,
        AgentId agentId,
        SessionId sessionId,
        ILogger logger,
        IReadOnlyList<IAgentTool>? tools = null,
        IReadOnlyList<object>? resourcesToDispose = null,
        IActivityTracker? activityTracker = null)
    {
        _agent = agent;
        AgentId = agentId;
        SessionId = sessionId;
        _logger = logger;
        _activityTracker = activityTracker;
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

    /// <summary>
    /// The system prompt that was rendered and injected into the agent at creation time.
    /// Populated by <see cref="InProcessIsolationStrategy.CreateAsync"/> immediately after
    /// <see cref="IContextBuilder.BuildSystemPromptAsync"/> returns so that the supervisor
    /// can stamp <see cref="GatewaySession.LastRenderedSystemPrompt"/> without round-tripping
    /// through the isolation strategy contract.
    /// </summary>
    internal string? RenderedSystemPrompt { get; set; }

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
        // Liveness: blocking prompt path (cron / soul / heartbeat) bypasses the
        // streaming dispatcher, so record at entry to keep the watchdog honest. (#1320)
        _activityTracker?.RecordActivity();
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
        // Liveness: blocking prompt path (cron / soul / heartbeat) bypasses the
        // streaming dispatcher, so record at entry to keep the watchdog honest. (#1320)
        _activityTracker?.RecordActivity();
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

    /// <summary>
    /// Maps a raw <see cref="AgentEvent"/> to the gateway-facing <see cref="AgentStreamEvent"/>, or
    /// <see langword="null"/> when the event has no client-visible representation.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="StreamCoreAsync"/> as a pure function (#1382) so the agent-event
    /// translation can be unit-tested directly without driving a live agent subscription/channel
    /// pipeline. <paramref name="messageId"/> is the stable per-turn correlation id stamped onto
    /// every emitted event.
    /// </remarks>
    internal static AgentStreamEvent? MapAgentEvent(AgentEvent agentEvent, string messageId)
        => agentEvent switch
        {
            // RunStarted/RunEnded bracket the ENTIRE loop (all turns, tool cycles, follow-up
            // continuations). They are the authoritative "agent busy" signal for clients, staying
            // asserted across the inter-step gaps (message-end -> tool-start, tool-end -> tool-start,
            // tool-end -> next message-start) that individual MessageStart/ToolStart events leave open.
            AgentStartEvent
                => new AgentStreamEvent { Type = AgentStreamEventType.RunStarted, MessageId = messageId },
            AgentEndEvent
                => new AgentStreamEvent { Type = AgentStreamEventType.RunEnded, MessageId = messageId },
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
                ToolResult = toolEnd.Result.Content.FirstOrDefault()?.Value,
                ToolIsError = toolEnd.IsError,
                MessageId = messageId
            },
            MessageEndEvent end when end.Message is AssistantAgentMessage assistant
                => new AgentStreamEvent
                {
                    Type = AgentStreamEventType.MessageEnd,
                    MessageId = messageId,
                    Usage = assistant.Usage is null ? null : new AgentResponseUsage(
                        InputTokens: assistant.Usage.InputTokens,
                        OutputTokens: assistant.Usage.OutputTokens,
                        CacheRead: assistant.Usage.CacheRead,
                        CacheWrite: assistant.Usage.CacheWrite)
                },
            ToolExecutionUpdateEvent { PartialResult.Details: AskUserRequest askUserRequest }
                => new AgentStreamEvent
                {
                    Type = AgentStreamEventType.UserInputRequired,
                    UserInputRequest = askUserRequest,
                    MessageId = messageId
                },
            TurnEndEvent
                => new AgentStreamEvent { Type = AgentStreamEventType.TurnEnd, MessageId = messageId },
            ClaimAuditEvent claimAudit
                => new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ClaimAudit,
                    MessageId = messageId,
                    ClaimAudit = new ClaimAuditSignal(
                        claimAudit.Result.ShouldBlock,
                        claimAudit.Result.UnbackedClaims
                            .Select(c => new ClaimAuditClaim(c.Category.ToString(), c.Snippet))
                            .ToList())
                },
            _ => null
        };

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
            // Liveness: any agent event (content delta, tool start/end, message end)
            // is proof the gateway is actively progressing this turn. (#1320)
            _activityTracker?.RecordActivity();
            try
            {
                var streamEvent = MapAgentEvent(agentEvent, messageId);

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
    public Task SteerDeferrableAsync(string message, CancellationToken cancellationToken = default)
    {
        // #1845: mark as defer-while-busy so the agent loop holds this side turn until it reaches
        // a genuine idle boundary rather than consuming an in-flight continuation.
        _agent.Steer(new AgentCoreUserMessage(message) { DeferWhileBusy = true });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Atomically aborts the current run (if any), clears stale steering messages from the
    /// abandoned direction, and enqueues the new direction so the agent resumes with the
    /// redirected goal. Part of #704 Phase 1b (Issue #800).
    /// </remarks>
    public async Task InterruptAndSteerAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        // 1. Abort the current run (no-op when idle; cancels CTS and waits for run to settle).
        await _agent.AbortAsync();

        // 2. Discard stale steering messages queued for the abandoned direction.
        _agent.ClearSteeringQueue();

        // 3. Enqueue the new direction. The agent picks it up at the next steering drain point.
        _agent.Steer(new AgentCoreUserMessage(message));
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


