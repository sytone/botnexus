using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Channels.Startup;

/// <summary>
/// Outcome of starting one channel adapter.
/// </summary>
/// <param name="ChannelType">The adapter's channel type identifier.</param>
/// <param name="DisplayName">The adapter's human-readable name.</param>
/// <param name="Started">Whether the adapter reached the started state.</param>
/// <param name="Attempts">Number of start attempts made.</param>
/// <param name="FailureKind">
/// Classification of the final failure, or <see langword="null"/> when the adapter started.
/// </param>
/// <param name="Error">The final exception, or <see langword="null"/> when the adapter started.</param>
public sealed record ChannelStartOutcome(
    string ChannelType,
    string DisplayName,
    bool Started,
    int Attempts,
    ChannelFailureKind? FailureKind,
    Exception? Error);

/// <summary>
/// Starts channel adapters with bounded, classification-aware retry, and reports which
/// adapters actually reached a started state (#2447).
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the gateway host started each adapter exactly once: a single upstream
/// 502 during start permanently disabled that channel for the life of the process, and the host
/// still logged "Gateway started with N channel adapter(s)" as though nothing had happened. The
/// only symptom was silence on the channel.
/// </para>
/// <para>
/// Retrying is only safe because adapters make a repeated <c>StartAsync</c> resumable - an
/// adapter that partially started must not start a second listener for an already-live resource
/// on the retry. See <c>TelegramChannelAdapter</c>'s per-bot start latch.
/// </para>
/// </remarks>
public sealed class ChannelStartupCoordinator
{
    private readonly ChannelStartRetryPolicy _policy;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelStartupCoordinator"/> class.
    /// </summary>
    /// <param name="logger">Logger for start, retry, and give-up diagnostics.</param>
    /// <param name="policy">Retry policy; defaults to <see cref="ChannelStartRetryPolicy"/> defaults.</param>
    /// <param name="delay">
    /// Backoff delay hook. Tests substitute a no-op so retry behaviour is asserted without
    /// real waiting; production leaves this <see langword="null"/> to use <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </param>
    public ChannelStartupCoordinator(
        ILogger logger,
        ChannelStartRetryPolicy? policy = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _policy = policy ?? new ChannelStartRetryPolicy();
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>
    /// Starts every adapter, retrying transient failures with bounded exponential backoff and
    /// abandoning terminal failures after a single attempt.
    /// </summary>
    /// <param name="adapters">The adapters to start.</param>
    /// <param name="dispatcher">Dispatcher passed to each adapter.</param>
    /// <param name="cancellationToken">Shutdown token.</param>
    /// <returns>One outcome per adapter, in the order supplied.</returns>
    public async Task<IReadOnlyList<ChannelStartOutcome>> StartAllAsync(
        IReadOnlyList<IChannelAdapter> adapters,
        IChannelDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var outcomes = new List<ChannelStartOutcome>(adapters.Count);
        foreach (var adapter in adapters)
            outcomes.Add(await StartOneAsync(adapter, dispatcher, cancellationToken).ConfigureAwait(false));

        return outcomes;
    }

    private async Task<ChannelStartOutcome> StartOneAsync(
        IChannelAdapter adapter,
        IChannelDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var channelType = adapter.ChannelType.Value;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await adapter.StartAsync(dispatcher, cancellationToken).ConfigureAwait(false);

                if (attempt > 1)
                {
                    _logger.LogInformation(
                        "Channel adapter '{ChannelType}' started on attempt {Attempt} of {MaxAttempts} after a transient failure",
                        channelType, attempt, _policy.MaxAttempts);
                }
                else
                {
                    _logger.LogInformation(
                        "Started channel adapter: {ChannelType} ({DisplayName})", channelType, adapter.DisplayName);
                }

                return new ChannelStartOutcome(channelType, adapter.DisplayName, Started: true, attempt, null, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var kind = ChannelFailureClassifier.Classify(ex);

                if (kind == ChannelFailureKind.Terminal)
                {
                    // Logged exactly once. Retrying a revoked token or malformed config only
                    // reproduces the same failure and buries the real cause in noise.
                    _logger.LogError(
                        ex,
                        "Failed to start channel adapter '{ChannelType}': terminal failure, not retrying. Channel is unavailable until the underlying configuration or credential is fixed.",
                        channelType);

                    return new ChannelStartOutcome(channelType, adapter.DisplayName, Started: false, attempt, kind, ex);
                }

                if (attempt >= _policy.MaxAttempts)
                {
                    _logger.LogError(
                        ex,
                        "Failed to start channel adapter '{ChannelType}' after {Attempts} attempt(s); giving up. Channel is unavailable until the gateway restarts.",
                        channelType, attempt);

                    return new ChannelStartOutcome(channelType, adapter.DisplayName, Started: false, attempt, kind, ex);
                }

                var delay = _policy.ComputeDelay(attempt);
                _logger.LogWarning(
                    ex,
                    "Transient failure starting channel adapter '{ChannelType}' (attempt {Attempt} of {MaxAttempts}); retrying in {DelayMs}ms.",
                    channelType, attempt, _policy.MaxAttempts, delay.TotalMilliseconds);

                await _delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Renders a single honest status line covering configured vs started adapters, naming any
    /// that failed. Replaces the misleading "Gateway started with N channel adapter(s)".
    /// </summary>
    /// <param name="outcomes">Outcomes returned by <see cref="StartAllAsync"/>.</param>
    /// <returns>A human-readable summary line.</returns>
    public static string DescribeStartup(IReadOnlyList<ChannelStartOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        var started = outcomes.Count(o => o.Started);
        var failed = outcomes.Where(o => !o.Started).Select(o => o.ChannelType).ToArray();

        return failed.Length == 0
            ? $"Gateway started with {started} of {outcomes.Count} channel adapter(s) running"
            : $"Gateway started DEGRADED: {started} of {outcomes.Count} channel adapter(s) running; failed: {string.Join(", ", failed)}";
    }
}
