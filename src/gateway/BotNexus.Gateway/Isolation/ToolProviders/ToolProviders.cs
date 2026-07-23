using System.Linq;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Cron;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Isolation.ToolProviders;

/// <summary>
/// Immutable per-<c>CreateAsync</c> inputs shared by every <see cref="IToolProvider"/>. Groups
/// the values a provider needs to decide inclusion and construct its tools without re-reading the
/// service provider (#1382 Finding 1). The <see cref="ResolveConversationId"/> delegate is the
/// memoised resolver from the isolation strategy, so conversation-aware providers reuse the single
/// bound-conversation lookup (#1382 Finding 2) instead of issuing their own store round-trips.
/// </summary>
/// <param name="Descriptor">The agent descriptor being materialised.</param>
/// <param name="ExecutionContext">The execution context (carries the session id, parameters, history).</param>
/// <param name="EffectiveToolIds">The normalised tool-id allowlist (empty = all tools).</param>
/// <param name="ExistingToolNames">Names of tools already selected (workspace + extension), used by
/// gates that must not double-register a tool already contributed elsewhere.</param>
/// <param name="IsSubAgentSession">Whether this session is a sub-agent session (spawn tools are
/// gated closed for sub-agents). Computed once by the strategy including its diagnostics.</param>
/// <param name="PathValidator">The resolved path validator for filesystem-scoped tools.</param>
/// <param name="ResolveConversationId">Memoised bound-conversation resolver keyed on a conversation store.</param>
/// <param name="Logger">The isolation-strategy logger, for provider-level diagnostics.</param>
/// <param name="CancellationToken">Cancellation for async provider work.</param>
internal sealed record ToolProviderContext(
    AgentDescriptor Descriptor,
    AgentExecutionContext ExecutionContext,
    IReadOnlyList<string> EffectiveToolIds,
    IReadOnlySet<string> ExistingToolNames,
    bool IsSubAgentSession,
    IPathValidator PathValidator,
    Func<IConversationStore, Task<ConversationId?>> ResolveConversationId,
    ILogger Logger,
    CancellationToken CancellationToken)
{
    /// <summary>Session id for this materialisation (shortcut over <see cref="ExecutionContext"/>).</summary>
    public SessionId SessionId => ExecutionContext.SessionId;

    /// <summary>Agent id for this materialisation (shortcut over <see cref="Descriptor"/>).</summary>
    public AgentId AgentId => Descriptor.AgentId;

    /// <summary>
    /// True when the named tool passes the effective allowlist: either no allowlist is configured
    /// (all tools) or the allowlist explicitly names the tool (case-insensitive). Mirrors the
    /// per-tool <c>effectiveToolIds.Count == 0 || effectiveToolIds.Contains(name)</c> gate that the
    /// service-locator body used inline.
    /// </summary>
    public bool ToolAllowed(string toolName)
        => EffectiveToolIds.Count == 0
           || EffectiveToolIds.Contains(toolName, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A cohesive unit of agent-tool construction with explicit constructor dependencies (#1382
/// Finding 1). Replaces the Service Locator anti-pattern in <c>InProcessIsolationStrategy.CreateAsync</c>
/// where 23 <c>_serviceProvider.GetService&lt;…&gt;()</c> calls were interleaved with per-tool inclusion
/// gates. Each provider now declares the collaborators it needs (so the DI graph is visible and the
/// provider is independently unit-testable), exposes a <see cref="ShouldInclude"/> gate, and yields
/// its tools from <see cref="CreateToolsAsync"/>. The strategy composes them with
/// <c>providers.Where(p =&gt; p.ShouldInclude(ctx)).SelectMany(p =&gt; p.CreateToolsAsync(ctx))</c>.
/// </summary>
internal interface IToolProvider
{
    /// <summary>
    /// Returns true when this provider should contribute tools for the given context. Encapsulates
    /// the per-tool availability + allowlist gate that previously lived inline in the strategy.
    /// </summary>
    bool ShouldInclude(ToolProviderContext context);

    /// <summary>
    /// Builds this provider's tools. Only called when <see cref="ShouldInclude"/> returned true.
    /// Async because some providers resolve the bound conversation or read the live session.
    /// </summary>
    Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context);
}

/// <summary>
/// Cron scheduling tool (<c>cron</c>). Included when the cron allowlist gate passes and no cron tool
/// was already contributed by the extension registry.
/// </summary>
internal sealed class CronToolProvider(ICronStore? cronStore, CronScheduler? cronScheduler) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context)
    {
        var cronEnabled = context.ToolAllowed("cron");
        var hasCronTool = context.ExistingToolNames.Contains("cron");
        return cronEnabled && !hasCronTool && cronStore is not null && cronScheduler is not null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var allowCrossAgentCron = ResolveAllowCrossAgentCron(context.Descriptor);
        IReadOnlyList<IAgentTool> tools =
            [new CronTool(cronStore!, cronScheduler!, context.AgentId, allowCrossAgentCron)];
        return Task.FromResult(tools);
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
}

/// <summary>
/// Session-management tool (<c>session</c>). Always included when a session store is available; the
/// access level and allowlist come from the descriptor. Intentionally not gated by the tool
/// allowlist — this matches the pre-refactor behaviour.
/// </summary>
internal sealed class SessionToolProvider(ISessionStore? sessionStore) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context) => sessionStore is not null;

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var (level, allowed) = ResolveSessionAccess(context.Descriptor);
        IReadOnlyList<IAgentTool> tools =
            [new SessionTool(sessionStore!, context.AgentId, level, allowed)];
        return Task.FromResult(tools);
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
}

