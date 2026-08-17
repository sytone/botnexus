// WorldEventProviderHealthObserver.cs
using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Providers;
using BotNexus.Gateway.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Providers;

/// <summary>
/// Turns repeated provider credential failures into <c>health.degraded</c> world events, and a
/// subsequent success into a <c>health.recovered</c> event (#3281).
///
/// <para>
/// <b>Why this exists.</b> During a seven-hour GitHub Copilot outage the gateway logged 391 failed
/// credential refreshes and emitted no event of any kind. Every Copilot-backed agent stopped
/// responding, and from a channel's point of view that was indistinguishable from the agents being
/// broken. <c>WorldEventTypes.HealthDegraded</c> was declared but had no publisher anywhere in the
/// codebase - the constant existed, the bus existed, and nothing had ever connected them.
/// </para>
///
/// <para>
/// <b>This class only emits; it does not decide what a user sees.</b> Channels subscribe and choose
/// their own presentation - a notice, a banner, a metric, or nothing. Emission is unconditional so
/// that the decision belongs to the channel rather than to the credential code.
/// </para>
///
/// <para>
/// <b>Debouncing is the point, not an optimisation.</b> A single transient 502 is normal operation
/// and must not raise an alarm, while a sustained outage must raise exactly one. Without the
/// threshold the observed incident would have produced 391 events; without the cooldown it would
/// have produced one per retry for seven hours. State is kept per provider so that one failing
/// provider never suppresses or triggers a signal about another.
/// </para>
/// </summary>
public sealed class WorldEventProviderHealthObserver : IProviderHealthObserver
{
    /// <summary>Payload key naming the affected provider.</summary>
    public const string PayloadProvider = "provider";

    /// <summary>Payload key carrying the exception type name of the failure.</summary>
    public const string PayloadFailureClass = "failureClass";

    /// <summary>Payload key carrying the upstream HTTP status code, when one was observed.</summary>
    public const string PayloadStatusCode = "statusCode";

    /// <summary>Payload key carrying how many consecutive failures have been seen.</summary>
    public const string PayloadConsecutiveFailures = "consecutiveFailures";

    /// <summary>Payload key carrying the UTC timestamp of the first failure in this streak.</summary>
    public const string PayloadFirstFailureUtc = "firstFailureUtc";

    /// <summary>Payload key carrying the failure detail message.</summary>
    public const string PayloadErrorMessage = "errorMessage";

    /// <summary>Event type published when a provider recovers after a degraded signal.</summary>
    public const string HealthRecoveredEventType = "health.recovered";

