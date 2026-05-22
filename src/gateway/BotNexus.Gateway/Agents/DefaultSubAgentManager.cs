using BotNexus.Gateway.Abstractions.Sessions;
using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Security;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default in-memory orchestrator for background sub-agent sessions.
/// </summary>
public sealed class DefaultSubAgentManager : ISubAgentManager
{
    private readonly IAgentSupervisor _supervisor;
    private readonly IAgentRegistry _registry;
    private readonly IActivityBroadcaster _activity;
    private readonly IChannelDispatcher _dispatcher;
    private readonly IAgentWorkspaceManager? _workspaceManager;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly ILogger<DefaultSubAgentManager> _logger;
    private readonly DefaultToolPolicyProvider? _policyProvider;
    private readonly ISessionStore? _sessionStore;
    private readonly ConcurrentDictionary<string, SubAgentInfo> _subAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<SessionId, ConcurrentBag<string>> _parentChildren = [];
    private readonly ConcurrentDictionary<string, AgentId> _parentAgentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentId> _childAgentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _timeouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _processedCompletions = new(StringComparer.OrdinalIgnoreCase);

    public DefaultSubAgentManager(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        IActivityBroadcaster activity,
        IChannelDispatcher dispatcher,
        IOptionsMonitor<GatewayOptions> options,
        ILogger<DefaultSubAgentManager> logger,
        IAgentWorkspaceManager? workspaceManager = null,
        DefaultToolPolicyProvider? policyProvider = null,
        ISessionStore? sessionStore = null)
    {
        _supervisor = supervisor;
        _registry = registry;
        _activity = activity;
        _dispatcher = dispatcher;
        _workspaceManager = workspaceManager;
        _options = options;
        _logger = logger;
        _policyProvider = policyProvider;
        _sessionStore = sessionStore;
    }

    /// <inheritdoc />
    public async Task<SubAgentInfo> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parentDescriptor = _registry.Get(request.ParentAgentId)
            ?? throw new KeyNotFoundException($"Parent agent '{request.ParentAgentId}' is not registered.");

        // Enforce spawn depth limit
        var maxDepth = _options.CurrentValue.SubAgents.MaxDepth;
        if (maxDepth > 0)
        {
            var depth = CountSubAgentDepth(request.ParentSessionId);
            if (depth >= maxDepth)
                throw new InvalidOperationException(
                    $"Cannot spawn sub-agent: parent session depth {depth} has reached the maximum depth of {maxDepth}.");
        }

