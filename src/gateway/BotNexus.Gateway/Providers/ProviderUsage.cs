using System.Collections.Concurrent;

namespace BotNexus.Gateway.Providers;

/// <summary>
/// One provider's rate-limit headroom, as the provider last reported it.
/// </summary>
/// <remarks>
/// Every value here is stated by the provider on a real response, not inferred. A null field means
/// the provider did not report that dimension — OpenAI, for example, reports requests and total
/// tokens but not the input/output split Anthropic gives.
/// </remarks>
/// <param name="Provider">Canonical provider id, e.g. <c>anthropic</c>.</param>
/// <param name="RequestsLimit">Requests permitted in the current window.</param>
/// <param name="RequestsRemaining">Requests still available.</param>
/// <param name="RequestsResetUtc">When the request allowance refills.</param>
/// <param name="InputTokensLimit">Input tokens permitted in the current window.</param>
/// <param name="InputTokensRemaining">Input tokens still available.</param>
/// <param name="InputTokensResetUtc">When the input-token allowance refills.</param>
/// <param name="OutputTokensLimit">Output tokens permitted in the current window.</param>
/// <param name="OutputTokensRemaining">Output tokens still available.</param>
/// <param name="OutputTokensResetUtc">When the output-token allowance refills.</param>
/// <param name="TokensLimit">Combined token allowance, where the provider reports one.</param>
/// <param name="TokensRemaining">Combined tokens still available.</param>
/// <param name="TokensResetUtc">When the combined allowance refills.</param>
/// <param name="ObservedAtUtc">When this snapshot was captured.</param>
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