    private readonly IWorldEventBus _eventBus;
    private readonly ILogger<WorldEventProviderHealthObserver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly ConcurrentDictionary<string, ProviderFailureState> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the observer.</summary>
    /// <param name="eventBus">Bus that carries the health events.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock, injected so cooldown behaviour is testable without sleeping.</param>
    /// <param name="failureThreshold">Consecutive failures required before the first event. Must be at least 1.</param>
    /// <param name="cooldown">Minimum interval between repeat degraded events for one provider.</param>
    public WorldEventProviderHealthObserver(
        IWorldEventBus eventBus,
        ILogger<WorldEventProviderHealthObserver> logger,
        TimeProvider? timeProvider = null,
        int failureThreshold = 3,
        TimeSpan? cooldown = null)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _failureThreshold = failureThreshold < 1
            ? throw new ArgumentOutOfRangeException(nameof(failureThreshold), failureThreshold, "Threshold must be at least 1.")
            : failureThreshold;
        _cooldown = cooldown ?? TimeSpan.FromMinutes(15);
    }

    /// <inheritdoc/>
    public async Task RecordAsync(string providerId, ProviderCredentialOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(outcome);

        // NotConfigured is a steady state, not a fault. Treating it as one would fire a degraded
        // event on every host that simply does not use a given provider, and would also reset the
        // failure streak of a provider that is genuinely down.
        if (outcome.Status == ProviderCredentialStatus.NotConfigured)
        {
            return;
        }

        if (outcome.IsProviderFault)
        {
            await RecordFailureAsync(providerId, outcome, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RecordSuccessAsync(providerId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(string providerId, ProviderCredentialOutcome outcome, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        WorldEvent? toPublish = null;

        // The whole decision is taken under one lock per provider so that concurrent agent turns
        // hitting a downed provider cannot each observe "threshold not yet reached" and then all
        // publish - the duplicate-suppression guarantee has to be atomic with the count it reads.
        var state = _state.GetOrAdd(providerId, static _ => new ProviderFailureState());
        lock (state.Sync)
        {
            state.ConsecutiveFailures++;
            state.FirstFailureUtc ??= now;

            var thresholdReached = state.ConsecutiveFailures >= _failureThreshold;
            var cooldownElapsed = state.LastPublishedUtc is null || now - state.LastPublishedUtc >= _cooldown;

            if (thresholdReached && cooldownElapsed)
            {
                state.LastPublishedUtc = now;
                state.Degraded = true;

                var payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PayloadProvider] = providerId,
                    [PayloadConsecutiveFailures] = state.ConsecutiveFailures.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [PayloadFirstFailureUtc] = state.FirstFailureUtc.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                };

                if (!string.IsNullOrWhiteSpace(outcome.FailureClass))
                    payload[PayloadFailureClass] = outcome.FailureClass;

                // Omitted rather than zero when no status was observed: a fabricated 0 would read as
                // a real measurement and misdirect whoever is diagnosing the outage.
                if (outcome.StatusCode is { } status)
                    payload[PayloadStatusCode] = status.ToString(System.Globalization.CultureInfo.InvariantCulture);

                if (!string.IsNullOrWhiteSpace(outcome.ErrorMessage))
                    payload[PayloadErrorMessage] = outcome.ErrorMessage;

                toPublish = WorldEvent.Create(WorldEventTypes.HealthDegraded, payload);
            }
        }

        if (toPublish is not null)
        {
            _logger.LogWarning(
                "Provider '{Provider}' degraded: {Failures} consecutive credential failures (status {StatusCode}).",
                providerId,
                toPublish.Payload[PayloadConsecutiveFailures],
                outcome.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");

            await PublishSafelyAsync(toPublish, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecordSuccessAsync(string providerId, CancellationToken cancellationToken)
    {
        if (!_state.TryGetValue(providerId, out var state))
        {
            return;
        }

        WorldEvent? toPublish = null;
        lock (state.Sync)
        {
            // Only announce recovery if a degraded event was actually published. A provider that
            // failed once below the threshold and then succeeded never alarmed anyone, so an
            // unsolicited "recovered" would be the first a channel heard of it.
            var wasDegraded = state.Degraded;

            state.ConsecutiveFailures = 0;
            state.FirstFailureUtc = null;
            state.LastPublishedUtc = null;
            state.Degraded = false;

            if (wasDegraded)
            {
                toPublish = WorldEvent.Create(
                    HealthRecoveredEventType,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [PayloadProvider] = providerId });
            }
        }

        if (toPublish is not null)
        {
            _logger.LogInformation("Provider '{Provider}' recovered; credentials resolving again.", providerId);
            await PublishSafelyAsync(toPublish, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes without allowing a bus fault to propagate into credential resolution. Failing to
    /// report an outage must not itself become an outage.
    /// </summary>
    private async Task PublishSafelyAsync(WorldEvent worldEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _eventBus.PublishAsync(worldEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed publishing provider health event {EventType}.", worldEvent.EventType);
        }
    }

    private sealed class ProviderFailureState
    {
        public object Sync { get; } = new();
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset? FirstFailureUtc { get; set; }
        public DateTimeOffset? LastPublishedUtc { get; set; }
        public bool Degraded { get; set; }
    }
}
