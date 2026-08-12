using System.Collections.Concurrent;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Concurrency-safe implementation of <see cref="IActiveLoopTracker"/>. In-flight runs live in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by run identity, so completion removes the
/// exact run that started and the live count is simply the size of that map - the count and the
/// detail list are therefore the same fact, not two facts that can drift (issue #2794).
/// </summary>
public sealed class ActiveLoopTracker(TimeProvider? timeProvider = null) : IActiveLoopTracker
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, ActiveLoopDetail> _active = new();
    private int _peakCount;
    private long _totalCompleted;

    /// <inheritdoc />
    public int ActiveCount => _active.Count;

    /// <inheritdoc />
    public int PeakCount => Volatile.Read(ref _peakCount);

    /// <inheritdoc />
    public long TotalCompleted => Interlocked.Read(ref _totalCompleted);

    /// <inheritdoc />
    public ActiveLoopRegistration TrackStart(string? agentId = null, string? conversationId = null, string? sessionId = null)
    {
        var id = Guid.NewGuid();
        _active[id] = new ActiveLoopDetail
        {
            LoopId = id.ToString("N"),
            AgentId = Normalize(agentId),
            ConversationId = Normalize(conversationId),
            SessionId = Normalize(sessionId),
            StartedAtUtc = _timeProvider.GetUtcNow()
        };

        // Update peak using a compare-and-swap loop against the observed live size.
        var current = _active.Count;
        int peak;
        while (current > (peak = Volatile.Read(ref _peakCount)))
        {
            if (Interlocked.CompareExchange(ref _peakCount, current, peak) == peak)
                break;
        }

        return new ActiveLoopRegistration(id);
    }

    /// <inheritdoc />
    public void TrackEnd(ActiveLoopRegistration registration)
    {
        // Only a removal that actually took the entry counts as a completion: an unknown or
        // repeated registration must not inflate TotalCompleted.
        if (registration.IsTracked && _active.TryRemove(registration.Id, out _))
            Interlocked.Increment(ref _totalCompleted);
    }

    /// <inheritdoc />
    public ActiveLoopSnapshot GetSnapshot()
    {
        // Materialise once, then derive the headline count from the materialised list so the two
        // cannot disagree even if a loop starts or ends between the two reads (AC2).
        var loops = _active.Values
            .OrderBy(d => d.StartedAtUtc)
            .ThenBy(d => d.LoopId, StringComparer.Ordinal)
            .ToList();

        return new ActiveLoopSnapshot
        {
            ActiveCount = loops.Count,
            PeakCount = PeakCount,
            TotalCompleted = TotalCompleted,
            ActiveLoops = loops
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