        // Validate tool grants against parent's deny-list (privilege escalation prevention)
        if (_policyProvider is not null && request.ToolIds is { Count: > 0 })
        {
            var parentDenyList = _policyProvider.GetEffectiveDenyList(request.ParentAgentId.Value);
            if (parentDenyList.Count > 0)
            {
                var denied = request.ToolIds
                    .Where(t => parentDenyList.Contains(t, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (denied.Count > 0)
                    throw new InvalidOperationException(
                        $"Sub-agent cannot be granted tools denied to the parent: {string.Join(", ", denied)}");
            }
        }

        var maxConcurrent = _options.CurrentValue.SubAgents.MaxConcurrentPerSession;
        if (maxConcurrent > 0)
        {
            var activeCount = _subAgents.Values.Count(info =>
                string.Equals(info.ParentSessionId, request.ParentSessionId, StringComparison.OrdinalIgnoreCase) &&
                info.Status == SubAgentStatus.Running);

            if (activeCount >= maxConcurrent)
                throw new InvalidOperationException(
                    $"Parent session '{request.ParentSessionId}' already has {activeCount} running sub-agents; maximum is {maxConcurrent}.");
        }

        var uniqueId = Guid.NewGuid().ToString("N");
        var archetype = request.Archetype ?? SubAgentArchetype.General;
        var childSessionId = SessionId.ForSubAgent(request.ParentSessionId, uniqueId);
        var subAgentId = uniqueId;
        var childAgentId = AgentId.From($"{request.ParentAgentId}--subagent--{archetype.Value}--{uniqueId}");

        AgentDescriptor baseDescriptor;
        if (!string.IsNullOrWhiteSpace(request.TargetAgentId))
        {
            var targetId = AgentId.From(request.TargetAgentId);
            baseDescriptor = _registry.Get(targetId)
                ?? throw new KeyNotFoundException($"Target agent '{request.TargetAgentId}' is not registered.");
        }
        else
        {
            baseDescriptor = parentDescriptor;
        }

        if (!_registry.Contains(childAgentId))
        {
            var childFileAccess = MergeFileAccess(
                parentDescriptor.FileAccess,
                baseDescriptor.FileAccess,
                request.AdditionalReadPaths,
                request.AdditionalWritePaths);

            _registry.Register(baseDescriptor with
            {
                AgentId = childAgentId,
                DisplayName = $"{baseDescriptor.DisplayName} ({archetype.Value})",
                FileAccess = childFileAccess
            });
        }

        var handle = await _supervisor.GetOrCreateAsync(childAgentId, childSessionId, ct);

        var configuredDefaultModel = string.IsNullOrWhiteSpace(_options.CurrentValue.SubAgents.DefaultModel)
            ? null
            : _options.CurrentValue.SubAgents.DefaultModel;

        var info = new SubAgentInfo
        {
            SubAgentId = subAgentId,
            ParentSessionId = request.ParentSessionId,
            ChildSessionId = childSessionId,
            Name = request.Name,
            Task = request.Task,
            Model = request.ModelOverride ?? configuredDefaultModel ?? parentDescriptor.ModelId,
            Archetype = archetype,
            Status = SubAgentStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            TurnsUsed = 0
        };

        if (!_subAgents.TryAdd(subAgentId, info))
            throw new InvalidOperationException($"Sub-agent '{subAgentId}' already exists.");

        _parentAgentIds[subAgentId] = request.ParentAgentId;
        _childAgentIds[subAgentId] = childAgentId;
        _parentChildren.GetOrAdd(request.ParentSessionId, _ => []).Add(subAgentId);

        // Register inherited deny-list so the child agent can't invoke parent-denied tools
        if (_policyProvider is not null)
        {
            var effectiveDenyList = _policyProvider.GetEffectiveDenyList(request.ParentAgentId.Value);
            if (effectiveDenyList.Count > 0)
                _policyProvider.SetDynamicDenyList(childAgentId, effectiveDenyList);
        }

        var timeoutSeconds = request.TimeoutSeconds > 0
            ? request.TimeoutSeconds
            : _options.CurrentValue.SubAgents.DefaultTimeoutSeconds;
        if (timeoutSeconds <= 0)
            timeoutSeconds = 1;

        var timeoutCts = new CancellationTokenSource();
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        _timeouts[subAgentId] = timeoutCts;

        _ = Task.Run(() => RunSubAgentAsync(subAgentId, handle, request.Task, timeoutSeconds, request.InheritedConversationId), CancellationToken.None);

        _logger.LogInformation(
            "Spawned sub-agent '{SubAgentId}' for parent session '{ParentSessionId}' in child session '{ChildSessionId}'.",
            subAgentId,
            request.ParentSessionId,
            childSessionId);

        await PublishLifecycleActivityAsync(
            GatewayActivityType.SubAgentSpawned,
            "subagent_spawned",
            info,
            request.ParentAgentId,
            $"Sub-agent '{subAgentId}' spawned.");

        return info;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubAgentInfo>> ListAsync(SessionId parentSessionId, CancellationToken ct = default)
    {
        if (!_parentChildren.TryGetValue(parentSessionId, out var subAgentIds))
            return Task.FromResult<IReadOnlyList<SubAgentInfo>>([]);

        var results = subAgentIds
            .Select(id => _subAgents.TryGetValue(id, out var info) ? info : null)
            .Where(info => info is not null)
            .Select(info => info!)
            .OrderBy(info => info.StartedAt)
            .ToArray();

        return Task.FromResult<IReadOnlyList<SubAgentInfo>>(results);
    }

    /// <inheritdoc />
    public Task<SubAgentInfo?> GetAsync(string subAgentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);
        return Task.FromResult(_subAgents.TryGetValue(subAgentId, out var info) ? info : null);
    }

    /// <inheritdoc />
    public async Task<bool> KillAsync(string subAgentId, SessionId requestingSessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);

        if (!_subAgents.TryGetValue(subAgentId, out var info))
            return false;

        if (!string.Equals(info.ParentSessionId, requestingSessionId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (info.Status is SubAgentStatus.Completed or SubAgentStatus.Failed or SubAgentStatus.Killed or SubAgentStatus.TimedOut)
            return false;

        if (_timeouts.TryRemove(subAgentId, out var timeoutCts))
        {
            timeoutCts.Cancel();
            timeoutCts.Dispose();
        }

        await CleanupChildAgentAsync(subAgentId, info.ChildSessionId, ct);

        if (!TryUpdateSubAgent(
            subAgentId,
            current => current with
            {
                Status = SubAgentStatus.Killed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultSummary = "Sub-agent was killed by parent session."
            },
            out var updatedInfo))
        {
            return false;
        }

        _logger.LogInformation(
            "Killed sub-agent '{SubAgentId}' for parent session '{ParentSessionId}'.",
            subAgentId,
            requestingSessionId);

        _parentAgentIds.TryGetValue(subAgentId, out var parentAgentId);
        await PublishLifecycleActivityAsync(
            GatewayActivityType.SubAgentKilled,
            "subagent_killed",
            updatedInfo,
            parentAgentId,
            $"Sub-agent '{subAgentId}' was killed.");

        return true;
    }

    /// <inheritdoc />
    public async Task OnCompletedAsync(string subAgentId, string resultSummary, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);

        if (!_subAgents.TryGetValue(subAgentId, out var existing))
            return;

        if (!_processedCompletions.TryAdd(subAgentId, 0))
        {
            _logger.LogDebug("Skipping duplicate completion for sub-agent '{SubAgentId}'.", subAgentId);
            return;
        }

        if (existing.Status == SubAgentStatus.Killed)
            return;

        var normalizedSummary = string.IsNullOrWhiteSpace(resultSummary)
            ? "Sub-agent completed with no summary."
            : resultSummary;

        var completionStatus = existing.Status == SubAgentStatus.Running
            ? SubAgentStatus.Completed
            : existing.Status;

        if (!TryUpdateSubAgent(
                subAgentId,
                current => current with
                {
                    Status = completionStatus,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ResultSummary = normalizedSummary
                },
                out var updated))
        {
            return;
        }

        _parentAgentIds.TryGetValue(subAgentId, out var parentAgentId);
        if (updated.Status == SubAgentStatus.Completed)
        {
            await PublishLifecycleActivityAsync(
                GatewayActivityType.SubAgentCompleted,
                "subagent_completed",
                updated,
                parentAgentId,
                $"Sub-agent '{subAgentId}' completed.");
        }
        else if (updated.Status is SubAgentStatus.Failed or SubAgentStatus.TimedOut)
        {
            await PublishLifecycleActivityAsync(
                GatewayActivityType.SubAgentFailed,
                "subagent_failed",
                updated,
                parentAgentId,
                $"Sub-agent '{subAgentId}' failed.");
        }

        if (string.IsNullOrWhiteSpace(parentAgentId))
            return;

        var completionMessage = new SubAgentCompletionMessage
        {
            SubAgentId = subAgentId,
            Status = DescribeStatus(updated.Status),
            Summary = normalizedSummary,
            CompletedAt = updated.CompletedAt ?? DateTimeOffset.UtcNow
        };

        var followUp = completionMessage.Content;

        try
        {
            _logger.LogInformation(
                "Dispatching sub-agent completion for parent agent '{ParentAgentId}' session '{ParentSessionId}' from sub-agent '{SubAgentId}'.",
                parentAgentId,
                updated.ParentSessionId,
                subAgentId);

            GatewayTelemetry.SubAgentParentWakeups.Add(1,
                new KeyValuePair<string, object?>("botnexus.parent.agent.id", parentAgentId),
                new KeyValuePair<string, object?>("botnexus.parent.session.id", updated.ParentSessionId),
                new KeyValuePair<string, object?>("botnexus.subagent.id", subAgentId));
            GatewayTelemetry.SubAgentWakeDispatched.Add(1,
                new KeyValuePair<string, object?>("botnexus.parent.agent.id", parentAgentId),
                new KeyValuePair<string, object?>("botnexus.parent.session.id", updated.ParentSessionId),
                new KeyValuePair<string, object?>("botnexus.subagent.id", subAgentId));

            await _dispatcher.DispatchAsync(new InboundMessage
            {
                ChannelType = ChannelKey.From("internal"),
                SenderId = $"subagent:{subAgentId}",
                ChannelAddress = ChannelAddress.From(updated.ParentSessionId.Value),
                SessionId = updated.ParentSessionId,
                TargetAgentId = parentAgentId,
                Content = followUp,
                Metadata = new Dictionary<string, object?>
                {
                    ["messageType"] = "subagent-completion",
                    ["subAgentId"] = subAgentId
                }
            }, ct);
        }
        catch (Exception ex)
        {
            GatewayTelemetry.SubAgentWakeDeliveryFailed.Add(1,
                new KeyValuePair<string, object?>("botnexus.parent.agent.id", parentAgentId),
                new KeyValuePair<string, object?>("botnexus.parent.session.id", updated.ParentSessionId),
                new KeyValuePair<string, object?>("botnexus.subagent.id", subAgentId));
            _logger.LogWarning(
                ex,
                "Failed delivering completion follow-up for sub-agent '{SubAgentId}' to parent session '{ParentSessionId}'.",
                subAgentId,
                updated.ParentSessionId);
        }
        finally
        {
            await CleanupChildAgentAsync(subAgentId, updated.ChildSessionId, CancellationToken.None);
        }
    }
    private async Task RunSubAgentAsync(string subAgentId, IAgentHandle handle, string task, int timeoutSeconds, string? inheritedConversationId = null)
    {
        if (!_timeouts.TryGetValue(subAgentId, out var timeoutCts))
            return;

        try
        {
            // Pin the sub-agent session to the parent conversation before running (#468).
            if (!string.IsNullOrWhiteSpace(inheritedConversationId) && _sessionStore is not null)
            {
                var session = await _sessionStore.GetAsync(handle.SessionId, timeoutCts.Token).ConfigureAwait(false);
                if (session is not null)
                {
                    session.Session.ConversationId = new BotNexus.Domain.Primitives.ConversationId(inheritedConversationId);
                    await _sessionStore.SaveAsync(session, timeoutCts.Token).ConfigureAwait(false);
                }
            }

            var response = await handle.PromptAsync(task, timeoutCts.Token);
            await OnCompletedAsync(subAgentId, response.Content);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            if (TryUpdateSubAgent(
                    subAgentId,
                    current => current.Status == SubAgentStatus.Killed
                        ? current
                        : current with
                        {
                            Status = SubAgentStatus.TimedOut,
                            CompletedAt = DateTimeOffset.UtcNow,
                            ResultSummary = $"Sub-agent timed out after {timeoutSeconds} seconds."
                        }))
            {
                await OnCompletedAsync(subAgentId, $"Sub-agent timed out after {timeoutSeconds} seconds.");
            }
        }
        catch (Exception ex)
        {
            if (TryUpdateSubAgent(
                    subAgentId,
                    current => current.Status == SubAgentStatus.Killed
                        ? current
                        : current with
                        {
                            Status = SubAgentStatus.Failed,
                            CompletedAt = DateTimeOffset.UtcNow,
                            ResultSummary = $"Sub-agent failed: {ex.Message}"
                        }))
            {
                await OnCompletedAsync(subAgentId, $"Sub-agent failed: {ex.Message}");
            }
        }
        finally
        {
            if (_timeouts.TryRemove(subAgentId, out var cleanupCts))
                cleanupCts.Dispose();
        }
    }

    private static string DescribeStatus(SubAgentStatus status)
        => status switch
        {
            SubAgentStatus.Completed => "completed",
            SubAgentStatus.Failed => "failed",
            SubAgentStatus.TimedOut => "timed out",
            SubAgentStatus.Killed => "was killed",
            _ => "updated"
        };

    private bool TryUpdateSubAgent(string subAgentId, Func<SubAgentInfo, SubAgentInfo> updateFactory)
        => TryUpdateSubAgent(subAgentId, updateFactory, out _);

    private bool TryUpdateSubAgent(
        string subAgentId,
        Func<SubAgentInfo, SubAgentInfo> updateFactory,
        out SubAgentInfo updatedInfo)
    {
        while (_subAgents.TryGetValue(subAgentId, out var current))
        {
            var updated = updateFactory(current);
            if (_subAgents.TryUpdate(subAgentId, updated, current))
            {
                updatedInfo = updated;
                return true;
            }
        }

        updatedInfo = default!;
        return false;
    }

    private async Task PublishLifecycleActivityAsync(
        GatewayActivityType type,
        string eventName,
        SubAgentInfo info,
        string? parentAgentId,
        string message)
    {
        try
        {
            await _activity.PublishAsync(new GatewayActivity
            {
                Type = type,
                AgentId = parentAgentId,
                SessionId = info.ParentSessionId,
                Message = message,
                Data = new Dictionary<string, object?>
                {
                    ["event"] = eventName,
                    ["subAgent"] = info
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish sub-agent lifecycle event '{EventName}' for sub-agent '{SubAgentId}'.",
                eventName,
                info.SubAgentId);
        }
    }

    private async Task CleanupChildAgentAsync(string subAgentId, SessionId childSessionId, CancellationToken ct)
    {
        if (!_childAgentIds.TryRemove(subAgentId, out var childAgentId))
            return;

        // Remove the dynamic deny-list registered for this ephemeral sub-agent
        _policyProvider?.RemoveDynamicDenyList(childAgentId);

        try
        {
            await _supervisor.StopAsync(childAgentId, childSessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed stopping child agent '{ChildAgentId}' for sub-agent '{SubAgentId}'.",
                childAgentId,
                subAgentId);
        }
        finally
        {
            _registry.Unregister(childAgentId);
        }

        if (_workspaceManager is null)
            return;

        try
        {
            if (_workspaceManager.TryCleanupWorkspace(childAgentId.Value))
            {
                _logger.LogDebug(
                    "Cleaned up temporary workspace for child agent '{ChildAgentId}'.",
                    childAgentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed cleaning temporary workspace for child agent '{ChildAgentId}'.",
                childAgentId);
        }
    }

    /// <summary>
    /// Counts the sub-agent nesting depth encoded in a session ID.
    /// A top-level session has depth 0; each <c>::subagent::</c> separator adds one level.
    /// </summary>
    private static int CountSubAgentDepth(SessionId sessionId)
    {
        const string separator = "::subagent::";
        var value = sessionId.Value;
        var depth = 0;
        var searchFrom = 0;
        while ((searchFrom = value.IndexOf(separator, searchFrom, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            depth++;
            searchFrom += separator.Length;
        }
        return depth;
    }

    /// <summary>
    /// Merges parent and base descriptor file access policies with the additional paths requested
    /// by the spawn request. The result is constrained to what the parent agent is allowed:
    /// sub-agents cannot exceed their parent's permissions (privilege escalation prevention).
    /// </summary>
    private static FileAccessPolicy? MergeFileAccess(
        FileAccessPolicy? parentAccess,
        FileAccessPolicy? baseAccess,
        IReadOnlyList<string> additionalReadPaths,
        IReadOnlyList<string> additionalWritePaths)
    {
        // Start with the base descriptor's access (parent clone or target agent)
        var baseRead = baseAccess?.AllowedReadPaths ?? [];
        var baseWrite = baseAccess?.AllowedWritePaths ?? [];
        var denied = baseAccess?.DeniedPaths ?? [];

        if (additionalReadPaths.Count == 0 && additionalWritePaths.Count == 0)
        {
            // No additions requested -- return base access as-is
            return baseAccess;
        }

        // Filter additional paths to only those the parent can access (privilege confinement).
        // If parentAccess is null, the parent has no restrictions -- allow any path.
        var parentAllowedRead = parentAccess?.AllowedReadPaths;
        var parentAllowedWrite = parentAccess?.AllowedWritePaths;

        var grantedRead = additionalReadPaths
            .Where(p => parentAllowedRead is null || parentAllowedRead.Any(allowed =>
                p.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var grantedWrite = additionalWritePaths
            .Where(p => parentAllowedWrite is null || parentAllowedWrite.Any(allowed =>
                p.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var mergedRead = baseRead.Concat(grantedRead).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mergedWrite = baseWrite.Concat(grantedWrite).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new FileAccessPolicy
        {
            AllowedReadPaths = mergedRead,
            AllowedWritePaths = mergedWrite,
            DeniedPaths = denied
        };
    }
}