/// <summary>
/// One observed provider call: which model, and what it consumed.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="Requests"/> is exact — one call, counted once. The token figures are
/// <b>derived</b>, from the provider's own consumed-so-far counter for the current rate-limit
/// window, attributed to the model this request named.
/// </para>
/// <para>
/// That derivation is sound for sequential traffic and approximate under concurrency: if two calls
/// to different models overlap, the whole drop lands on whichever response returns second. The
/// portal labels these as observed rather than billed for exactly this reason. The alternative —
/// parsing usage out of the response — is not available here, because agent turns stream as SSE and
/// buffering that body to read a trailing usage frame is precisely what the streaming guard forbids.
/// </para>
/// </remarks>
/// <param name="Provider">Canonical provider id.</param>
/// <param name="Model">Model id named in the request body.</param>
/// <param name="Requests">Calls observed. Exact.</param>
/// <param name="Failures">Calls that returned a non-success status. Exact.</param>
/// <param name="InputTokens">Input tokens observed. Derived from the window's consumed counter.</param>
/// <param name="OutputTokens">Output tokens observed. Derived from the window's consumed counter.</param>
/// <param name="ObservedAtUtc">When the call completed.</param>
public sealed record ProviderUsageSample(
    string Provider,
    string Model,
    long Requests,
    long Failures,
    long InputTokens,
    long OutputTokens,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Holds the latest rate-limit headroom per provider and a bounded rolling window of observed calls.
/// </summary>
public interface IProviderUsageStore
{
    /// <summary>Records a rate-limit snapshot and, when a model is known, a usage sample.</summary>
    /// <param name="snapshot">The freshly parsed snapshot.</param>
    /// <param name="model">Model named in the request that produced it; may be null.</param>
    /// <param name="failed">
    /// Whether the call returned a non-success status. Recorded because a burn view that counts
    /// only successes hides the most expensive kind of mistake: a misconfigured model that 404s on
    /// every send looks identical to no traffic at all.
    /// </param>
    void Record(ProviderRateLimitSnapshot snapshot, string? model, bool failed = false);

    /// <summary>The most recent snapshot per provider.</summary>
    IReadOnlyDictionary<string, ProviderRateLimitSnapshot> Snapshots { get; }

    /// <summary>Usage samples observed at or after <paramref name="sinceUtc"/>.</summary>
    /// <param name="sinceUtc">Window start.</param>
    IReadOnlyList<ProviderUsageSample> SamplesSince(DateTimeOffset sinceUtc);
}

/// <summary>
/// In-memory <see cref="IProviderUsageStore"/>.
/// </summary>
/// <remarks>
/// Deliberately not persisted. This answers "what is my burn rate right now", which is a live
/// question: rate-limit headroom is meaningless once its reset has passed, and a restarted gateway
/// has no in-flight allowance to report. Persisting it would add a schema and a migration to store
/// values that are stale the moment the process stops.
/// </remarks>
public sealed class ProviderUsageStore : IProviderUsageStore
{
    /// <summary>
    /// How long samples are kept. Bounds memory and matches the longest window the portal offers.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// Hard cap on retained samples, so a runaway loop cannot grow this without bound between the
    /// time-based prunes. At roughly 100 bytes a sample this is a few megabytes worst case.
    /// </summary>
    private const int MaxSamples = 20_000;

    private readonly ConcurrentDictionary<string, ProviderRateLimitSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProviderUsageSample> _samples = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    /// <summary>Creates a store.</summary>
    /// <param name="timeProvider">Clock, injected so tests need no real waiting.</param>
    public ProviderUsageStore(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, ProviderRateLimitSnapshot> Snapshots => _snapshots;

    /// <inheritdoc/>
    public void Record(ProviderRateLimitSnapshot snapshot, string? model, bool failed = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var previous = _snapshots.TryGetValue(snapshot.Provider, out var p) ? p : null;
        _snapshots[snapshot.Provider] = snapshot;

        if (string.IsNullOrWhiteSpace(model))
            return;

        var input = Consumed(
            Used(previous?.InputTokensLimit, previous?.InputTokensRemaining), previous?.InputTokensResetUtc,
            Used(snapshot.InputTokensLimit, snapshot.InputTokensRemaining), snapshot.InputTokensResetUtc);
        var output = Consumed(
            Used(previous?.OutputTokensLimit, previous?.OutputTokensRemaining), previous?.OutputTokensResetUtc,
            Used(snapshot.OutputTokensLimit, snapshot.OutputTokensRemaining), snapshot.OutputTokensResetUtc);

        var sample = new ProviderUsageSample(
            snapshot.Provider, model.Trim(), Requests: 1, Failures: failed ? 1 : 0, input, output,
            snapshot.ObservedAtUtc == default ? _time.GetUtcNow() : snapshot.ObservedAtUtc);

        lock (_gate)
        {
            _samples.Add(sample);
            Prune();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProviderUsageSample> SamplesSince(DateTimeOffset sinceUtc)
    {
        lock (_gate)
        {
            Prune();
            return [.. _samples.Where(s => s.ObservedAtUtc >= sinceUtc)];
        }
    }

    /// <summary>Allowance consumed so far in the current window, or null when not reported.</summary>
    /// <param name="limit">Window allowance.</param>
    /// <param name="remaining">Allowance still available.</param>
    /// <returns>Consumed tokens, never negative.</returns>
    internal static long? Used(long? limit, long? remaining) =>
        limit is null || remaining is null ? null : Math.Max(0, limit.Value - remaining.Value);

    /// <summary>
    /// Tokens this call consumed, from the provider's own consumed-so-far counter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on <c>used</c> (limit minus remaining) rather than on the drop in <c>remaining</c>,
    /// because the two behave differently across a window boundary and the boundary is the common
    /// case, not the rare one: Anthropic's token windows roll roughly every minute, so two ordinary
    /// user turns almost never share one.
    /// </para>
    /// <para>
    /// Within a window, consumption is the rise in <c>used</c>. When the window has rolled — the
    /// reset instant moved — <c>used</c> has restarted from zero, so its current value <em>is</em>
    /// this window's consumption and is counted whole. An earlier version returned zero on a
    /// rollover to avoid mistaking a refill for spend; correct about the hazard, but it discarded
    /// nearly every real measurement, and the panel reported a flat zero burn while tokens were
    /// visibly being spent.
    /// </para>
    /// </remarks>
    /// <param name="beforeUsed">Consumed-so-far at the previous observation.</param>
    /// <param name="beforeReset">Reset instant at the previous observation.</param>
    /// <param name="afterUsed">Consumed-so-far now.</param>
    /// <param name="afterReset">Reset instant now.</param>
    /// <returns>Tokens attributable to this call, never negative.</returns>
    internal static long Consumed(long? beforeUsed, DateTimeOffset? beforeReset, long? afterUsed, DateTimeOffset? afterReset)
    {
        if (afterUsed is null)
            return 0;

        // No prior reading, or the window rolled: the counter restarted, so what it reads now is
        // what has been spent since it did.
        if (beforeUsed is null || beforeReset is null || afterReset is null || afterReset != beforeReset)
            return afterUsed.Value;

        var delta = afterUsed.Value - beforeUsed.Value;
        return delta > 0 ? delta : 0;
    }

    // Caller holds _gate.
    private void Prune()
    {
        var cutoff = _time.GetUtcNow() - Retention;
        _samples.RemoveAll(s => s.ObservedAtUtc < cutoff);
        if (_samples.Count > MaxSamples)
            _samples.RemoveRange(0, _samples.Count - MaxSamples);
    }
}
