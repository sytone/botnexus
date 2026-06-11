namespace BotNexus.Gateway.Contracts.Memory;

/// <summary>
/// Request to perform memory consolidation (dreaming). The provider merges,
/// summarises, or archives older entries to maintain long-term coherence.
/// </summary>
/// <param name="AgentId">The agent whose memory to consolidate.</param>
/// <param name="LookbackDays">
/// Number of days of daily notes to consider for consolidation. Defaults to 14.
/// </param>
/// <param name="MaxContentChars">
/// Maximum characters of source content to feed into the consolidation process.
/// Defaults to 50,000.
/// </param>
/// <param name="DryRun">
/// When true, the provider should return what it would consolidate without
/// actually modifying any stored state.
/// </param>
public sealed record AgentMemoryConsolidateRequest(
    string AgentId,
    int LookbackDays = 14,
    int MaxContentChars = 50_000,
    bool DryRun = false);
