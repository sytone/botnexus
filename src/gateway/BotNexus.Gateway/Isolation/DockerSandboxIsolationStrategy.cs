using System.Collections.Concurrent;
using System.Diagnostics;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Docker sandbox isolation strategy — runs agents inside Docker sandbox containers
/// with lifecycle management (create on first dispatch, keep warm, stop on idle timeout).
/// </summary>
/// <remarks>
/// <para>
/// This strategy uses Docker's sandbox API (<c>docker sandbox create</c>) to provide
/// lightweight isolation. Each agent gets its own sandbox that persists across dispatches
/// (warm reuse) and stops after a configurable idle period to conserve resources.
/// </para>
/// <para>
/// The lifecycle state machine transitions are:
/// <list type="bullet">
///   <item><b>None → Creating</b> — First dispatch triggers sandbox creation.</item>
///   <item><b>Creating → Running</b> — Sandbox is ready for agent dispatch.</item>
///   <item><b>Running → Running</b> — Subsequent dispatches reuse the warm sandbox.</item>
///   <item><b>Running → Stopping</b> — Idle timeout triggers graceful stop.</item>
///   <item><b>Stopping → Stopped</b> — Sandbox resources released.</item>
///   <item><b>Stopped → Creating</b> — Next dispatch re-creates the sandbox.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class DockerSandboxIsolationStrategy : IIsolationStrategy, IAsyncDisposable
{
    private readonly IDockerSandboxRunner _runner;
    private readonly IOptions<DockerSandboxOptions> _options;
    private readonly ILogger<DockerSandboxIsolationStrategy> _logger;
    private readonly ConcurrentDictionary<AgentId, SandboxState> _sandboxes = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public DockerSandboxIsolationStrategy(
        IDockerSandboxRunner runner,
        IOptions<DockerSandboxOptions> options,
        ILogger<DockerSandboxIsolationStrategy> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "docker-sandbox";

    /// <inheritdoc />
    public async Task<IAgentHandle> CreateAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!await _runner.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Docker sandbox is not available on this host. " +
                $"Ensure Docker is installed and the 'docker sandbox' command is accessible. " +
                $"Agent '{descriptor.AgentId}' cannot use the '{Name}' isolation strategy.");
        }

        var sandboxName = GetSandboxName(descriptor.AgentId);
        var state = await EnsureSandboxRunningAsync(descriptor.AgentId, sandboxName, cancellationToken)
            .ConfigureAwait(false);

        state.RecordActivity();

        _logger.LogInformation(
            "Dispatching to Docker sandbox '{SandboxName}' for agent '{AgentId}' session '{SessionId}'",
            sandboxName, descriptor.AgentId, context.SessionId);

        return new DockerSandboxAgentHandle(
            descriptor.AgentId,
            context.SessionId,
            sandboxName,
            state,
            _logger);
    }

    /// <summary>
    /// Gets the current lifecycle status of a sandbox for the given agent.
    /// Returns <see cref="SandboxLifecycleStatus.None"/> if no sandbox has been created.
    /// </summary>
    public SandboxLifecycleStatus GetStatus(AgentId agentId)
        => _sandboxes.TryGetValue(agentId, out var state)
            ? state.Status
            : SandboxLifecycleStatus.None;

    /// <summary>
    /// Checks all running sandboxes for idle timeout and stops any that have exceeded it.
    /// Called by the hosted service on a timer.
    /// </summary>
    public async Task CheckIdleTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        var timeout = _options.Value.IdleTimeout;
        var now = DateTimeOffset.UtcNow;

        foreach (var (agentId, state) in _sandboxes)
        {
            if (state.Status != SandboxLifecycleStatus.Running)
                continue;

            if (now - state.LastActivity > timeout)
            {
                _logger.LogInformation(
                    "Sandbox for agent '{AgentId}' idle for {Elapsed} (threshold {Timeout}), stopping",
                    agentId, now - state.LastActivity, timeout);

                await StopSandboxAsync(agentId, state, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<SandboxState> EnsureSandboxRunningAsync(
        AgentId agentId,
        string sandboxName,
        CancellationToken cancellationToken)
    {
        // Fast path: sandbox already running
        if (_sandboxes.TryGetValue(agentId, out var existing) &&
            existing.Status == SandboxLifecycleStatus.Running)
        {
            // Verify it's actually healthy
            if (await _runner.IsHealthyAsync(sandboxName, cancellationToken).ConfigureAwait(false))
                return existing;

            _logger.LogWarning(
                "Sandbox '{SandboxName}' for agent '{AgentId}' reported unhealthy, recreating",
                sandboxName, agentId);
        }

        await _createLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_sandboxes.TryGetValue(agentId, out existing) &&
                existing.Status == SandboxLifecycleStatus.Running &&
                await _runner.IsHealthyAsync(sandboxName, cancellationToken).ConfigureAwait(false))
            {
                return existing;
            }

            var state = new SandboxState(sandboxName);
            state.TransitionTo(SandboxLifecycleStatus.Creating);
            _sandboxes[agentId] = state;

            _logger.LogInformation(
                "Creating Docker sandbox '{SandboxName}' for agent '{AgentId}'",
                sandboxName, agentId);

            await _runner.CreateAsync(sandboxName, cancellationToken).ConfigureAwait(false);
            state.TransitionTo(SandboxLifecycleStatus.Running);

            _logger.LogInformation(
                "Docker sandbox '{SandboxName}' for agent '{AgentId}' is now running",
                sandboxName, agentId);

            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create Docker sandbox '{SandboxName}' for agent '{AgentId}'",
                sandboxName, agentId);
            throw;
        }
        finally
        {
            _createLock.Release();
        }
    }

    private async Task StopSandboxAsync(
        AgentId agentId,
        SandboxState state,
        CancellationToken cancellationToken)
    {
        try
        {
            state.TransitionTo(SandboxLifecycleStatus.Stopping);
            await _runner.StopAsync(state.Name, cancellationToken).ConfigureAwait(false);
            state.TransitionTo(SandboxLifecycleStatus.Stopped);

            _logger.LogInformation("Stopped Docker sandbox '{SandboxName}' for agent '{AgentId}'",
                state.Name, agentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error stopping Docker sandbox '{SandboxName}' for agent '{AgentId}'",
                state.Name, agentId);
            // Mark as stopped anyway to allow recreation
            state.TransitionTo(SandboxLifecycleStatus.Stopped);
        }
    }

    private static string GetSandboxName(AgentId agentId)
        => $"agent-{agentId.Value}";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var (agentId, state) in _sandboxes)
        {
            if (state.Status == SandboxLifecycleStatus.Running)
            {
                try
                {
                    await _runner.StopAsync(state.Name, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping sandbox '{SandboxName}' during dispose", state.Name);
                }
            }
        }

        _sandboxes.Clear();
        _createLock.Dispose();
    }
}

