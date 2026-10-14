using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Bounded run-local aggregation for completed results classified by a tool-progress policy.
/// The guard owns sequence state only; policy owns classification and the loop owns control flow.
/// </summary>
internal sealed class ToolNonProgressGuard(ToolProgressPolicy policy, Action<IReadOnlyList<GuardObservation>>? evidenceObserver = null)
{
    internal const int WarningThreshold = 3;
    internal const int StopThreshold = 6;
    internal const int AbsoluteToolResultLimit = 128;
    internal const int MaximumCyclePeriod = 4;
    internal const int RecentOutcomeLimit = 12;
    internal const int ObservedScopeLimit = 16;

    private readonly Dictionary<string, string> _observed = new(StringComparer.Ordinal);
    private readonly Queue<string> _scopeOrder = new();
    private readonly List<ClassifiedOutcome> _recent = [];
    private int _count;
    private int _housekeepingCount;
    private int _totalResults;
    private bool _warned;
    private readonly List<GuardObservation> _evidence = [];
    private int? _episode;
    internal IReadOnlyList<GuardObservation> Evidence => _evidence.ToArray();
    internal void Record(ToolNonProgressObservation observation, string disposition)
    {
        var safeKind = observation.Kind switch
        {
            "edit-non-progress" or "unchanged-housekeeping" or "unchanged-status" or "unchanged-read"
                or "absolute-tool-result-limit" or "classified-non-progress" => observation.Kind,
            _ => "classified-non-progress"
        };
        var value = new GuardObservation(safeKind, observation.ConsecutiveCount,
            observation.TotalResults, WarningThreshold, observation.AbsoluteLimitReached ? AbsoluteToolResultLimit : StopThreshold,
            observation.AbsoluteLimitReached, disposition, [Guid.NewGuid().ToString("N")]);
        if (_episode is { } index) _evidence[index] = value with { EvidenceReferences = _evidence[index].EvidenceReferences };
        else
        {
            if (_evidence.Count == 16) _evidence.RemoveAt(0);
            _evidence.Add(value);
            _episode = _evidence.Count - 1;
        }
        evidenceObserver?.Invoke(Evidence);
    }
    private ToolProgressDecision? _latestDecision;

    internal int RecentOutcomeCount => _recent.Count;
    internal int ObservedScopeCount => _observed.Count;

    internal async Task<ToolNonProgressObservation> ObserveAsync(
        IReadOnlyList<ToolCallContent> calls,
        IReadOnlyList<ToolResultAgentMessage> results,
        CancellationToken cancellationToken)
    {
        var byId = calls.ToDictionary(call => call.Id, StringComparer.Ordinal);
        var warning = false;
        foreach (var result in results)
        {
            // Control policy and the absolute fuse see every executed result, including incomplete ones.
            // Actual completed-result measurement belongs to the handle's separate event observer.
            cancellationToken.ThrowIfCancellationRequested();
            _totalResults++;
            // An unmatched completed result is still retained and counted, but is not evidence.
            if (!byId.TryGetValue(result.ToolCallId, out var call))
                continue;

            var decision = ValidateDecision(await policy(
                    new ToolProgressContext(call, result), cancellationToken)
                .ConfigureAwait(false));
            if (decision is null || decision.Outcome == ToolProgressOutcome.Neutral)
                continue;
            if (decision.Outcome == ToolProgressOutcome.Progress)
            {
                Reset();
                warning = false;
                continue;
            }

            // ValidateDecision guarantees nonempty identities; never retain raw tool payloads.
            var scope = decision.ScopeIdentity ?? throw new InvalidOperationException("Missing scope identity.");
            var evidence = decision.EvidenceIdentity ?? throw new InvalidOperationException("Missing evidence identity.");
            var kind = decision.Kind ?? throw new InvalidOperationException("Missing kind identity.");
            if (_observed.TryGetValue(scope, out var priorEvidence) && priorEvidence != evidence)
            {
                Reset();
                warning = false;
            }
            if (!_observed.ContainsKey(scope))
            {
                if (_observed.Count == ObservedScopeLimit)
                    _observed.Remove(_scopeOrder.Dequeue());
                _scopeOrder.Enqueue(scope);
            }
            _observed[scope] = evidence;
            if (_recent.Count == RecentOutcomeLimit)
                _recent.RemoveAt(0);
            _recent.Add(new ClassifiedOutcome(scope, evidence, kind));

            // Known status-only tools retain the existing six-result budget even across diverse
            // scopes. Research instead requires an actually repeated classified outcome sequence.
            _housekeepingCount = IsHousekeeping(kind) ? Math.Min(StopThreshold, _housekeepingCount + 1) : 0;
            _count = Math.Max(_housekeepingCount, RepeatedSuffixLength());
            _latestDecision = decision;
            if (!_warned && _count >= WarningThreshold)
            {
                _warned = true;
                warning = true;
            }
            // Process the full executed batch; later progress invalidates pending warning/stop.
        }

        var absoluteLimitReached = _totalResults >= AbsoluteToolResultLimit;
        return new ToolNonProgressObservation(
            _count,
            absoluteLimitReached ? "absolute-tool-result-limit" : _latestDecision?.Kind,
            _latestDecision?.Guidance,
            warning,
            _count >= StopThreshold || absoluteLimitReached,
            absoluteLimitReached,
            _totalResults);
    }

    internal void Reset()
    {
        if (_episode is { } index && _evidence[index].Disposition == "warning")
            _evidence[index] = _evidence[index] with { Disposition = "recovered" };
        _episode = null;
        _observed.Clear();
        _scopeOrder.Clear();
        _recent.Clear();
        _count = 0;
        _housekeepingCount = 0;
        _warned = false;
        _latestDecision = null;
        // External steering and progress do not extend the absolute run budget.
        evidenceObserver?.Invoke(Evidence);
    }

    private int RepeatedSuffixLength()
    {
        var longest = 1;
        for (var period = 1; period <= MaximumCyclePeriod; period++)
        {
            var length = period;
            for (var i = _recent.Count - 1; i >= period; i--)
            {
                if (_recent[i] != _recent[i - period])
                    break;
                length++;
            }
            // Require two complete copies, not a threshold on distinct targets or partial cycles.
            if (length >= 2 * period)
                longest = Math.Max(longest, length);
        }
        return longest;
    }

    private static bool IsHousekeeping(string kind)
        => kind is "unchanged-housekeeping" or "unchanged-status" or "clock-check";

    private static ToolProgressDecision? ValidateDecision(ToolProgressDecision? decision)
    {
        if (decision is null || decision.Outcome != ToolProgressOutcome.NoProgress)
            return decision;

        if (string.IsNullOrWhiteSpace(decision.ScopeIdentity)
            || string.IsNullOrWhiteSpace(decision.EvidenceIdentity)
            || string.IsNullOrWhiteSpace(decision.Kind))
        {
            throw new InvalidOperationException(
                "A non-progress tool policy decision requires scope, evidence, and kind identities.");
        }
        return decision;
    }

    private sealed record ClassifiedOutcome(string Scope, string Evidence, string Kind);
}

internal sealed record ToolNonProgressObservation(
    int ConsecutiveCount,
    string? Kind,
    string? Guidance,
    bool WarningReady,
    bool ShouldStop,
    bool AbsoluteLimitReached,
    int TotalResults);
