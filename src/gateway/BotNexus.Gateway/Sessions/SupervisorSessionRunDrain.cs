using System.Diagnostics;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// The production <see cref="ISessionRunDrain"/> (issue #2903): fences the run bound to one exact
/// session by aborting its live agent handle and waiting for the handle to report it is no longer
/// running, so an archive cannot seal a session out from under a turn that is still writing to it.
/// </summary>
/// <remarks>
/// <para>
/// The supervisor is resolved lazily from the service provider rather than injected. The
/// dependency graph is genuinely cyclic - <c>ISessionStore</c> needs the drain, the drain needs
/// <c>IAgentSupervisor</c>, and the supervisor needs <c>ISessionStore</c> - and resolving at drain
/// time rather than at construction time is what breaks it. Nothing is resolved unless an archive
/// actually runs.
/// </para>
/// <para>
/// Scoping (AC3): only the handle registered for the <em>exact</em> session id is touched. The
/// supervisor keys instances by (agent, session), so an agent with three live sessions loses only
/// the one being archived.
/// </para>
/// </remarks>
public sealed class SupervisorSessionRunDrain : ISessionRunDrain
{
    // The handle reports IsRunning from the agent's per-turn status. There is no completion signal
    // to await, so settle detection is a bounded poll. 25ms is short enough that the common case
    // (abort lands almost immediately) costs one or two iterations, and long enough that a drain
    // waiting the full budget does not spin.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly IServiceProvider _services;
    private readonly ILogger<SupervisorSessionRunDrain>? _logger;

    /// <summary>
    /// Creates the drain over the root service provider. The provider is captured, not resolved
    /// from, until <see cref="DrainAsync"/> is called - see the class remarks for why.
    /// </summary>
    /// <param name="services">Root provider used to resolve <see cref="IAgentSupervisor"/> lazily.</param>
    /// <param name="logger">Optional logger for drain diagnostics.</param>
    public SupervisorSessionRunDrain(IServiceProvider services, ILogger<SupervisorSessionRunDrain>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SessionDrainOutcome> DrainAsync(
        SessionId sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var supervisor = _services.GetService<IAgentSupervisor>();
        if (supervisor is null)
            return SessionDrainOutcome.NoActiveRun;

        var handles = ResolveHandlesForSession(supervisor, sessionId);
        if (handles.Count == 0)
            return SessionDrainOutcome.NoActiveRun;

        var running = handles.Where(handle => handle.IsRunning).ToList();
        if (running.Count == 0)
            return SessionDrainOutcome.NoActiveRun;

        foreach (var handle in running)
        {
            try
            {
                await handle.AbortAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An abort that throws is not a reason to seal over the run: fall through to the
                // settle wait and let the timeout decide. Reporting Drained here would be a lie.
                _logger?.LogWarning(
                    ex,
                    "Abort failed while draining session '{SessionId}' for archive; waiting for the run to settle anyway.",
                    sessionId.Value);
            }
        }

        var deadline = Stopwatch.StartNew();
        while (running.Any(handle => handle.IsRunning))
        {
            if (deadline.Elapsed >= timeout)
            {
                _logger?.LogWarning(
                    "Session '{SessionId}' still had a live run after {Timeout}; archive will be refused.",
                    sessionId.Value,
                    timeout);
                return SessionDrainOutcome.TimedOut;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return SessionDrainOutcome.Drained;
    }

    /// <summary>
    /// Selects the live handles bound to exactly this session id. Iterating instances and matching
    /// on <c>SessionId</c> (rather than on agent id) is what keeps the fence session-scoped.
    /// </summary>
    private static List<IAgentHandle> ResolveHandlesForSession(IAgentSupervisor supervisor, SessionId sessionId)
    {
        var handles = new List<IAgentHandle>();
        foreach (var instance in supervisor.GetAllInstances())
        {
            if (!instance.SessionId.Equals(sessionId))
                continue;

            var handle = supervisor.GetHandle(instance.AgentId, instance.SessionId);
            if (handle is not null)
                handles.Add(handle);
        }

        return handles;
    }
}
