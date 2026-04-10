using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Compacts session history by summarizing older messages while preserving recent turns.
/// </summary>
public interface ISessionCompactor
{
    /// <summary>
    /// Determines whether the session should be compacted based on history size and options.
    /// </summary>
    bool ShouldCompact(GatewaySession session, CompactionOptions options);

    /// <summary>
    /// Compacts the session history: summarizes older messages, preserves recent turns,
    /// and returns the result. The session's history is modified in place.
    /// </summary>
    Task<CompactionResult> CompactAsync(
        GatewaySession session,
        CompactionOptions options,
        CancellationToken cancellationToken = default);
}
