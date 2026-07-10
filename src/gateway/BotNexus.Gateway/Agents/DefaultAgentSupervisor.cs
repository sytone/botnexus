using System.Diagnostics;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default agent supervisor — manages agent instance lifecycle using isolation strategies.
/// </summary>
public sealed class DefaultAgentSupervisor : IAgentSupervisor, IAgentHandleInspector
{
    private readonly IAgentRegistry _registry;
    private readonly IReadOnlyDictionary<string, IIsolationStrategy> _strategies;
    private readonly ISessionStore _sessionStore;
    private readonly Dictionary<AgentSessionKey, (AgentInstance Instance, IAgentHandle Handle, AgentDescriptor Descriptor)> _instances = [];
    private readonly Dictionary<AgentSessionKey, Task<(AgentInstance Instance, IAgentHandle Handle, AgentDescriptor Descriptor)>> _pendingCreates = [];
    private readonly Lock _sync = new();
    private readonly ILogger<DefaultAgentSupervisor> _logger;

    public DefaultAgentSupervisor(
        IAgentRegistry registry,
        IEnumerable<IIsolationStrategy> strategies,
        ISessionStore sessionStore,
        ILogger<DefaultAgentSupervisor> logger)
    {
        _registry = registry;
        _strategies = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _sessionStore = sessionStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IAgentHandle> GetOrCreateAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = GatewayDiagnostics.Source.StartActivity("gateway.agent_lifecycle", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", agentId);
        activity?.SetTag("botnexus.session.id", sessionId);
        activity?.SetTag("botnexus.correlation.id", System.Diagnostics.Activity.Current?.TraceId.ToString());

        var baseDescriptor = _registry.Get(agentId)
            ?? throw new KeyNotFoundException($"Agent '{agentId}' is not registered.");
        var existingSession = await _sessionStore.GetAsync(sessionId, cancellationToken);
        var descriptor = ResolveDescriptorForSession(baseDescriptor, existingSession);
        var key = AgentSessionKey.From(agentId, sessionId);
        Task<(AgentInstance Instance, IAgentHandle Handle, AgentDescriptor Descriptor)> creationTask;
        TaskCompletionSource<(AgentInstance Instance, IAgentHandle Handle, AgentDescriptor Descriptor)>? creationCompletion = null;

        lock (_sync)
        {
            if (_instances.TryGetValue(key, out var existing) && existing.Instance.Status is AgentInstanceStatus.Idle or AgentInstanceStatus.Running)
            {
                // Invalidate if the agent descriptor changed (e.g., provider, model, tools)
                if (existing.Descriptor == descriptor)
                    return existing.Handle;

                _logger.LogInformation(
                    "Agent descriptor changed for '{AgentId}' session '{SessionId}' — recreating instance",
                    agentId, sessionId);
                _instances.Remove(key);
                _ = DisposeHandleAsync(existing.Handle);
            }

            if (!_pendingCreates.TryGetValue(key, out creationTask!))
            {
                if (descriptor.MaxConcurrentSessions > 0)
                {
                    var activeSessions = CountActiveSessionsForAgent(agentId);
                    if (activeSessions >= descriptor.MaxConcurrentSessions)
                        throw new AgentConcurrencyLimitExceededException(agentId, descriptor.MaxConcurrentSessions);
                }

                creationCompletion = new TaskCompletionSource<(AgentInstance Instance, IAgentHandle Handle, AgentDescriptor Descriptor)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                creationTask = creationCompletion.Task;
                _pendingCreates[key] = creationTask;
            }
        }

        if (creationCompletion is null)
        {
            var existingCreate = await creationTask.WaitAsync(cancellationToken);
            return existingCreate.Handle;
        }

        try
        {
            var created = await CreateEntryAsync(descriptor, sessionId, key, existingSession, cancellationToken);
            lock (_sync)
            {
                _pendingCreates.Remove(key);
                _instances[key] = created;
            }
            creationCompletion.SetResult(created);

            _logger.LogInformation("Created agent instance '{AgentId}' for session '{SessionId}' (isolation: {Strategy})",
                agentId, sessionId, created.Instance.IsolationStrategy);

            return created.Handle;
        }
        catch (Exception ex)
        {
            lock (_sync) _pendingCreates.Remove(key);
            creationCompletion.SetException(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        var key = AgentSessionKey.From(agentId, sessionId);
        (AgentInstance Instance, IAgentHandle Handle, AgentDescriptor Descriptor) entry;

        lock (_sync)
        {
            if (!_instances.Remove(key, out entry))
                return;
        }

        entry.Instance.Status = AgentInstanceStatus.Stopping;
        await entry.Handle.DisposeAsync();
        entry.Instance.Status = AgentInstanceStatus.Stopped;

        _logger.LogInformation("Stopped agent instance '{AgentId}' for session '{SessionId}'", agentId, sessionId);
    }

    /// <inheritdoc />
    public AgentInstance? GetInstance(AgentId agentId, SessionId sessionId)
    {
        lock (_sync) return _instances.GetValueOrDefault(AgentSessionKey.From(agentId, sessionId)).Instance;
    }

    /// <inheritdoc />
    public IAgentHandle? GetHandle(AgentId agentId, SessionId sessionId)
    {
        lock (_sync) return _instances.GetValueOrDefault(AgentSessionKey.From(agentId, sessionId)).Handle;
    }

    /// <inheritdoc />
    public IAgentTool? ResolveTool(AgentId agentId, SessionId sessionId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return null;

        var handle = GetHandle(agentId, sessionId);
        return handle is IAgentHandleInspector inspector
            ? inspector.ResolveTool(agentId, sessionId, toolName)
            : null;
    }

    /// <inheritdoc />
    public ContextDiagnostics? GetContextDiagnostics() => null;

    /// <inheritdoc />
    public IReadOnlyList<AgentInstance> GetAllInstances()
    {
        lock (_sync) return _instances.Values.Select(e => e.Instance).ToList();
    }

    /// <inheritdoc />
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        List<(AgentInstance Instance, IAgentHandle Handle, AgentDescriptor Descriptor)> entries;
        lock (_sync)
        {
            entries = [.. _instances.Values];
            _instances.Clear();
            _pendingCreates.Clear();
        }

        foreach (var (instance, handle, _) in entries)
        {
            instance.Status = AgentInstanceStatus.Stopping;
            try { await handle.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping agent instance '{InstanceId}'", instance.InstanceId); }
            instance.Status = AgentInstanceStatus.Stopped;
        }
    }

    private int CountActiveSessionsForAgent(AgentId agentId)
    {
        var activeInstanceKeys = _instances
            .Where(pair =>
                string.Equals(pair.Value.Instance.AgentId.Value, agentId.Value, StringComparison.OrdinalIgnoreCase) &&
                pair.Value.Instance.Status is not AgentInstanceStatus.Stopped and not AgentInstanceStatus.Faulted)
            .Select(pair => pair.Key.SessionId);

        var pendingKeys = _pendingCreates.Keys.Where(key => string.Equals(key.AgentId.Value, agentId.Value, StringComparison.OrdinalIgnoreCase))
            .Select(key => key.SessionId);

        return activeInstanceKeys
            .Concat(pendingKeys)
            .Distinct()
            .Count();
    }

    private async Task<(AgentInstance Instance, IAgentHandle Handle, AgentDescriptor Descriptor)> CreateEntryAsync(
        AgentDescriptor descriptor,
        SessionId sessionId,
        AgentSessionKey key,
        GatewaySession? existingSession,
        CancellationToken cancellationToken)
    {
        var descriptorErrors = AgentDescriptorValidator.Validate(descriptor, _strategies.Keys);
        if (descriptorErrors.Count > 0)
            throw new InvalidOperationException(
                $"Agent '{descriptor.AgentId}' configuration is invalid: {string.Join("; ", descriptorErrors)}");

        if (!_strategies.TryGetValue(descriptor.IsolationStrategy, out var strategy))
        {
            var availableStrategies = string.Join(", ", _strategies.Keys.Order(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"Isolation strategy '{descriptor.IsolationStrategy}' is not registered for agent '{descriptor.AgentId}'. " +
                $"Available strategies: {availableStrategies}.");
        }

        IReadOnlyList<SessionEntry> priorHistory = [];
        if (existingSession?.History.Count > 0)
        {
            priorHistory = existingSession.History;
            _logger.LogInformation("Resuming session '{SessionId}' with {Count} history entries",
                sessionId, priorHistory.Count);
        }

        var context = new AgentExecutionContext
        {
            SessionId = sessionId,
            History = priorHistory,
            Parameters = BuildExecutionParameters(existingSession)
        };
        var handle = await strategy.CreateAsync(descriptor, context, cancellationToken);

        // Stamp the last rendered system prompt on the session so the debug inspector
        // can surface it. InProcessAgentHandle exposes RenderedSystemPrompt as an
        // internal property; other strategies don't render prompts client-side.
        if (existingSession is not null && handle is InProcessAgentHandle inProcess
            && inProcess.RenderedSystemPrompt is { Length: > 0 })
        {
            existingSession.LastRenderedSystemPrompt = inProcess.RenderedSystemPrompt;
            existingSession.LastRenderedSystemPromptAt = DateTimeOffset.UtcNow;
        }

        var instance = new AgentInstance
        {
            InstanceId = key.ToString(),
            AgentId = descriptor.AgentId,
            SessionId = sessionId,
            IsolationStrategy = descriptor.IsolationStrategy,
            Status = AgentInstanceStatus.Idle
        };

        return (instance, handle, descriptor);
    }

    private AgentDescriptor ResolveDescriptorForSession(AgentDescriptor descriptor, GatewaySession? session)
    {
        if (session is null
            || !TryGetMetadataString(session, "modelOverride", out var rawOverride)
            || string.IsNullOrWhiteSpace(rawOverride))
        {
            return descriptor;
        }

        var trimmed = rawOverride.Trim();
        var provider = descriptor.ApiProvider;
        var model = trimmed;
        var separator = trimmed.IndexOf('/');
        if (separator > 0 && separator < trimmed.Length - 1)
        {
            provider = trimmed[..separator];
            model = trimmed[(separator + 1)..];
        }

        if (descriptor.AllowedModelIds.Count > 0
            && !descriptor.AllowedModelIds.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId}' requested model '{model}', but it is not allowed for agent '{descriptor.AgentId}'.");
        }

        if (string.Equals(provider, descriptor.ApiProvider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(model, descriptor.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return descriptor;
        }

        _logger.LogInformation(
            "Applying per-session model override for agent '{AgentId}' session '{SessionId}': {Provider}/{Model}",
            descriptor.AgentId,
            session.SessionId,
            provider,
            model);

        return descriptor with
        {
            ApiProvider = provider,
            ModelId = model
        };
    }

    private static bool TryGetMetadataString(GatewaySession session, string key, out string value)
    {
        value = string.Empty;
        if (!session.Metadata.TryGetValue(key, out var raw) || raw is null)
            return false;

        value = raw.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Builds the execution-context parameter bag from session metadata. Currently carries the
    /// connecting client kind ("mobile" vs "desktop") so the context builder can surface it on
    /// the runtime line (#1209). Returns an empty dictionary when the session has no such metadata
    /// so the no-hint path is unchanged.
    /// </summary>
    /// <param name="session">The resuming session, or <see langword="null"/> for a fresh session.</param>
    /// <returns>A parameter dictionary; empty when no relevant metadata is present.</returns>
    private static IReadOnlyDictionary<string, object?> BuildExecutionParameters(GatewaySession? session)
    {
        var parameters = new Dictionary<string, object?>();
        if (session is not null && TryGetMetadataString(session, "clientKind", out var clientKind))
        {
            parameters["clientKind"] = clientKind;
        }

        // PBI3 #1851: surface the originating channel type so the isolation strategy can
        // tag hot-path turn metrics (botnexus.turns.total{channel=...}) with a bounded,
        // low-cardinality channel dimension. Absent for non-channel-bound sessions.
        if (session?.ChannelType is { } channelType && !string.IsNullOrWhiteSpace(channelType.Value))
        {
            parameters["channel"] = channelType.Value;
        }

        return parameters;
    }

    private async Task DisposeHandleAsync(IAgentHandle handle)
    {
        try { await handle.DisposeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error disposing stale agent handle during descriptor refresh"); }
    }
}
