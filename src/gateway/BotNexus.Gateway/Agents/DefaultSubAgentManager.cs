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
using BotNexus.Domain.World;
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
    // Single source of truth per sub-agent. Folds the previously-separate parent/child agent
    // id, timeout CTS, and the completion/cleanup once-only gates into one record so an add or
    // teardown is a single atomic dictionary operation. The old design spread these across five
    // parallel dictionaries keyed by the same subAgentId that had to be kept mutually consistent
    // by hand — the synthetic child-AgentId fallback in OnCompletedAsync existed precisely
    // because _childAgentIds could drift out of sync with _subAgents (#1385).
    private readonly ConcurrentDictionary<string, SubAgentRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<SessionId, ConcurrentBag<string>> _parentChildren = [];

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
        // Defence in depth: `required` is a compile-time guarantee only —
        // callers using `null!` (or producing a null Mode via reflection / JSON
        // deserialization quirks) would otherwise reach the default arm and
        // hit a NullReferenceException dereferencing request.Mode.GetType().
        ArgumentNullException.ThrowIfNull(request.Mode);

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

        var maxConcurrent = _options.CurrentValue.SubAgents.MaxConcurrentPerSession;
        if (maxConcurrent > 0)
        {
            var activeCount = _records.Values.Count(record =>
                record.Info.ParentSessionId == request.ParentSessionId &&
                record.Info.Status == SubAgentStatus.Running);

            if (activeCount >= maxConcurrent)
                throw new InvalidOperationException(
                    $"Parent session '{request.ParentSessionId}' already has {activeCount} running sub-agents; maximum is {maxConcurrent}.");
        }

        var uniqueId = Guid.NewGuid().ToString("N");
        var subAgentId = uniqueId;
        var childSessionId = SessionId.ForSubAgent(request.ParentSessionId, uniqueId);

        // Phase 5 / F-6 (#562): pattern-match on the required Mode discriminated
        // union. Mode is `required` on SubAgentSpawnRequest as of step 5, so the
        // null case is structurally unreachable — the default arm exists only to
        // catch a future third subclass of SubAgentSpawnMode being added without
        // updating this switch.
        SubAgentArchetype archetype;
        AgentDescriptor baseDescriptor;
        AgentId childAgentId;
        string? name;
        string? modelOverride;
        string? apiProviderOverride;
        IReadOnlyList<string>? toolIds;
        string? systemPromptOverride;

        switch (request.Mode)
        {
            case Embody embody:
                archetype = embody.Role;
                baseDescriptor = parentDescriptor;
                childAgentId = AgentId.From($"{request.ParentAgentId}--subagent--{archetype.Value}--{uniqueId}");
                name = embody.Customizations.Name;
                modelOverride = embody.Customizations.ModelOverride;
                apiProviderOverride = embody.Customizations.ApiProviderOverride;
                toolIds = embody.Customizations.ToolIds;
                systemPromptOverride = embody.Customizations.SystemPromptOverride;
                break;

            case Mirror mirror:
                // Mirror is strict pass-through of the target's descriptor. The
                // archetype slot in the child agent id is filled with the target
                // id (locked design #562) so the child surfaces the mirrored
                // identity rather than a generic role label.
                archetype = SubAgentArchetype.General;
                baseDescriptor = _registry.Get(mirror.TargetAgentId)
                    ?? throw new KeyNotFoundException($"Target agent '{mirror.TargetAgentId}' is not registered.");
                childAgentId = AgentId.From($"{request.ParentAgentId}--subagent--{mirror.TargetAgentId.Value}--{uniqueId}");
                name = null;
                modelOverride = null;
                apiProviderOverride = null;
                toolIds = null;
                systemPromptOverride = null;
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown SubAgentSpawnMode subclass '{request.Mode.GetType().FullName}'. "
                    + "Embody and Mirror are the only legal modes — see SubAgentSpawnMode.");
        }

        // Validate tool grants against parent's deny-list (privilege escalation
        // prevention). Runs AFTER the switch so it reads the typed `toolIds`
        // local resolved from Mode, not a deleted top-level request field.
        if (_policyProvider is not null && toolIds is { Count: > 0 })
        {
            var parentDenyList = _policyProvider.GetEffectiveDenyList(request.ParentAgentId.Value);
            if (parentDenyList.Count > 0)
            {
                var denied = toolIds
                    .Where(t => parentDenyList.Contains(t, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (denied.Count > 0)
                    throw new InvalidOperationException(
                        $"Sub-agent cannot be granted tools denied to the parent: {string.Join(", ", denied)}");
            }
        }

        // Build file access policy for workspace isolation.
        // By default, sub-agents can only access their own temp workspace.
        // ShareWorkspace grants read+write to the parent's workspace.
        // GrantedPaths adds read-only access to specific directories.
        FileAccessPolicy? childFileAccess = null;
        if (request.ShareWorkspace || request.GrantedPaths is { Count: > 0 })
        {
            var allowedRead = new List<string>();
            var allowedWrite = new List<string>();

            if (request.ShareWorkspace && _workspaceManager is not null)
            {
                var parentWorkspace = _workspaceManager.GetWorkspacePath(request.ParentAgentId.Value);
                allowedRead.Add(parentWorkspace);
                allowedWrite.Add(parentWorkspace);
            }

            if (request.GrantedPaths is { Count: > 0 })
            {
                foreach (var grantedPath in request.GrantedPaths)
                {
                    if (!string.IsNullOrWhiteSpace(grantedPath))
                        allowedRead.Add(Path.GetFullPath(grantedPath));
                }
            }

            childFileAccess = new FileAccessPolicy
            {
                AllowedReadPaths = allowedRead,
                AllowedWritePaths = allowedWrite
            };
        }

        if (!_registry.Contains(childAgentId))
        {
            _registry.Register(baseDescriptor with
            {
                AgentId = childAgentId,
                DisplayName = $"{baseDescriptor.DisplayName} ({archetype.Value})",
                Kind = AgentKind.SubAgent,
                FileAccess = childFileAccess ?? baseDescriptor.FileAccess
            });
        }

        var handle = await _supervisor.GetOrCreateAsync(childAgentId, childSessionId, ct);

        // Eager conversation pinning (Phase 4 / F-6): pin the child session to the
        // parent conversation BEFORE returning, so concurrent lookups (e.g.
        // ISessionStore.ListByConversationAsync, /api/conversations/{id}/history,
        // canvas resolvers) never see the child as an orphan. Prior to this, the pin
        // ran inside the fire-and-forget Task.Run below, opening an orphan window
        // between SpawnAsync returning and the background task being scheduled.
        if (_sessionStore is not null)
        {
            var childSession = await _sessionStore.GetAsync(childSessionId, ct).ConfigureAwait(false);
            if (childSession is not null)
            {
                childSession.ConversationId = request.InheritedConversationId;
                await _sessionStore.SaveAsync(childSession, ct).ConfigureAwait(false);
            }
        }

        var configuredDefaultModel = string.IsNullOrWhiteSpace(_options.CurrentValue.SubAgents.DefaultModel)
            ? null
            : _options.CurrentValue.SubAgents.DefaultModel;

        var info = new SubAgentInfo
        {
            SubAgentId = subAgentId,
            ParentSessionId = request.ParentSessionId,
            ChildSessionId = childSessionId,
            Name = name,
            ParentAgentId = request.ParentAgentId.Value,
            ChildAgentId = childAgentId.Value,
            Task = request.Task,
            Model = modelOverride ?? configuredDefaultModel ?? baseDescriptor.ModelId,
            Archetype = archetype,
            Status = SubAgentStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            TurnsUsed = 0
        };

        var record = new SubAgentRecord(info, request.ParentAgentId, childAgentId);
        if (!_records.TryAdd(subAgentId, record))
            throw new InvalidOperationException($"Sub-agent '{subAgentId}' already exists.");

        // Persist the sub-agent session row to sessions.db (best-effort; non-SQLite stores no-op).
        if (_sessionStore is not null)
        {
            try { await _sessionStore.SaveSubAgentSessionAsync(info, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist sub-agent session row for '{SubAgentId}'.", subAgentId);
            }
        }

        _parentChildren.GetOrAdd(request.ParentSessionId, _ => []).Add(subAgentId);

        // Register inherited deny-list so the child agent can't invoke parent-denied tools
        if (_policyProvider is not null)
        {
            var effectiveDenyList = _policyProvider.GetEffectiveDenyList(request.ParentAgentId.Value);
            if (effectiveDenyList.Count > 0)
                _policyProvider.SetDynamicDenyList(childAgentId, effectiveDenyList);
        }

        var subAgentOptions = _options.CurrentValue.SubAgents;

        // Clamp the agent-supplied timeout to the configured ceiling. Depth and concurrency are
        // already bounded above; without this the timeout (the only budget wired to a real
        // cancellation token) could be set arbitrarily high, letting a background sub-agent run
        // effectively forever. Mirrors the runaway-cost guard the agent_converse tool applies.
        var timeoutSeconds = subAgentOptions.ResolveTimeoutSeconds(request.TimeoutSeconds);

        // Clamp the requested turn budget too. It is not yet wired to a live per-turn counter, but
        // bounding it here keeps the request shape honest and prevents a latent runaway when it is.
        var maxTurns = subAgentOptions.ResolveMaxTurns(request.MaxTurns);
        if (request.TimeoutSeconds > timeoutSeconds || request.MaxTurns > maxTurns)
        {
            _logger.LogWarning(
                "Sub-agent '{SubAgentId}' spawn budget clamped: timeoutSeconds {RequestedTimeout}->{ClampedTimeout}, maxTurns {RequestedMaxTurns}->{ClampedMaxTurns}.",
                subAgentId,
                request.TimeoutSeconds,
                timeoutSeconds,
                request.MaxTurns,
                maxTurns);
        }

        var timeoutCts = new CancellationTokenSource();
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        record.TimeoutCts = timeoutCts;

        _ = Task.Run(() => RunSubAgentAsync(subAgentId, handle, request.Task, timeoutSeconds), CancellationToken.None);

        _logger.LogInformation(
            "Spawned sub-agent '{SubAgentId}' for parent session '{ParentSessionId}' in child session '{ChildSessionId}'.",
            subAgentId,
            request.ParentSessionId,
            childSessionId);

        await PublishLifecycleActivityAsync(
            GatewayActivityType.SubAgentSpawned,
            "subagent_spawned",
            info,
            request.ParentAgentId.Value,
            $"Sub-agent '{subAgentId}' spawned.");

        return info;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubAgentInfo>> ListAsync(SessionId parentSessionId, CancellationToken ct = default)
    {
        if (!_parentChildren.TryGetValue(parentSessionId, out var subAgentIds))
            return Task.FromResult<IReadOnlyList<SubAgentInfo>>([]);

        var results = subAgentIds
            .Select(id => _records.TryGetValue(id, out var record) ? record.Info : null)
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
        return Task.FromResult(_records.TryGetValue(subAgentId, out var record) ? record.Info : null);
    }

    /// <inheritdoc />
    public async Task<bool> KillAsync(string subAgentId, SessionId requestingSessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);

        if (!_records.TryGetValue(subAgentId, out var record))
            return false;

        var info = record.Info;

        if (info.ParentSessionId != requestingSessionId)
            return false;

        if (info.Status is SubAgentStatus.Completed or SubAgentStatus.Failed or SubAgentStatus.Killed or SubAgentStatus.TimedOut)
            return false;

        record.CancelTimeout();

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

        // Update the sub-agent session row with Killed status (best-effort).
        if (_sessionStore is not null && updatedInfo.CompletedAt.HasValue)
        {
            try
            {
                await _sessionStore.UpdateSubAgentSessionAsync(
                    subAgentId,
                    updatedInfo.CompletedAt.Value,
                    SubAgentStatus.Killed.ToString(),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update sub-agent session row for '{SubAgentId}'.", subAgentId);
            }
        }

        await PublishLifecycleActivityAsync(
            GatewayActivityType.SubAgentKilled,
            "subagent_killed",
            updatedInfo,
            record.ParentAgentId.Value,
            $"Sub-agent '{subAgentId}' was killed.");

        return true;
    }

    /// <inheritdoc />
    public async Task OnCompletedAsync(string subAgentId, string resultSummary, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);

        if (!_records.TryGetValue(subAgentId, out var record))
            return;

        var existing = record.Info;

        if (!record.TryBeginCompletion())
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

        // Update the sub-agent session row with the final status (best-effort).
        if (_sessionStore is not null && updated.CompletedAt.HasValue)
        {
            try
            {
                await _sessionStore.UpdateSubAgentSessionAsync(
                    subAgentId,
                    updated.CompletedAt.Value,
                    updated.Status.ToString(),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update sub-agent session row for '{SubAgentId}'.", subAgentId);
            }
        }

        var parentAgentId = record.ParentAgentId;
        // Producer-side species: this wake-up is FROM the child sub-agent TO the parent.
        // Sender must carry the child's AgentId, not the parent's, so participant
        // tracking and downstream conversation attribution see the correct originator.
        // (Fix #526 / sub-agent misclassification.) The child AgentId now lives on the
        // record alongside the rest of the sub-agent state, so it can never drift out of
        // sync with the live entry the way the old separate _childAgentIds map could —
        // the synthetic-fallback workaround that drift required is no longer needed (#1385).
        var childAgentId = record.ChildAgentId;
        if (updated.Status == SubAgentStatus.Completed)
        {
            await PublishLifecycleActivityAsync(
                GatewayActivityType.SubAgentCompleted,
                "subagent_completed",
                updated,
                parentAgentId.Value,
                $"Sub-agent '{subAgentId}' completed.");
        }
        else if (updated.Status is SubAgentStatus.Failed or SubAgentStatus.TimedOut)
        {
            await PublishLifecycleActivityAsync(
                GatewayActivityType.SubAgentFailed,
                "subagent_failed",
                updated,
                parentAgentId.Value,
                $"Sub-agent '{subAgentId}' failed.");
        }

        if (string.IsNullOrWhiteSpace(parentAgentId.Value))
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
                Sender = CitizenId.Of(childAgentId),
                ChannelAddress = ChannelAddress.From(updated.ParentSessionId.Value),
                RoutingHints = new InboundMessageRoutingHints(
                    RequestedAgentId: parentAgentId,
                    RequestedSessionId: updated.ParentSessionId,
                    RequestedConversationId: null),
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
    private async Task RunSubAgentAsync(string subAgentId, IAgentHandle handle, string task, int timeoutSeconds)
    {
        if (!_records.TryGetValue(subAgentId, out var record) || record.TimeoutCts is not { } timeoutCts)
            return;

        try
        {
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
            record.DisposeTimeout();
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
        if (_records.TryGetValue(subAgentId, out var record))
            return record.TryUpdateInfo(updateFactory, out updatedInfo);

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
                SessionId = info.ParentSessionId.Value,
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
        // The cleanup body must run at most once per sub-agent: it stops the child agent,
        // removes the dynamic deny-list, unregisters the descriptor and reclaims the
        // workspace. KillAsync and the OnCompletedAsync `finally` can both reach here, so
        // the once-only gate lives on the record (previously this role was implicitly served
        // by _childAgentIds.TryRemove succeeding exactly once).
        if (!_records.TryGetValue(subAgentId, out var record) || !record.TryBeginCleanup())
            return;

        var childAgentId = record.ChildAgentId;

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
    /// Single source of truth for one sub-agent's mutable runtime state. Consolidates what were
    /// previously five parallel dictionaries keyed by the same sub-agent id (#1385): the live
    /// <see cref="SubAgentInfo"/>, the parent/child <see cref="AgentId"/> values, the timeout
    /// <see cref="CancellationTokenSource"/>, and the completion/cleanup once-only gates. Folding
    /// them into one record means an add or teardown is a single atomic dictionary operation and
    /// the maps can no longer drift apart — the drift between <c>_childAgentIds</c> and the live
    /// entry is exactly what forced the old synthetic-child-id fallback in <c>OnCompletedAsync</c>.
    /// </summary>
    private sealed class SubAgentRecord(SubAgentInfo info, AgentId parentAgentId, AgentId childAgentId)
    {
        private SubAgentInfo _info = info;
        private int _completionProcessed;
        private int _cleanupStarted;

        /// <summary>Parent agent id captured at spawn. Immutable for the record's lifetime.</summary>
        public AgentId ParentAgentId { get; } = parentAgentId;

        /// <summary>Child agent id captured at spawn. Immutable for the record's lifetime.</summary>
        public AgentId ChildAgentId { get; } = childAgentId;

        /// <summary>
        /// The timeout cancellation source. Set once after the spawn budget is resolved and the
        /// record is already registered, so the background run loop can read it. Cleared (set to
        /// null) atomically on cancel/dispose so a late kill cannot double-dispose.
        /// </summary>
        public CancellationTokenSource? TimeoutCts
        {
            get => Volatile.Read(ref _timeoutCts);
            set => Volatile.Write(ref _timeoutCts, value);
        }

        /// <summary>The current immutable runtime snapshot. Swapped atomically via <see cref="TryUpdateInfo"/>.</summary>
        public SubAgentInfo Info => Volatile.Read(ref _info);

        /// <summary>
        /// Atomically applies <paramref name="updateFactory"/> to the current snapshot using a
        /// compare-and-swap loop — the same lock-free update semantics the old
        /// <c>ConcurrentDictionary.TryUpdate</c> loop provided.
        /// </summary>
        public bool TryUpdateInfo(Func<SubAgentInfo, SubAgentInfo> updateFactory, out SubAgentInfo updatedInfo)
        {
            while (true)
            {
                var current = Volatile.Read(ref _info);
                var updated = updateFactory(current);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _info, updated, current), current))
                {
                    updatedInfo = updated;
                    return true;
                }
            }
        }

        /// <summary>Returns true exactly once — the first caller wins the completion gate.</summary>
        public bool TryBeginCompletion() => Interlocked.CompareExchange(ref _completionProcessed, 1, 0) == 0;

        /// <summary>Returns true exactly once — the first caller wins the child-cleanup gate.</summary>
        public bool TryBeginCleanup() => Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) == 0;

        /// <summary>Cancels and disposes the timeout source (used by an explicit kill). Idempotent.</summary>
        public void CancelTimeout()
        {
            var cts = Interlocked.Exchange(ref _timeoutCts, null);
            if (cts is null)
                return;
            cts.Cancel();
            cts.Dispose();
        }

        /// <summary>Disposes the timeout source without cancelling (used by the run loop's finally). Idempotent.</summary>
        public void DisposeTimeout()
        {
            Interlocked.Exchange(ref _timeoutCts, null)?.Dispose();
        }

        // Backing field for TimeoutCts so set / read / Cancel / Dispose all touch one field and
        // Cancel/Dispose can clear it atomically, avoiding a double-dispose race between an
        // in-flight kill and the run loop's finally block.
        private CancellationTokenSource? _timeoutCts;
    }
}
