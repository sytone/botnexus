using BotNexus.Gateway.Abstractions.Agents;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Run-local supervisor policy. Only whole completed turns can request a stop; neither prose nor
/// idle turns are evidence. All retained identities and sequences are fixed-size and bounded.
/// This is independent of the agent loop's tool-specific progress policy.
/// </summary>
internal sealed class SubAgentNoProgressDetector(int turnWindow, int failureThreshold)
{
    internal const int MaximumToolsPerTurn = 128;
    internal const int MaximumSuccessHistory = 256;
    private readonly int _window = Math.Clamp(turnWindow, 2, 64);
    private readonly int _threshold = Math.Clamp(failureThreshold, 2, 64);
    private readonly List<AgentActivityObservation> _pending = [];
    private readonly Queue<CompletedTurn> _turns = new();
    private readonly HashSet<AgentActivityObservation> _successes = [];
    private readonly Queue<AgentActivityObservation> _successOrder = new();
    private bool _overflow;
    private int _completedTurns;

    internal int RetainedTurnCount => _turns.Count;
    internal int PendingToolCount => _pending.Count;
    internal int RetainedSuccessCount => _successes.Count;

    internal SubAgentNoProgressDecision? Observe(AgentActivityObservation observation)
    {
        if (observation.Kind != AgentActivityKind.TurnCompleted)
        {
            if (observation.Kind is not (AgentActivityKind.ToolSucceeded or AgentActivityKind.ToolFailed or AgentActivityKind.ToolProgressUnknown))
                return null;
            if (_pending.Count < MaximumToolsPerTurn) _pending.Add(observation);
            else _overflow = true;
            return null;
        }

        if (_completedTurns < int.MaxValue) _completedTurns++;
        // Never infer a loop from a truncated batch, even if a productive result was beyond the cap.
        if (_overflow)
        {
            _pending.Clear();
            _turns.Clear();
            _overflow = false;
            return null;
        }

        // A success receipt may hide a durable mutation. Without complete progress evidence,
        // conservatively reset after the whole batch rather than infer unchanged underlying work.
        var newEvidence = _pending.Any(value => value.Kind == AgentActivityKind.ToolProgressUnknown);
        var hasSuccess = false;
        foreach (var success in _pending.Where(value => value.Kind == AgentActivityKind.ToolSucceeded))
        {
            hasSuccess = true;
            if (_successes.Contains(success)) continue;
            if (_successes.Count == MaximumSuccessHistory) _successes.Remove(_successOrder.Dequeue());
            _successes.Add(success);
            _successOrder.Enqueue(success);
            newEvidence = true;
        }
        var failures = _pending.Where(value => value.Kind == AgentActivityKind.ToolFailed).ToArray();
        _pending.Clear();
        if (newEvidence)
        {
            _turns.Clear();
            return null;
        }

        if (_turns.Count == _window) _turns.Dequeue();
        _turns.Enqueue(new CompletedTurn(_completedTurns, failures, hasSuccess && failures.Length == 0));
        if (failures.Length > 0)
        {
            var matches = _turns.Where(turn => turn.Failures.AsSpan().SequenceEqual(failures))
                .Select(turn => turn.TurnId).ToArray();
            if (matches.Length >= _threshold)
                return new("repeated-failing-turn-sequence", matches.Length, matches);
        }
        if (_turns.Count == _window && _turns.All(turn => turn.UnchangedSuccess))
            return new("unchanged-successful-evidence", _window, _turns.Select(turn => turn.TurnId).ToArray());
        return null;
    }

    private sealed record CompletedTurn(int TurnId, AgentActivityObservation[] Failures, bool UnchangedSuccess);
}

internal sealed record SubAgentNoProgressDecision(string Reason, int MatchedTurns, IReadOnlyList<int> SupportingTurnIds);
