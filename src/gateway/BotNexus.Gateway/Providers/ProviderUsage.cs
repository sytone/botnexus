using System.Collections.Concurrent;

namespace BotNexus.Gateway.Providers;

/// <summary>One provider's rate-limit headroom, as the provider last reported it.</summary>
/// <remarks>A null field means the provider did not report that dimension.</remarks>
public sealed record ProviderRateLimitSnapshot(
    string Provider,
    long? RequestsLimit = null,
    long? RequestsRemaining = null,
    DateTimeOffset? RequestsResetUtc = null,
    long? InputTokensLimit = null,
    long? InputTokensRemaining = null,
    DateTimeOffset? InputTokensResetUtc = null,
    long? OutputTokensLimit = null,
    long? OutputTokensRemaining = null,
    DateTimeOffset? OutputTokensResetUtc = null,
    long? TokensLimit = null,
    long? TokensRemaining = null,
    DateTimeOffset? TokensResetUtc = null,
    DateTimeOffset ObservedAtUtc = default)
{
    /// <summary>True when the provider reported at least one usable dimension.</summary>
    public bool HasAnyLimit =>
        RequestsLimit is > 0 || InputTokensLimit is > 0 || OutputTokensLimit is > 0 || TokensLimit is > 0;
}

/// <summary>One HTTP response observed from a provider.</summary>
/// <remarks>
/// Requests and failures count response-bearing wire attempts. Transport exceptions have no HTTP
/// response and therefore produce no sample. Nullable token values distinguish unavailable counter
/// evidence from a measured zero delta. Combined tokens remain separate from the input/output split.
/// </remarks>
public sealed record ProviderUsageSample(
    string Provider,
    string? Model,
    long Requests,
    long Failures,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    DateTimeOffset ObservedAtUtc);

/// <summary>A bounded usage query and whether the exact requested window was retained.</summary>
public sealed record ProviderUsageQueryResult(
    IReadOnlyList<ProviderUsageSample> Samples,
    bool IsTruncated);

/// <summary>Holds latest provider headroom and a bounded rolling window of observed responses.</summary>
public interface IProviderUsageStore
{
    /// <summary>Records one response-bearing wire attempt and any counters it supplied.</summary>
    void Record(ProviderRateLimitSnapshot snapshot, string? model, bool failed = false);

    /// <summary>The most recent independently merged dimensions per provider.</summary>
    IReadOnlyDictionary<string, ProviderRateLimitSnapshot> Snapshots { get; }

    /// <summary>Queries samples and reports whether retention removed part of the requested window.</summary>
    ProviderUsageQueryResult QuerySince(DateTimeOffset sinceUtc);
}

/// <summary>In-memory <see cref="IProviderUsageStore"/>.</summary>
public sealed class ProviderUsageStore : IProviderUsageStore
{
    /// <summary>Longest retained time window.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private const int MaxSamples = 20_000;

    private readonly ConcurrentDictionary<string, ProviderRateLimitSnapshot> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CounterBaselines> _counterBaselines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _latestObservedAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProviderUsageSample> _samples = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private DateTimeOffset? _discardedThroughUtc;