/// <summary>
/// Conversation-management tool (<c>conversation</c>). Included when a conversation store is
/// available; resolves the bound conversation via the memoised resolver. Not gated by the tool
/// allowlist — matches the pre-refactor behaviour.
/// </summary>
internal sealed class ConversationToolProvider(
    IConversationStore? conversationStore,
    ISessionStore? sessionStore,
    IConversationChangeNotifier? conversationChangeNotifier,
    IInboundMessageOrchestrator? messageOrchestrator,
    IConversationRouter? conversationRouter) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context) => conversationStore is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var conversationId = await context.ResolveConversationId(conversationStore!).ConfigureAwait(false);
        var (level, allowed) = ResolveConversationAccess(context.Descriptor);
        return
        [
            new ConversationTool(
                conversationStore!,
                context.AgentId,
                conversationId,
                level,
                allowed,
                sessionStore,
                messageOrchestrator,
                conversationChangeNotifier,
                conversationRouter)
        ];
    }

    internal static (ConversationAccessLevel level, IReadOnlyList<string>? allowedAgents) ResolveConversationAccess(AgentDescriptor descriptor)
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
}

/// <summary>
/// Ask-user tool (<c>ask_user</c>). Included when the allowlist permits it and an ask-user response
/// registry is available.
/// </summary>
internal sealed class AskUserToolProvider(
    IAskUserResponseRegistry? askUserRegistry,
    IConversationStore? conversationStore,
    ISessionStore? sessionStore) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context)
        => context.ToolAllowed("ask_user") && askUserRegistry is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var askUserConversationId = conversationStore is not null
            ? await context.ResolveConversationId(conversationStore).ConfigureAwait(false)
            : null;
        return
        [
            new AskUserTool(
                askUserRegistry!,
                context.AgentId,
                context.SessionId,
                askUserConversationId,
                conversationStore)
        ];
    }

    // sessionStore is retained as an explicit dependency for parity with the pre-refactor wiring
    // (the ask-user block resolved the conversation id via the session store); the memoised resolver
    // already closes over it, so it is not passed to the tool directly.
    private ISessionStore? SessionStore => sessionStore;
}

/// <summary>
/// Always-on delay tool (<c>delay</c>). Never gated — mirrors the unconditional registration in the
/// pre-refactor body.
/// </summary>
internal sealed class DelayToolProvider(IOptions<DelayToolOptions>? delayToolOptions) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context) => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var options = delayToolOptions ?? Options.Create(new DelayToolOptions());
        IReadOnlyList<IAgentTool> tools = [new DelayTool(options)];
        return Task.FromResult(tools);
    }
}

