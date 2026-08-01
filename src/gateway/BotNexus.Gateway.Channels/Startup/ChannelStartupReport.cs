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