    /// <summary>Creates a store.</summary>
    public ProviderUsageStore(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, ProviderRateLimitSnapshot> Snapshots => _snapshots;

    /// <inheritdoc/>
    public void Record(ProviderRateLimitSnapshot snapshot, string? model, bool failed = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var observedAt = snapshot.ObservedAtUtc == default ? _time.GetUtcNow() : snapshot.ObservedAtUtc;
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

        // The stale check, all dimension deltas, all baseline advances, the latest view and sample
        // publication are one provider transition under one gate. Stale responses remain countable
        // wire attempts, but cannot report a token delta or rewind any baseline/headroom dimension.
        lock (_gate)
        {
            var isStale = _latestObservedAt.TryGetValue(snapshot.Provider, out var latest) && observedAt < latest;
            if (!_counterBaselines.TryGetValue(snapshot.Provider, out var baselines))
                baselines = new CounterBaselines();

            long? input = null;
            long? output = null;
            long? total = null;
            if (!isStale)
            {
                input = DeltaAndAdvance(
                    ref baselines.Input,
                    snapshot.InputTokensLimit,
                    snapshot.InputTokensRemaining,
                    snapshot.InputTokensResetUtc);
                output = DeltaAndAdvance(
                    ref baselines.Output,
                    snapshot.OutputTokensLimit,
                    snapshot.OutputTokensRemaining,
                    snapshot.OutputTokensResetUtc);
                total = DeltaAndAdvance(
                    ref baselines.Total,
                    snapshot.TokensLimit,
                    snapshot.TokensRemaining,
                    snapshot.TokensResetUtc);

                _counterBaselines[snapshot.Provider] = baselines;
                _latestObservedAt[snapshot.Provider] = observedAt;
                _snapshots[snapshot.Provider] = MergeSnapshot(
                    _snapshots.TryGetValue(snapshot.Provider, out var current) ? current : null,
                    snapshot,
                    observedAt);
            }

            _samples.Add(new ProviderUsageSample(
                snapshot.Provider,
                normalizedModel,
                Requests: 1,
                Failures: failed ? 1 : 0,
                input,
                output,
                total,
                observedAt));
            Prune();
        }
    }

    /// <inheritdoc/>
    public ProviderUsageQueryResult QuerySince(DateTimeOffset sinceUtc)
    {
        lock (_gate)
        {
            Prune();
            return new ProviderUsageQueryResult(
                [.. _samples.Where(sample => sample.ObservedAtUtc >= sinceUtc)],
                _discardedThroughUtc is { } discardedThrough && discardedThrough >= sinceUtc);
        }
    }

    /// <summary>Allowance consumed so far, or null when the dimension is unavailable.</summary>
    internal static long? Used(long? limit, long? remaining) =>
        limit is null || remaining is null ? null : Math.Max(0, limit.Value - remaining.Value);

    /// <summary>Delta in one provider counter window, or null when the new reading is unavailable.</summary>
    internal static long? Consumed(
        long? beforeUsed,
        DateTimeOffset? beforeReset,
        long? afterUsed,
        DateTimeOffset? afterReset)
    {
        if (afterUsed is null)
            return null;
        if (beforeUsed is null || beforeReset is null || afterReset is null || afterReset != beforeReset)
            return afterUsed.Value;

        return Math.Max(0, afterUsed.Value - beforeUsed.Value);
    }

    private static long? DeltaAndAdvance(
        ref CounterBaseline baseline,
        long? limit,
        long? remaining,
        DateTimeOffset? resetUtc)
    {
        var used = Used(limit, remaining);
        if (used is null)
            return null;

        var delta = Consumed(baseline.Used, baseline.ResetUtc, used, resetUtc);
        baseline = new CounterBaseline(used, resetUtc);
        return delta;
    }

    private static ProviderRateLimitSnapshot MergeSnapshot(
        ProviderRateLimitSnapshot? current,
        ProviderRateLimitSnapshot incoming,
        DateTimeOffset observedAt)
    {
        // Preserve the first provider-stated object exactly. Handler snapshots already carry an
        // observation time; direct callers may intentionally use the default and existing store
        // semantics expose that same snapshot instance.
        if (current is null)
            return incoming;

        static (long? Limit, long? Remaining, DateTimeOffset? Reset) Dimension(
            long? newLimit,
            long? newRemaining,
            DateTimeOffset? newReset,
            long? oldLimit,
            long? oldRemaining,
            DateTimeOffset? oldReset) =>
            newLimit is not null && newRemaining is not null
                ? (newLimit, newRemaining, newReset)
                : (oldLimit, oldRemaining, oldReset);

        var requests = Dimension(
            incoming.RequestsLimit, incoming.RequestsRemaining, incoming.RequestsResetUtc,
            current?.RequestsLimit, current?.RequestsRemaining, current?.RequestsResetUtc);
        var input = Dimension(
            incoming.InputTokensLimit, incoming.InputTokensRemaining, incoming.InputTokensResetUtc,
            current?.InputTokensLimit, current?.InputTokensRemaining, current?.InputTokensResetUtc);
        var output = Dimension(
            incoming.OutputTokensLimit, incoming.OutputTokensRemaining, incoming.OutputTokensResetUtc,
            current?.OutputTokensLimit, current?.OutputTokensRemaining, current?.OutputTokensResetUtc);
        var total = Dimension(
            incoming.TokensLimit, incoming.TokensRemaining, incoming.TokensResetUtc,
            current?.TokensLimit, current?.TokensRemaining, current?.TokensResetUtc);

        return new ProviderRateLimitSnapshot(
            incoming.Provider,
            requests.Limit, requests.Remaining, requests.Reset,
            input.Limit, input.Remaining, input.Reset,
            output.Limit, output.Remaining, output.Reset,
            total.Limit, total.Remaining, total.Reset,
            observedAt);
    }

    // Caller holds _gate.
    private void Prune()
    {
        var cutoff = _time.GetUtcNow() - Retention;
        var expired = _samples.Where(sample => sample.ObservedAtUtc < cutoff).ToList();
        if (expired.Count > 0)
        {
            NoteDiscarded(expired);
            _samples.RemoveAll(sample => sample.ObservedAtUtc < cutoff);
        }

        if (_samples.Count > MaxSamples)
        {
            var removeCount = _samples.Count - MaxSamples;
            NoteDiscarded(_samples.GetRange(0, removeCount));
            _samples.RemoveRange(0, removeCount);
        }
    }

    private void NoteDiscarded(IEnumerable<ProviderUsageSample> discarded)
    {
        foreach (var sample in discarded)
        {
            if (_discardedThroughUtc is null || sample.ObservedAtUtc > _discardedThroughUtc)
                _discardedThroughUtc = sample.ObservedAtUtc;
        }
    }

    private readonly record struct CounterBaseline(long? Used, DateTimeOffset? ResetUtc);

    private sealed class CounterBaselines
    {
        public CounterBaseline Input;
        public CounterBaseline Output;
        public CounterBaseline Total;
    }
}