/// <summary>
/// Always-on date/time tool (<c>datetime</c>). Never gated. Timezone falls back from the agent soul
/// to the gateway default timezone.
/// </summary>
internal sealed class DateTimeToolProvider(IOptions<PlatformConfig>? platformConfig) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context) => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var serverTimezone = platformConfig?.Value.Gateway?.DefaultTimezone;
        IReadOnlyList<IAgentTool> tools = [new DateTimeTool(context.Descriptor.Soul?.Timezone ?? serverTimezone)];
        return Task.FromResult(tools);
    }
}

/// <summary>
/// Always-on file-watcher tool (<c>watch_file</c>). Never gated; scoped by the resolved path validator.
/// </summary>
internal sealed class FileWatcherToolProvider(IOptions<FileWatcherToolOptions>? fileWatcherToolOptions) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context) => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var options = fileWatcherToolOptions ?? Options.Create(new FileWatcherToolOptions());
        IReadOnlyList<IAgentTool> tools = [new FileWatcherTool(options, context.PathValidator)];
        return Task.FromResult(tools);
    }
}

/// <summary>
/// Always-on AGENTS.md discovery tool (<c>get_agent_files</c>). Pull-based convention loading; never
/// gated. Scoped by the path validator.
/// </summary>
internal sealed class AgentFilesToolProvider(System.IO.Abstractions.IFileSystem? fileSystem) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context) => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        IReadOnlyList<IAgentTool> tools = [new AgentFilesTool(context.PathValidator, fileSystem)];
        return Task.FromResult(tools);
    }
}

/// <summary>
/// Sub-agent orchestration tools (<c>spawn_subagent</c>, <c>list_subagents</c>, <c>manage_subagent</c>).
/// Gated closed for sub-agent sessions and when the sub-agent manager is unavailable or max depth is
/// zero. The spawn tool additionally requires a bound conversation so spawned sessions stay visible
/// in the portal thread. The typed/substring sub-agent diagnostics remain in the strategy.
/// </summary>
internal sealed class SubAgentToolProvider(
    ISubAgentManager? subAgentManager,
    IOptions<GatewayOptions>? gatewayOptions,
    IConversationStore? conversationStore,
    ISessionStore? sessionStore) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context)
        => subAgentManager is not null
           && gatewayOptions?.Value.SubAgents is { MaxDepth: > 0 }
           && !context.IsSubAgentSession;

    /// <inheritdoc />
    public async Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var tools = new List<IAgentTool>();

        var includeSpawn = context.ToolAllowed("spawn_subagent");
        var includeList = context.ToolAllowed("list_subagents");
        var includeManage = context.ToolAllowed("manage_subagent");

        if (includeSpawn)
        {
            var spawnConversationId = conversationStore is not null
                ? await context.ResolveConversationId(conversationStore).ConfigureAwait(false)
                : null;
            if (spawnConversationId is { } resolvedSpawnConversationId)
            {
                tools.Add(new SubAgentSpawnTool(subAgentManager!, context.AgentId, context.SessionId, resolvedSpawnConversationId));
            }
            else
            {
                context.Logger.LogInformation(
                    "Skipping spawn_subagent tool for session '{SessionId}' (agent '{AgentId}'): no conversation is bound to this session. " +
                    "Sub-agent sessions must inherit a parent conversation to remain visible in the portal thread.",
                    context.SessionId,
                    context.AgentId);
            }
        }
        if (includeList)
            tools.Add(new SubAgentListTool(subAgentManager!, context.SessionId));
        if (includeManage)
            tools.Add(new SubAgentManageTool(subAgentManager!, context.SessionId));

        return tools;
    }

    private ISessionStore? SessionStore => sessionStore;
}

/// <summary>
/// Agent-to-agent converse tool (<c>agent_converse</c>). Requires an exchange service and a session
/// store, plus the allowlist gate.
/// </summary>
internal sealed class AgentConverseToolProvider(
    IAgentExchangeService? conversationService,
    ISessionStore? sessionStore,
    IOptions<AgentExchangeOptions>? exchangeOptions) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context)
        => conversationService is not null && sessionStore is not null && context.ToolAllowed("agent_converse");

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        IReadOnlyList<IAgentTool> tools =
        [
            new AgentConverseTool(conversationService!, sessionStore!, context.AgentId, context.SessionId, exchangeOptions?.Value)
        ];
        return Task.FromResult(tools);
    }
}

