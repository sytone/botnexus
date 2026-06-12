using BotNexus.Gateway.Agents;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST endpoint for agent exchange budget diagnostics.
/// Surfaces inter-agent communication budget state for operators.
/// </summary>
[ApiController]
[Route("api/exchanges")]
public sealed class ExchangesController(
    AgentExchangeBudgetTracker? budgetTracker = null) : ControllerBase
{
    /// <summary>
    /// Returns budget state for all tracked agent pairs, optionally filtered by initiator or target.
    /// </summary>
    [HttpGet("budget")]
    public IActionResult GetBudget([FromQuery] string? initiator = null, [FromQuery] string? target = null)
    {
        if (budgetTracker is null)
            return Ok(new ExchangeBudgetResponse([], 0, DateTimeOffset.UtcNow));

        var allPairs = budgetTracker.GetAllPairInfo();
        var now = DateTimeOffset.UtcNow;

        var entries = new List<ExchangeBudgetEntry>();
        foreach (var (key, info) in allPairs)
        {
            var parts = key.Split(':');
            if (parts.Length != 2) continue;

            var pairInitiator = parts[0];
            var pairTarget = parts[1];

            if (initiator is not null && !string.Equals(pairInitiator, initiator, StringComparison.OrdinalIgnoreCase))
                continue;
            if (target is not null && !string.Equals(pairTarget, target, StringComparison.OrdinalIgnoreCase))
                continue;

            var cooldownActive = info.CooldownUntil.HasValue && info.CooldownUntil.Value > now;
            var cooldownRemainingSeconds = cooldownActive
                ? (int)Math.Ceiling((info.CooldownUntil!.Value - now).TotalSeconds)
                : 0;

            entries.Add(new ExchangeBudgetEntry(
                Initiator: pairInitiator,
                Target: pairTarget,
                DailyTurnsUsed: info.DailyTurnsUsed,
                DailyTurnCap: info.DailyTurnCap,
                CooldownActive: cooldownActive,
                CooldownRemainingSeconds: cooldownRemainingSeconds,
                LoopCounter: info.LoopCounter,
                LastInteraction: info.LastExchangeEnd));
        }

        return Ok(new ExchangeBudgetResponse(entries, entries.Count, now));
    }
}

internal sealed record ExchangeBudgetResponse(
    IReadOnlyList<ExchangeBudgetEntry> Pairs,
    int TotalPairs,
    DateTimeOffset Timestamp);

internal sealed record ExchangeBudgetEntry(
    string Initiator,
    string Target,
    int DailyTurnsUsed,
    int DailyTurnCap,
    bool CooldownActive,
    int CooldownRemainingSeconds,
    int LoopCounter,
    DateTimeOffset? LastInteraction);