/// <summary>
/// Lifecycle status of a Docker sandbox instance.
/// </summary>
public enum SandboxLifecycleStatus
{
    /// <summary>No sandbox exists for this agent.</summary>
    None,
    /// <summary>Sandbox is being created.</summary>
    Creating,
    /// <summary>Sandbox is running and ready for dispatches.</summary>
    Running,
    /// <summary>Sandbox is being stopped (idle timeout or explicit stop).</summary>
    Stopping,
    /// <summary>Sandbox has been stopped and resources released.</summary>
    Stopped
}

/// <summary>
/// Mutable state tracking for a single Docker sandbox instance.
/// Thread-safe via atomic operations for activity tracking and explicit transitions.
/// </summary>
internal sealed class SandboxState
{
    private long _lastActivityTicks;
    private volatile SandboxLifecycleStatus _status;

    public SandboxState(string name)
    {
        Name = name;
        _lastActivityTicks = DateTimeOffset.UtcNow.Ticks;
        _status = SandboxLifecycleStatus.None;
    }

    public string Name { get; }

    public SandboxLifecycleStatus Status => _status;

    public DateTimeOffset LastActivity
        => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public void RecordActivity()
        => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.Ticks);

    public void TransitionTo(SandboxLifecycleStatus newStatus)
        => _status = newStatus;
}
