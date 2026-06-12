using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Tracks per-pair turn budgets, daily caps, and loop detection for agent-to-agent exchanges.
/// All state is in-memory and resets on process restart.
/// </summary>
public sealed class AgentExchangeBudgetTracker
{
    private readonly IOptions<AgentExchangeBudgetOptions> _options;
    private readonly ILogger<AgentExchangeBudgetTracker> _logger;
    private readonly ConcurrentDictionary<string, PairState> _pairStates = new();
    private readonly TimeProvider _timeProvider;

    public AgentExchangeBudgetTracker(
        IOptions<AgentExchangeBudgetOptions> options,
        ILogger<AgentExchangeBudgetTracker> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validates that the given agent pair is within budget. Throws if budget is exhausted or cooldown active.
    /// </summary>
    public void EnsureWithinBudget(AgentId initiator, AgentId target)
    {
        var key = MakePairKey(initiator, target);
        var state = _pairStates.GetOrAdd(key, _ => new PairState(_timeProvider.GetUtcNow()));
        var now = _timeProvider.GetUtcNow();
        var opts = _options.Value;

        lock (state)
        {
            // Reset daily counters if day changed
            if (state.DayStart.Date != now.Date)
            {
                state.DailyTurns = 0;
                state.DayStart = now;
            }

            // Check cooldown
            if (state.CooldownUntil.HasValue && now < state.CooldownUntil.Value)
            {
                var remaining = state.CooldownUntil.Value - now;
                _logger.LogWarning(
                    "Agent exchange {Initiator} -> {Target} blocked: cooldown active ({Remaining}s remaining)",
                    initiator.Value, target.Value, (int)remaining.TotalSeconds);
                throw new InvalidOperationException(
                    $"Agent pair '{initiator.Value}' -> '{target.Value}' is in cooldown for {(int)remaining.TotalSeconds}s due to loop detection.");
            }

            // Check daily cap
            if (state.DailyTurns >= opts.DailyTurnCap)
            {
                _logger.LogWarning(
                    "Agent exchange {Initiator} -> {Target} blocked: daily turn cap exhausted ({Cap})",
                    initiator.Value, target.Value, opts.DailyTurnCap);
                throw new InvalidOperationException(
                    $"Daily conversation budget exhausted for pair '{initiator.Value}' -> '{target.Value}' (cap: {opts.DailyTurnCap}).");
            }

            // Loop detection: if same pair re-engages within window
            if (state.LastExchangeEnd.HasValue)
            {
                var elapsed = (now - state.LastExchangeEnd.Value).TotalSeconds;
                if (elapsed < opts.LoopDetectionWindowSeconds)
                {
                    state.LoopCounter++;
                    if (state.LoopCounter >= opts.LoopThreshold)
                    {
                        state.CooldownUntil = now.AddSeconds(opts.CooldownOnLoopDetectSeconds);
                        state.LoopCounter = 0;
                        _logger.LogWarning(
                            "Loop detected for {Initiator} -> {Target}: {Count} rapid re-engagements. Cooldown {Seconds}s applied.",
                            initiator.Value, target.Value, opts.LoopThreshold, opts.CooldownOnLoopDetectSeconds);
                        throw new InvalidOperationException(
                            $"Loop detected for pair '{initiator.Value}' -> '{target.Value}'. Cooldown of {opts.CooldownOnLoopDetectSeconds}s applied.");
                    }
                }
                else
                {
                    // Outside window — reset loop counter
                    state.LoopCounter = 0;
                }
            }
        }
    }

    /// <summary>
    /// Records that an exchange completed, incrementing the daily turn count and marking the timestamp.
    /// </summary>
    public void RecordExchangeComplete(AgentId initiator, AgentId target, int turnsUsed)
    {
        var key = MakePairKey(initiator, target);
        var state = _pairStates.GetOrAdd(key, _ => new PairState(_timeProvider.GetUtcNow()));
        var now = _timeProvider.GetUtcNow();

        lock (state)
        {
            if (state.DayStart.Date != now.Date)
            {
                state.DailyTurns = 0;
                state.DayStart = now;
            }

            state.DailyTurns += turnsUsed;
            state.LastExchangeEnd = now;
        }
    }

    /// <summary>
    /// Gets the current budget state for a pair (for diagnostics).
    /// </summary>
    public PairBudgetInfo? GetPairInfo(AgentId initiator, AgentId target)
    {
        var key = MakePairKey(initiator, target);
        if (!_pairStates.TryGetValue(key, out var state))
            return null;

        lock (state)
        {
            return new PairBudgetInfo(
                DailyTurnsUsed: state.DailyTurns,
                DailyTurnCap: _options.Value.DailyTurnCap,
                LoopCounter: state.LoopCounter,
                CooldownUntil: state.CooldownUntil,
                LastExchangeEnd: state.LastExchangeEnd);
        }
    }

    /// <summary>
    /// Returns all tracked pairs and their budget state (for diagnostics endpoint).
    /// </summary>
    public IReadOnlyDictionary<string, PairBudgetInfo> GetAllPairInfo()
    {
        var result = new Dictionary<string, PairBudgetInfo>();
        foreach (var (key, state) in _pairStates)
        {
            lock (state)
            {
                result[key] = new PairBudgetInfo(
                    DailyTurnsUsed: state.DailyTurns,
                    DailyTurnCap: _options.Value.DailyTurnCap,
                    LoopCounter: state.LoopCounter,
                    CooldownUntil: state.CooldownUntil,
                    LastExchangeEnd: state.LastExchangeEnd);
            }
        }
        return result;
    }

    private static string MakePairKey(AgentId initiator, AgentId target) =>
        $"{initiator.Value}:{target.Value}";

    private sealed class PairState(DateTimeOffset dayStart)
    {
        public int DailyTurns;
        public DateTimeOffset DayStart = dayStart;
        public int LoopCounter;
        public DateTimeOffset? CooldownUntil;
        public DateTimeOffset? LastExchangeEnd;
    }
}

/// <summary>
/// Diagnostic snapshot of budget state for an agent pair.
/// </summary>
public sealed record PairBudgetInfo(
    int DailyTurnsUsed,
    int DailyTurnCap,
    int LoopCounter,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset? LastExchangeEnd);
