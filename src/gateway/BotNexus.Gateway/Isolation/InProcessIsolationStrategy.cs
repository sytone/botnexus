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
using BotNexus.Gateway.Audit;
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

        // #2396: a per-run thinking selection (headless `agent exec --thinking`, carried as session
        // metadata by ChatController and surfaced here through AgentExecutionContext.Parameters) is
        // MORE specific than the conversation's standing override, so it overlays the conversation
        // layer rather than becoming a fourth resolver argument. Folding it in here keeps
        // ModelOverrideResolver the single precedence authority; an unrecognised token is treated as
        // unset, matching how a persisted conversation token is handled directly below.
        if (context.Parameters.TryGetValue("thinkingOverride", out var runThinkingRaw)
            && runThinkingRaw is string runThinkingToken
            && TryParseThinkingToken(runThinkingToken, out var runThinking))
        {
            conversationOverrideLayer = conversationOverrideLayer with { Thinking = runThinking };
        }

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

        // #2796: hand the prompt builder the SAME resolved settings that configure the model and
        // AgentOptions below. This is the single value; the context builder must never re-resolve
        // the override or read descriptor.ModelId for the runtime block, or the block drifts from
        // what the run actually uses.
        var effectiveSettings = new EffectiveExecutionSettings(
            Provider: descriptor.ApiProvider,
            Model: resolvedModelId,
            DescriptorDefaultModel: descriptor.ModelId,
            Thinking: effectiveModel.Thinking,
            ContextWindow: effectiveModel.ContextWindow);

        var enrichedSystemPrompt = await _contextBuilder.BuildSystemPromptAsync(descriptor, context, effectiveSettings, cancellationToken);

        var workspacePath = _workspaceManager.GetWorkspacePath(descriptor.AgentId.Value);
        var pathValidator = new DefaultPathValidator(descriptor.FileAccess, workspacePath);
        var workspaceTools = _toolFactory.CreateTools(WorkingDir.From(workspacePath), pathValidator, descriptor.ShellCommand);
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
            var memoryStore = _memoryStoreFactory.Create(descriptor.AgentId);
            // Initialize asynchronously ΓÇö don't block handle creation.
            // Memory tools work immediately; the store initializes in the background.
            _ = memoryStore.InitializeAsync(CancellationToken.None);
            var agentMemory = _agentMemoryFactory.Create(descriptor.AgentId.Value);
            tools.Add(new MemorySaveTool(agentMemory, descriptor.AgentId.Value, _sharedMemoryRegistry));
            tools.Add(new MemorySearchTool(agentMemory, descriptor.AgentId.Value, descriptor.Memory, _sharedMemoryRegistry));
            tools.Add(new MemoryGetTool(memoryStore));
        }

        // #1382 Finding 1: the per-tool availability + allowlist gates that previously issued 23
        // inline _serviceProvider.GetService<...>() calls are now explicit IToolProvider units (see
        // Isolation/ToolProviders). Sub-agent gate diagnostics stay here because they observe an
        // invariant, not a tool; the computed flag is handed to the SubAgentToolProvider.
        //
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
                "drift - typed and substring signals must agree.",
                context.SessionId,
                descriptor.AgentId);
        }
        else if (descriptor.Kind != AgentKind.SubAgent && context.SessionId.IsSubAgent)
        {
            _logger.LogWarning(
                "Isolation gate: SessionId '{SessionId}' is a sub-agent shape but descriptor.Kind={Kind} for " +
                "agent '{AgentId}'. The substring fallback is correctly blocking spawn tools, but the typed " +
                "signal should also be SubAgent - this indicates the descriptor was registered outside of " +
                "DefaultSubAgentManager.SpawnAsync.",
                context.SessionId,
                descriptor.Kind,
                descriptor.AgentId);
        }

        // Shared locals still needed by the agent-options assembly further down.
        var sessionStore = _serviceProvider.GetService<ISessionStore>();
        var platformConfig = _serviceProvider.GetService<IOptions<PlatformConfig>>();

        var toolProviderContext = new ToolProviders.ToolProviderContext(
            descriptor,
            context,
            effectiveToolIds,
            new HashSet<string>(tools.Select(tool => tool.Name), StringComparer.OrdinalIgnoreCase),
            isSubAgentSession,
            pathValidator,
            store => GetConversationIdAsync(store, sessionStore),
            _logger,
            cancellationToken);

        // #1382 Finding 1: providers.Where(ShouldInclude).SelectMany(CreateTools). Kept as an
        // explicit loop rather than a LINQ SelectMany because CreateToolsAsync is asynchronous
        // (bound-conversation resolution / live-session reads); the semantics are identical.
        foreach (var toolProvider in BuildToolProviders(sessionStore, platformConfig))
        {
            if (!toolProvider.ShouldInclude(toolProviderContext))
                continue;
            var providerTools = await toolProvider.CreateToolsAsync(toolProviderContext).ConfigureAwait(false);
            if (providerTools.Count > 0)
                tools.AddRange(providerTools);
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
        // #2615: the fail-closed tool-audit write-ahead. Pre-#2615 this existed only for sub-agents
        // (#2113), so a top-level agent's tool call was never written ahead and a crash mid-tool left
        // no evidence the tool had been invoked at all. It now runs for EVERY agent, and it is the
        // seam that both blocks a side-effecting tool whose invocation cannot be durably recorded and
        // closes out an interrupted call with an explicit incomplete record.
        var toolWriteAhead = new ToolAuditWriteAhead(
            sessionStore,
            _serviceProvider.GetService<IToolAuditSink>() ?? DefaultToolAuditSink.Instance,
            _serviceProvider.GetService<ISecretRedactor>() ?? new SecretRedactor(),
            context.SessionId,
            _logger);

        {
            var agentId = descriptor.AgentId;

            beforeToolCall = async (ctx, ct) =>
            {
                // Write ahead FIRST, then consult policy. The record must be durable before any
                // decision that can lead to execution, and a blocked call still throws out of here
                // before the tool is reached (#2615 AC2).
                await toolWriteAhead.PersistStartAsync(
                    ctx.ToolCallRequest.Id,
                    ctx.ToolCallRequest.Name,
                    ctx.ValidatedArgs,
                    ct).ConfigureAwait(false);

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

            afterToolCall = async (ctx, ct) =>
            {
                // The call reported a result, so it is accounted for and must not later be written
                // out as an interrupted invocation.
                toolWriteAhead.RecordCompleted(ctx.ToolCallRequest.Id);

                if (hookDispatcher is null)
                    return null;

                var resultText = AgentToolResultText.Extract(ctx.Result);
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
            // #2896: this path has already resolved the effective window through
            // ModelOverrideResolver above (conversation override > agent descriptor), so reuse it
            // rather than re-reading the stores, falling back to the registered model's own window.
            // Null leaves CompactionOptions.ContextWindowTokens exactly as configured.
            var scopedContextWindow = ScopedCompactionWindow.Resolve(
                conversationOverride: null,
                agentWindow: effectiveModel.ContextWindow,
                modelWindow: model.ContextWindow);
            maybeCompactAsync = async cancellationToken =>
            {
                var liveSession = await sessionStore.GetAsync(compactSessionId, cancellationToken).ConfigureAwait(false);
                var scopedOptions = ScopedCompactionWindow.Apply(compactionOptions.CurrentValue, scopedContextWindow);
                if (liveSession is null || !compactor.ShouldCompact(liveSession.Session, scopedOptions))
                {
                    return;
                }

                await compactionCoordinator.CompactAsync(compactAgentId, liveSession, cancellationToken).ConfigureAwait(false);
            };
        }

        // #3015: resolve the non-secret auth-profile identity BEFORE building options so a
        // suspension is scoped to the credential actually in use, not merely to the provider. Two
        // agents sharing github-copilot but authenticating with different credentials must not cool
        // each other. Failure here is non-fatal: a null profile degrades the scope to the provider's
        // "default" profile rather than failing the run.
        string? authProfileId = null;
        try
        {
            authProfileId = await _authManager
                .GetAuthProfileIdAsync(model.Provider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve auth profile id for provider '{Provider}'.", model.Provider);
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
            // #2548: give the agent core a diagnostic sink. AgentOptions.OnDiagnostic is the only
            // channel the core has for non-fatal runtime conditions it deliberately swallows to
            // keep the run alive (an event listener threw, an agent_end notification failed, a
            // BeforeToolCall hook breached its fail-closed timeout). Nothing assigned it, so every
            // one of those was discarded at the boundary and the operator saw nothing. Agent.Core
            // deliberately carries no logging dependency, so the host - not the core - owns the
            // forwarding: the delegate closes over the gateway's existing ILogger.
            //
            // Warning is the correct severity: these conditions are never expected, and each one
            // means work was silently lost, but none of them failed the turn. Information would
            // bury them in the normal hot-path stream; Error would page on a condition the agent
            // already recovered from.
            OnDiagnostic: diagnostic => _logger.LogWarning(
                "Agent diagnostic for '{AgentId}' session '{SessionId}': {Diagnostic}",
                descriptor.AgentId.Value, context.SessionId.Value, diagnostic),
            ToolTimeout: ResolveToolTimeout(descriptor),
            ClaimAudit: ResolveClaimAuditOptions(platformConfig?.Value.Gateway?.ClaimAudit),
            MaybeCompactAsync: maybeCompactAsync,
            // #3015: the exhaustion lane's memory. The registry is a gateway singleton so a
            // suspension recorded on one turn is still visible on the next -- pre-#3015 all retry
            // state lived in a local attempt counter and died with the call, which is precisely why
            // a billing-disabled profile re-paid four provider round-trips plus 3.5s of backoff on
            // every single turn, forever. Resolved defensively so unit tests that construct the
            // strategy without the full service graph keep working (null simply records nothing;
            // the one-attempt fail-fast still applies).
            SuspensionRegistry: _serviceProvider.GetService<BotNexus.Agent.Core.Loop.IProviderSuspensionRegistry>(),
            AuthProfile: authProfileId,
            // #3162: the central tool-output backstop. Reads gateway:toolOutputBudget and defaults
            // ON (256 KiB) when the section is absent; disabled (0) only when Enabled=false or
            // MaxBytes<=0, matching the toolResultPersistence convention.
            MaxToolOutputBytes: ResolveMaxToolOutputBytes(platformConfig?.Value.Gateway?.ToolOutputBudget));

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
            _serviceProvider.GetService<IActivityTracker>(),
            toolWriteAhead,
            // #3091: the diagnostics endpoint must report the window this run is ACTUALLY bound to.
            // Resolved from the same effectiveModel/model pair that configures the run below, so the
            // reported window cannot drift from the executed one (same single-derivation rule as #2796).
            ContextWindowResolver.Resolve(effectiveModel.ContextWindow, model))
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
    // #1382 Finding 1: assemble the explicit tool providers that replaced the inline service-locator
    // gates. Every GetService/GetServices call that used to be scattered through CreateAsync now
    // lives here in one place, feeding each provider its dependencies through a normal constructor.
    // Providers whose dependencies are absent gate themselves out via ShouldInclude, preserving the
    // pre-refactor "is not null" semantics exactly. Order matches the original registration order so
    // the resulting tool list is identical.
    private IReadOnlyList<ToolProviders.IToolProvider> BuildToolProviders(
        ISessionStore? sessionStore,
        IOptions<PlatformConfig>? platformConfig)
    {
        var conversationStore = _serviceProvider.GetService<IConversationStore>();
        return
        [
            new ToolProviders.CronToolProvider(
                _serviceProvider.GetService<ICronStore>(),
                _serviceProvider.GetService<CronScheduler>(),
                _serviceProvider.GetService<BotNexus.Agent.Providers.Core.Registry.ModelRegistry>(),
                _serviceProvider.GetService<BotNexus.Cron.Actions.ICommandCronAuthorizer>(),
                _serviceProvider.GetService<BotNexus.Cron.ICronAlertTargetResolver>()),
            new ToolProviders.SessionToolProvider(sessionStore),
            new ToolProviders.ConversationToolProvider(
                conversationStore,
                sessionStore,
                _serviceProvider.GetService<IConversationChangeNotifier>(),
                _serviceProvider.GetService<IInboundMessageOrchestrator>(),
                _serviceProvider.GetService<IConversationRouter>()),
            new ToolProviders.AskUserToolProvider(
                _serviceProvider.GetService<IAskUserResponseRegistry>(),
                conversationStore,
                sessionStore),
            new ToolProviders.DelayToolProvider(_serviceProvider.GetService<IOptions<DelayToolOptions>>()),
            new ToolProviders.DateTimeToolProvider(platformConfig),
            new ToolProviders.FileWatcherToolProvider(_serviceProvider.GetService<IOptions<FileWatcherToolOptions>>()),
            new ToolProviders.AgentFilesToolProvider(_serviceProvider.GetService<System.IO.Abstractions.IFileSystem>()),
            new ToolProviders.SubAgentToolProvider(
                _serviceProvider.GetService<ISubAgentManager>(),
                _serviceProvider.GetService<IOptions<GatewayOptions>>(),
                conversationStore,
                sessionStore),
            new ToolProviders.AgentConverseToolProvider(
                _serviceProvider.GetService<IAgentExchangeService>(),
                sessionStore,
                _serviceProvider.GetService<IOptions<AgentExchangeOptions>>()),
            new ToolProviders.FinishAgentExchangeToolProvider(sessionStore),
            new ToolProviders.ListAgentsToolProvider(
                _serviceProvider.GetService<IAgentRegistry>(),
                _serviceProvider.GetService<IOptions<AgentExchangeOptions>>()),
            new ToolProviders.AgentManagementToolProvider(
                _serviceProvider.GetService<IAgentRegistry>(),
                _serviceProvider.GetService<IAgentConfigurationWriter>(),
                _serviceProvider.GetService<BotNexusHome>(),
                _serviceProvider.GetServices<IAgentChangeNotifier>(),
                _serviceProvider.GetService<IOptions<PlatformConfig>>(),
                _llmClient),
            new ToolProviders.CanvasToolProvider(
                _serviceProvider.GetService<IConversationStore>(),
                _serviceProvider.GetServices<IAgentCanvasNotifier>(),
                _serviceProvider.GetService<IOptions<PlatformConfig>>()),
            new ToolProviders.TodoToolProvider(
                _serviceProvider.GetService<IConversationStore>(),
                _serviceProvider.GetServices<IAgentTodoNotifier>()),
        ];
    }

    private static BotNexus.Agent.Providers.Core.Models.ThinkingLevel? ParseAgentThinking(string? thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking))
            return null;
        return AgentDescriptorValidator.TryParseThinking(thinking, out var level) ? level : null;
    }
    /// <summary>
    /// Resolves the central tool-output backstop budget (#3162) from gateway configuration.
    /// </summary>
    /// <remarks>
    /// An absent section means the platform default (backstop ON), because an unbounded tool result
    /// reaching the context window is precisely the condition #3162 exists to prevent -- "not
    /// configured" must not mean "unprotected". Zero is returned only when an operator explicitly
    /// disabled it, which <c>ToolOutputBudget.Apply</c> treats as a no-op.
    /// </remarks>
    internal static int ResolveMaxToolOutputBytes(ToolOutputBudgetConfig? config)
    {
        var effective = config ?? new ToolOutputBudgetConfig();
        return effective is { Enabled: true, MaxBytes: > 0 } ? effective.MaxBytes : 0;
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

    // #3091: the resolved context window for this run, or null when it could not be established.
    // Never defaulted to a literal - see ContextWindowResolver.
    private readonly int? _contextWindowTokens;

    /// <summary>
    /// The fail-closed tool-audit write-ahead this handle's run writes through (#2615). The handle
    /// is the single choke point every execution path crosses, so it is also the only place that
    /// can observe a run unwinding and close out the calls that started and never reported a
    /// result. Optional so unit tests can construct the handle without the gateway DI graph.
    /// </summary>
    private readonly ToolAuditWriteAhead? _toolWriteAhead;

    public InProcessAgentHandle(
        BotNexus.Agent.Core.Agent agent,
        AgentId agentId,
        SessionId sessionId,
        ILogger logger,
        IReadOnlyList<IAgentTool>? tools = null,
        IReadOnlyList<object>? resourcesToDispose = null,
        IActivityTracker? activityTracker = null,
        ToolAuditWriteAhead? toolWriteAhead = null,
        int? contextWindowTokens = null)
    {
        _agent = agent;
        AgentId = agentId;
        SessionId = sessionId;
        _logger = logger;
        _activityTracker = activityTracker;
        _contextWindowTokens = contextWindowTokens;
        _toolWriteAhead = toolWriteAhead;
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
    public int? GetContextWindowTokens() => _contextWindowTokens;

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
            var response = BuildResponse(messages);

            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (OperationCanceledException oce)
        {
            activity?.SetStatus(ActivityStatusCode.Error, oce.Message);
            // #2615 AC3/AC4: a cancellation or timeout after tool-start must not make the
            // invocation vanish. Close out every still-unaccounted call with the shared explicit
            // incomplete record before the cancellation propagates.
            await RecordInterruptedToolsAsync(oce.CancellationToken).ConfigureAwait(false);
            throw BuildInterruptedException(oce);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // A crash mid-run is the other half of AC3: the run unwinds through here, and an
            // in-flight tool has to leave the same auditable incomplete record it would on a
            // cancellation.
            await RecordInterruptedToolsAsync(cancellationToken).ConfigureAwait(false);
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
            var response = BuildResponse(messages);

            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (OperationCanceledException oce)
        {
            activity?.SetStatus(ActivityStatusCode.Error, oce.Message);
            await RecordInterruptedToolsAsync(oce.CancellationToken).ConfigureAwait(false);
            throw BuildInterruptedException(oce);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await RecordInterruptedToolsAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Writes the explicit incomplete record for every tool call that started and never reported a
    /// result (#2615 AC3/AC4). Safe to call more than once and on a handle constructed without a
    /// write-ahead; the write-ahead itself is idempotent and never throws.
    /// </summary>
    private Task RecordInterruptedToolsAsync(CancellationToken cancellationToken)
        => _toolWriteAhead?.RecordInterruptedAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Projects the messages a completed blocking run produced into a gateway <see cref="AgentResponse"/>,
    /// carrying full tool-call metadata (id, name, arguments, result content, error) so a blocking caller
    /// such as the cron trigger can persist a tool timeline with parity to the interactive streaming path
    /// (issue #2118). Tool calls are surfaced in execution order.
    /// </summary>
    private static AgentResponse BuildResponse(IReadOnlyList<AgentMessage> messages)
    {
        var lastAssistant = messages.OfType<AssistantAgentMessage>().LastOrDefault();
        return new AgentResponse
        {
            Content = lastAssistant?.Content ?? string.Empty,
            Usage = lastAssistant?.Usage is { } u ? new AgentResponseUsage(u.InputTokens, u.OutputTokens) : null,
            ToolCalls = BuildToolCalls(messages, pendingToolCallIds: null)
        };
    }

    /// <summary>
    /// Builds the interruption exception carried out of a cancelled/timed-out blocking run. Reads the
    /// live agent timeline (<see cref="AgentState.Messages"/>) to capture every tool call that completed
    /// before cancellation, plus any tool still in-flight (<see cref="AgentState.PendingToolCalls"/>),
    /// which is marked <see cref="AgentToolCallInfo.IsIncomplete"/> so the transcript represents the
    /// interrupted tool consistently (issue #2118).
    /// </summary>
    private AgentPromptInterruptedException BuildInterruptedException(OperationCanceledException oce)
    {
        var snapshot = _agent.State.Messages;
        var lastAssistant = snapshot.OfType<AssistantAgentMessage>().LastOrDefault();
        var partial = new AgentResponse
        {
            Content = lastAssistant?.Content ?? string.Empty,
            Usage = lastAssistant?.Usage is { } u ? new AgentResponseUsage(u.InputTokens, u.OutputTokens) : null,
            ToolCalls = BuildToolCalls(snapshot, _agent.State.PendingToolCalls)
        };
        return new AgentPromptInterruptedException(partial, oce.CancellationToken);
    }

    /// <summary>
    /// Correlates the assistant tool-call requests (which carry arguments) with the tool-result messages
    /// (which carry result content and error state) in a run timeline, producing one
    /// <see cref="AgentToolCallInfo"/> per requested call in execution order. A call whose id appears in
    /// <paramref name="pendingToolCallIds"/> but has no matching result was interrupted mid-flight and is
    /// flagged <see cref="AgentToolCallInfo.IsIncomplete"/>. Shared by the completed and interrupted paths
    /// so both persist the same tool-timeline shape (issue #2118).
    /// </summary>
    internal static IReadOnlyList<AgentToolCallInfo> BuildToolCalls(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlySet<string>? pendingToolCallIds)
    {
        // Result rows are keyed by tool call id; first result wins for a given id.
        var resultsById = new Dictionary<string, ToolResultAgentMessage>(StringComparer.Ordinal);
        foreach (var result in messages.OfType<ToolResultAgentMessage>())
        {
            resultsById.TryAdd(result.ToolCallId, result);
        }

        var toolCalls = new List<AgentToolCallInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Walk the timeline in order. Assistant messages carry the tool-call requests (with the
        // arguments the model supplied); each request is matched to its result to form a full row.
        foreach (var message in messages)
        {
            if (message is not AssistantAgentMessage assistant || assistant.ToolCalls is null)
                continue;

            foreach (var call in assistant.ToolCalls)
            {
                if (!seen.Add(call.Id))
                    continue;

                var arguments = call.Arguments is { Count: > 0 }
                    ? JsonSerializer.Serialize(call.Arguments)
                    : null;

                if (resultsById.TryGetValue(call.Id, out var result))
                {
                    toolCalls.Add(new AgentToolCallInfo(
                        call.Id,
                        call.Name,
                        result.IsError,
                        arguments,
                        AgentToolResultText.Extract(result.Result),
                        IsIncomplete: false));
                }
                else
                {
                    // No result row: the call is either still in-flight at cancellation (pending) or
                    // otherwise never completed. Either way it is an incomplete/interrupted call.
                    var incomplete = pendingToolCallIds?.Contains(call.Id) ?? true;
                    toolCalls.Add(new AgentToolCallInfo(
                        call.Id,
                        call.Name,
                        IsError: incomplete,
                        arguments,
                        ResultContent: null,
                        IsIncomplete: incomplete));
                }
            }
        }

        // Defensive: surface any orphan result whose request was never captured on an assistant
        // message (should not happen in normal runs, but keeps the timeline lossless).
        foreach (var result in messages.OfType<ToolResultAgentMessage>())
        {
            if (seen.Add(result.ToolCallId))
            {
                toolCalls.Add(new AgentToolCallInfo(
                    result.ToolCallId,
                    result.ToolName,
                    result.IsError,
                    Arguments: null,
                    AgentToolResultText.Extract(result.Result),
                    IsIncomplete: false));
            }
        }

        return toolCalls;
    }

    /// <summary>
    /// Projects a blocking-run timeline into the shared <see cref="ToolInvocationRecord"/> shape
    /// (issue #2613). One record per requested call, in execution order, with contiguous
    /// <see cref="ToolInvocationRecord.OrderIndex"/> values, the arguments the model supplied, the
    /// result content and error state, and the start/completion timestamps carried by the timeline
    /// messages. A call whose id has no result row was interrupted mid-flight and is emitted as an
    /// incomplete record with its arguments intact and no completion timestamp.
    /// </summary>
    /// <remarks>
    /// This is the blocking-path half of the #2613 parity pair; the streaming half lives in
    /// <see cref="BotNexus.Gateway.Streaming.StreamingSessionHelper"/>. Both project into the SAME
    /// record type through the SAME <see cref="ToolInvocationRecordPolicy"/>, which is what makes
    /// the two boundaries observably equivalent rather than merely similar.
    /// </remarks>
    /// <param name="messages">The run timeline (assistant tool-call requests plus tool results).</param>
    /// <param name="pendingToolCallIds">Ids still in flight at interruption, or null.</param>
    /// <param name="policy">Redaction/truncation policy; defaults to <see cref="ToolInvocationRecordPolicy.Default"/>.</param>
    /// <returns>The ordered tool invocation records for the run.</returns>
    internal static IReadOnlyList<ToolInvocationRecord> BuildToolInvocations(
        IEnumerable<AgentMessage> messages,
        ISet<string>? pendingToolCallIds,
        ToolInvocationRecordPolicy? policy = null)
    {
        policy ??= ToolInvocationRecordPolicy.Default;
        var timeline = messages as IReadOnlyList<AgentMessage> ?? messages.ToList();

        // Result rows are keyed by tool call id; first result wins for a given id.
        var resultsById = new Dictionary<string, ToolResultAgentMessage>(StringComparer.Ordinal);
        foreach (var result in timeline.OfType<ToolResultAgentMessage>())
        {
            resultsById.TryAdd(result.ToolCallId, result);
        }

        var records = new List<ToolInvocationRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in timeline)
        {
            if (message is not AssistantAgentMessage assistant || assistant.ToolCalls is null)
                continue;

            foreach (var call in assistant.ToolCalls)
            {
                if (!seen.Add(call.Id))
                    continue;

                var arguments = call.Arguments is { Count: > 0 }
                    ? JsonSerializer.Serialize(call.Arguments)
                    : null;

                if (resultsById.TryGetValue(call.Id, out var result))
                {
                    records.Add(policy.Create(
                        orderIndex: records.Count,
                        toolCallId: call.Id,
                        toolName: call.Name,
                        rawArguments: arguments,
                        rawResultContent: AgentToolResultText.Extract(result.Result),
                        isError: result.IsError,
                        isIncomplete: false,
                        startedAt: assistant.Timestamp,
                        completedAt: result.Timestamp ?? assistant.Timestamp));
                }
                else
                {
                    // No result row: the call was either still in flight at cancellation or never
                    // completed. Either way it is an incomplete/interrupted call.
                    var incomplete = pendingToolCallIds?.Contains(call.Id) ?? true;
                    records.Add(policy.Create(
                        orderIndex: records.Count,
                        toolCallId: call.Id,
                        toolName: call.Name,
                        rawArguments: arguments,
                        rawResultContent: null,
                        isError: incomplete,
                        isIncomplete: incomplete,
                        startedAt: assistant.Timestamp,
                        completedAt: null));
                }
            }
        }

        // Defensive: surface any orphan result whose request never appeared on an assistant
        // message, so the timeline stays lossless.
        foreach (var result in timeline.OfType<ToolResultAgentMessage>())
        {
            if (seen.Add(result.ToolCallId))
            {
                records.Add(policy.Create(
                    orderIndex: records.Count,
                    toolCallId: result.ToolCallId,
                    toolName: result.ToolName,
                    rawArguments: null,
                    rawResultContent: AgentToolResultText.Extract(result.Result),
                    isError: result.IsError,
                    isIncomplete: false,
                    startedAt: result.Timestamp,
                    completedAt: result.Timestamp));
            }
        }

        return records;
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
                ToolResult = AgentToolResultText.Extract(toolEnd.Result),
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

            // #2615 AC3/AC4: the streamed run may have been abandoned mid-tool (client disconnect,
            // turn cancellation, provider death). Any call that started and never reported a result
            // is closed out with the explicit incomplete record rather than left silently open.
            await RecordInterruptedToolsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IDisposable? ObserveTurns(Action onTurnCompleted)
    {
        ArgumentNullException.ThrowIfNull(onTurnCompleted);

        // TurnEndEvent is the agent loop's own per-turn boundary (one model call plus its tool
        // cycle) — the same event that drives RunMetricsAccumulator.IncrementTurns. Projecting it
        // here means a turn-budget caller counts exactly what the loop counts (#2656).
        return _agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent is TurnEndEvent)
            {
                try
                {
                    onTurnCompleted();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Turn observer threw for '{AgentId}' session '{SessionId}'.",
                        AgentId,
                        SessionId);
                }
            }

            return Task.CompletedTask;
        });
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
    /// <remarks>
    /// #2484: injects the COMPOSED message (text plus any vision payload) verbatim, so a steer
    /// dispatched with draft attachments delivers them. The string overload cannot carry images.
    /// </remarks>
    public Task SteerAsync(AgentCoreUserMessage message, CancellationToken cancellationToken = default)
    {
        _agent.Steer(message);
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
    /// <remarks>
    /// #2484 typed counterpart: same abort/clear/enqueue sequence, but the composed message
    /// (including its vision payload) is enqueued intact instead of text only.
    /// </remarks>
    public async Task InterruptAndSteerAsync(AgentCoreUserMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await _agent.AbortAsync();
        _agent.ClearSteeringQueue();
        _agent.Steer(message);
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
    /// <remarks>
    /// Closes the check-then-enqueue race (#2438) by enqueueing FIRST and only then re-reading
    /// the run status. Ordering argument:
    /// <list type="bullet">
    /// <item>If the run is still live after the enqueue, the loop has not yet reached its
    /// post-run drain point, so it will observe the message. Queued.</item>
    /// <item>If the run has settled, the loop's final drain either already took the message -
    /// in which case <c>TryReclaimFollowUp</c> fails and we correctly report queued - or it did
    /// not, in which case we reclaim it and report not-queued so the caller sends it normally.
    /// The reclaim is done under the queue's own lock, so exactly one of the two wins.</item>
    /// </list>
    /// The message is therefore delivered exactly once and never stranded.
    /// </remarks>
    public Task<bool> TryFollowUpWhileRunningAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (!IsRunning)
            return Task.FromResult(false);

        var queued = new AgentCoreUserMessage(message);

        // Enqueue unconditionally; a PendingMessageQueueFullException from the bounded queue
        // propagates to the caller by design - overflow is a visible refusal, not a drop.
        _agent.FollowUp(queued);

        if (IsRunning)
            return Task.FromResult(true);

        // The run settled between the first check and the enqueue. Either the loop's final drain
        // already claimed our message (reclaim fails -> it IS being delivered) or it did not
        // (reclaim succeeds -> we own it again and the caller must send it normally).
        var reclaimed = _agent.TryReclaimFollowUp(queued);
        return Task.FromResult(!reclaimed);
    }

    /// <inheritdoc />
    /// <remarks>
    /// #2484 typed counterpart: identical enqueue-then-reverify ordering, but the COMPOSED message
    /// is what round-trips through the pending-message queue, so a follow-up issued with draft
    /// attachments still carries them when the queue is drained after the current run settles.
    /// </remarks>
    public Task<bool> TryFollowUpWhileRunningAsync(AgentCoreUserMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!IsRunning)
            return Task.FromResult(false);

        _agent.FollowUp(message);

        if (IsRunning)
            return Task.FromResult(true);

        var reclaimedTyped = _agent.TryReclaimFollowUp(message);
        return Task.FromResult(!reclaimedTyped);
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