/// <summary>
/// Finish-agent-exchange system control tool (<c>finish_agent_exchange</c>). Registered only for
/// agent-to-agent sessions and intentionally bypasses the tool allowlist (it is a system control
/// tool, not an agent-configured capability).
/// </summary>
internal sealed class FinishAgentExchangeToolProvider(ISessionStore? sessionStore) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context) => sessionStore is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var session = await sessionStore!.GetAsync(context.SessionId, context.CancellationToken).ConfigureAwait(false);
        if (session is not null && session.SessionType == SessionType.AgentAgent)
        {
            return [new FinishAgentExchangeTool(sessionStore!, context.SessionId)];
        }
        return [];
    }
}

/// <summary>
/// List-agents tool (<c>list_agents</c>). Requires an agent registry and the allowlist gate.
/// </summary>
internal sealed class ListAgentsToolProvider(
    IAgentRegistry? agentRegistry,
    IOptions<AgentExchangeOptions>? exchangeOptions) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context)
        => agentRegistry is not null && context.ToolAllowed("list_agents");

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        IReadOnlyList<IAgentTool> tools =
            [new ListAgentsTool(agentRegistry!, context.AgentId, exchangeOptions?.Value)];
        return Task.FromResult(tools);
    }
}

/// <summary>
/// Agent lifecycle tools (<c>create_agent</c>, <c>update_agent</c>). Requires the registry,
/// configuration writer, and BotNexus home; each tool has its own allowlist sub-gate.
/// </summary>
internal sealed class AgentManagementToolProvider(
    IAgentRegistry? agentRegistry,
    IAgentConfigurationWriter? configurationWriter,
    BotNexusHome? botNexusHome,
    IEnumerable<IAgentChangeNotifier> changeNotifiers,
    ApiProviderRegistry? apiProviderRegistry,
    IOptions<PlatformConfig>? platformConfigOptions,
    LlmClient llmClient) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context)
        => agentRegistry is not null && configurationWriter is not null && botNexusHome is not null;

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        var tools = new List<IAgentTool>();

        if (context.ToolAllowed("create_agent"))
        {
            tools.Add(new CreateAgentTool(
                agentRegistry!, configurationWriter!, changeNotifiers, botNexusHome!,
                platformConfigOptions, apiProviderRegistry, llmClient.Models));
        }

        if (context.ToolAllowed("update_agent"))
        {
            tools.Add(new UpdateAgentTool(
                agentRegistry!, configurationWriter!, changeNotifiers, apiProviderRegistry, llmClient.Models));
        }

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}

/// <summary>
/// Canvas tool (<c>canvas</c>). Allowlist-gated. Resolves the bound conversation so the canvas is
/// anchored to it; no-ops safely when there is no conversation context.
/// </summary>
internal sealed class CanvasToolProvider(
    IConversationStore? conversationStore,
    IEnumerable<IAgentCanvasNotifier> canvasNotifiers) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context) => context.ToolAllowed("canvas");

    /// <inheritdoc />
    public async Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        ConversationId? canvasConversationId = null;
        if (conversationStore is not null)
        {
            canvasConversationId = await context.ResolveConversationId(conversationStore).ConfigureAwait(false);
        }
        return [new CanvasTool(context.AgentId, canvasConversationId, conversationStore, canvasNotifiers.ToArray())];
    }
}

/// <summary>
/// Per-conversation todo tool (<c>todo</c>). Allowlist-gated (#1464). Resolved per conversation
/// exactly like the canvas tool so the plan state is anchored to the bound conversation.
/// </summary>
internal sealed class TodoToolProvider(
    IConversationStore? conversationStore,
    IEnumerable<IAgentTodoNotifier> todoNotifiers) : IToolProvider
{
    /// <inheritdoc />
    public bool ShouldInclude(ToolProviderContext context) => context.ToolAllowed("todo");

    /// <inheritdoc />
    public async Task<IReadOnlyList<IAgentTool>> CreateToolsAsync(ToolProviderContext context)
    {
        ConversationId? todoConversationId = null;
        if (conversationStore is not null)
        {
            todoConversationId = await context.ResolveConversationId(conversationStore).ConfigureAwait(false);
        }
        return [new TodoTool(todoConversationId, conversationStore, context.AgentId, todoNotifiers.ToArray())];
    }
}
