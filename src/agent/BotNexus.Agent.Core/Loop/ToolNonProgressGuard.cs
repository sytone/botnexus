using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Bounded run-local aggregation for completed results classified by a tool-progress policy.
/// The guard owns sequence state only; policy owns classification and the loop owns control flow.
/// </summary>
internal sealed class ToolNonProgressGuard(ToolProgressPolicy policy)
{
    internal const int WarningThreshold = 3;
    internal const int StopThreshold = 6;
    internal const int AbsoluteToolResultLimit = 128;

    private readonly Dictionary<string, string> _observed = new(StringComparer.Ordinal);
    private int _count;
    private int _totalResults;
    private bool _warned;

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
        _observed.Clear();
        _count = 0;
        _warned = false;
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
