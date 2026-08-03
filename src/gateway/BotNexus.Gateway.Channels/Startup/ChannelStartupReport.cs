namespace BotNexus.Gateway.Channels.Startup;

/// <summary>
/// Captures the outcome of the channel-adapter startup pass so an operator-facing endpoint can
/// distinguish adapters <em>configured</em> from adapters actually <em>started</em> (#2447).
/// </summary>
/// <remarks>
/// <para>
/// The gateway used to log "Gateway started with 5 channel adapter(s)" after an adapter had
/// failed to start. The count was the configured count, so a permanently dead channel was
/// indistinguishable from a healthy one and the only symptom was silence on that channel.
/// </para>
/// <para>
/// This mirrors <c>ExtensionBootReport</c> (#2220), which solved the same class of problem for
/// extension loading: capture the real per-item result at boot, register it as a singleton, and
/// let a health endpoint report the named failures instead of a single reassuring number.
/// </para>
/// </remarks>
public sealed class ChannelStartupReport
{
    private volatile IReadOnlyList<ChannelStartOutcome> _outcomes = [];

    /// <summary>
    /// Gets the recorded start outcomes, one per configured adapter. Empty until the gateway
    /// host has completed its startup pass.
    /// </summary>
    public IReadOnlyList<ChannelStartOutcome> Outcomes => _outcomes;

    /// <summary>
    /// Gets whether the startup pass has completed and every adapter reached the started state.
    /// </summary>
    public bool AllStarted => _outcomes.Count > 0 && _outcomes.All(outcome => outcome.Started);

    private readonly object _faultGate = new();
    private volatile IReadOnlyList<ChannelServiceFault> _serviceFaults = [];

    /// <summary>
    /// Gets faults contained by <see cref="ChannelFaultBarrierHostedService"/> - channel-owned
    /// background services that failed rather than adapters that failed to start (#2731).
    /// </summary>
    /// <remarks>
    /// Before #2731 such a fault escalated to <c>StopHost</c> and the only trace was a fatal log
    /// line in a process that was already exiting. Recording it here makes the degraded state
    /// queryable for the life of the process.
    /// </remarks>
    public IReadOnlyList<ChannelServiceFault> ServiceFaults => _serviceFaults;

    /// <summary>
    /// Records a contained channel background-service fault.
    /// </summary>
    /// <param name="fault">The fault to publish.</param>
    public void RecordServiceFault(ChannelServiceFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        lock (_faultGate)
        {
            _serviceFaults = [.. _serviceFaults, fault];
        }
    }

    /// <summary>
    /// Records the results of the startup pass. Called once from <c>GatewayHost.ExecuteAsync</c>
    /// immediately after <see cref="ChannelStartupCoordinator.StartAllAsync"/> returns.
    /// </summary>
    /// <param name="outcomes">One outcome per configured adapter.</param>
    public void Record(IReadOnlyList<ChannelStartOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        _outcomes = outcomes;
    }
}

/// <summary>
/// A channel-owned background service fault contained by the #2731 fault barrier.
/// </summary>
/// <param name="ChannelType">The channel whose service faulted.</param>
/// <param name="ServiceName">The CLR type name of the faulted hosted service.</param>
/// <param name="Error">The exception that was contained.</param>
public sealed record ChannelServiceFault(string ChannelType, string ServiceName, Exception Error);