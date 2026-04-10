using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
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
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<DefaultSubAgentManager> _logger;
    private readonly ConcurrentDictionary<string, SubAgentInfo> _subAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _parentChildren = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _parentAgentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _childAgentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _timeouts = new(StringComparer.OrdinalIgnoreCase);

    public DefaultSubAgentManager(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        IActivityBroadcaster activity,
        IOptions<GatewayOptions> options,
        ILogger<DefaultSubAgentManager> logger)
    {
        _supervisor = supervisor;
        _registry = registry;
        _activity = activity;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SubAgentInfo> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parentDescriptor = _registry.Get(request.ParentAgentId)
            ?? throw new KeyNotFoundException($"Parent agent '{request.ParentAgentId}' is not registered.");

        var maxConcurrent = _options.Value.SubAgents.MaxConcurrentPerSession;
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
        var childSessionId = $"{request.ParentSessionId}::subagent::{uniqueId}";
        var subAgentId = uniqueId;
        var childAgentId = request.ParentAgentId;

        var handle = await _supervisor.GetOrCreateAsync(childAgentId, childSessionId, ct);

        var configuredDefaultModel = string.IsNullOrWhiteSpace(_options.Value.SubAgents.DefaultModel)
            ? null
            : _options.Value.SubAgents.DefaultModel;

        var info = new SubAgentInfo
        {
            SubAgentId = subAgentId,
            ParentSessionId = request.ParentSessionId,
            ChildSessionId = childSessionId,
            Name = request.Name,
            Task = request.Task,
            Model = request.ModelOverride ?? configuredDefaultModel ?? parentDescriptor.ModelId,
            Status = SubAgentStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            TurnsUsed = 0
        };

        if (!_subAgents.TryAdd(subAgentId, info))
            throw new InvalidOperationException($"Sub-agent '{subAgentId}' already exists.");

        _parentAgentIds[subAgentId] = request.ParentAgentId;
        _childAgentIds[subAgentId] = childAgentId;
        _parentChildren.GetOrAdd(request.ParentSessionId, _ => []).Add(subAgentId);

        var timeoutSeconds = request.TimeoutSeconds > 0
            ? request.TimeoutSeconds
            : _options.Value.SubAgents.DefaultTimeoutSeconds;
        if (timeoutSeconds <= 0)
            timeoutSeconds = 1;

        var timeoutCts = new CancellationTokenSource();
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        _timeouts[subAgentId] = timeoutCts;

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
            request.ParentAgentId,
            $"Sub-agent '{subAgentId}' spawned.");

        return info;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubAgentInfo>> ListAsync(string parentSessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);

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
    public async Task<bool> KillAsync(string subAgentId, string requestingSessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingSessionId);

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

        if (_childAgentIds.TryGetValue(subAgentId, out var childAgentId))
            await _supervisor.StopAsync(childAgentId, info.ChildSessionId, ct);

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

        var followUp = $"Sub-agent {subAgentId} {DescribeStatus(updated.Status)}. Summary:\n{normalizedSummary}";

        try
        {
            var parentHandle = await _supervisor.GetOrCreateAsync(parentAgentId, updated.ParentSessionId, ct);
            await parentHandle.FollowUpAsync(followUp, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed delivering completion follow-up for sub-agent '{SubAgentId}' to parent session '{ParentSessionId}'.",
                subAgentId,
                updated.ParentSessionId);
        }
    }

    private async Task RunSubAgentAsync(string subAgentId, IAgentHandle handle, string task, int timeoutSeconds)
    {
        if (!_timeouts.TryGetValue(subAgentId, out var timeoutCts))
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
}
