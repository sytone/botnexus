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

    private readonly Dictionary<string, string> _observed = new(StringComparer.Ordinal);
    private int _count;
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

    internal async Task<ToolNonProgressObservation> ObserveAsync(
        IReadOnlyList<ToolCallContent> calls,
        IReadOnlyList<ToolResultAgentMessage> results,
        CancellationToken cancellationToken)
    {
        var byId = calls.ToDictionary(call => call.Id, StringComparer.Ordinal);
        var warning = false;
        ToolProgressDecision? latestDecision = null;
        foreach (var result in results)
        {
            // Control policy and the absolute fuse see every executed result, including incomplete ones.
            // Actual completed-result measurement belongs to the handle's separate event observer.
            _totalResults++;
            if (!byId.TryGetValue(result.ToolCallId, out var call))
            {
                Reset();
                continue;
            }

            var decision = ValidateDecision(await policy(
                    new ToolProgressContext(call, result),
                    cancellationToken)
                .ConfigureAwait(false));
            if (decision is null || decision.IsProgress)
            {
                Reset();
                continue;
            }

            var scope = decision.ScopeIdentity!;
            var evidence = decision.EvidenceIdentity!;
            if (_observed.TryGetValue(scope, out var priorEvidence) && priorEvidence != evidence)
                Reset();
            if (!_observed.ContainsKey(scope)
                && _observed.Count >= 2
                && !decision.Kind!.Equals("unchanged-housekeeping", StringComparison.Ordinal))
            {
                Reset();
            }
            _observed[scope] = evidence;
            _count++;

            latestDecision = decision;
            if (!_warned && _count >= WarningThreshold)
            {
                _warned = true;
                warning = true;
            }
            // Finish processing the entire executed batch. A sibling result is never discarded.
        }

        var absoluteLimitReached = _totalResults >= AbsoluteToolResultLimit;
        return new ToolNonProgressObservation(
            _count,
            absoluteLimitReached ? "absolute-tool-result-limit" : latestDecision?.Kind,
            latestDecision?.Guidance,
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
        _count = 0;
        _warned = false;
        evidenceObserver?.Invoke(Evidence);
    }

    private static ToolProgressDecision? ValidateDecision(ToolProgressDecision? decision)
    {
        if (decision is null || decision.IsProgress)
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
}

internal sealed record ToolNonProgressObservation(
    int ConsecutiveCount,
    string? Kind,
    string? Guidance,
    bool WarningReady,
    bool ShouldStop,
    bool AbsoluteLimitReached,
    int TotalResults);
