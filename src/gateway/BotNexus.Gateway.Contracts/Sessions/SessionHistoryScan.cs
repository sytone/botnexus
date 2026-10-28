using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>A bounded, transcript-only read for an already authorized session owner.</summary>
/// <remarks>Stores must verify ExpectedAgentId before reading content. Budgets cap managed
/// materialization, including a single oversized entry. MaxCharacters is a conservative
/// scan-unit budget: UTF-16 content characters in memory, UTF-8 content bytes in SQLite,
/// raw JSON characters in File (including ignored fields). Incomplete means the prefix is not
/// the entire transcript; callers must never interpret it as an exhaustive negative search.</remarks>
public sealed record SessionHistoryScan(AgentId ExpectedAgentId, int MaxEntries, int MaxCharacters)
{
    /// <summary>Rejects unbounded or invalid reads at the storage boundary.</summary>
    public void Validate()
    {
        if (MaxEntries is < 1 or > 2000 || MaxCharacters is < 1 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(MaxEntries), "History scans allow 1..2000 entries and 1..1048576 characters.");
    }
}

/// <summary>A bounded transcript prefix and its explicit exhaustion and materialization counters.</summary>
public sealed record SessionHistoryScanPage(IReadOnlyList<SessionEntry> Entries, bool Complete, int MaterializedCharacters);
