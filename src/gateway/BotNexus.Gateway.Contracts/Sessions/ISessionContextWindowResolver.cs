using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Resolves the effective context window (in tokens) that auto-compaction must budget against for a
/// given agent/conversation pair (#2896).
/// </summary>
/// <remarks>
/// <para>
/// Before this seam existed, <see cref="ISessionCompactor.ShouldCompact"/> computed its token
/// threshold from <see cref="CompactionOptions.ContextWindowTokens"/> alone - a single value bound
/// once, process-globally, from <c>gateway:compaction:contextWindowTokens</c>. Every other layer of
/// the stack already resolved a per-scope window (conversation override, then
/// <c>AgentDescriptor.ContextWindow</c>, then the registered model's own window), and all of it was
/// discarded at the compaction boundary. The failure is asymmetric and in the dangerous direction:
/// an agent pinned to a 32k model under a 200k global setting reaches provider overflow while
/// <c>ShouldCompact</c> still returns <see langword="false"/>, so the turn is salvaged by the lossy
/// reactive path (<c>AgentLoopRunner.CompactForOverflow</c>) instead of compacting on schedule.
/// </para>
/// <para>
/// Resolution is asynchronous because the conversation override is a store read, while
/// <c>ShouldCompact</c> is deliberately synchronous and pure. The seam is therefore applied by the
/// asynchronous callers, which preserve the configured policy window while applying resolved
/// model capacity or the explicitly selected inherited working window. Shared
/// <see cref="CompactionThreshold"/> arithmetic keeps pre-turn, mid-loop and diagnostic policy aligned.
/// The byte-based <see cref="CompactionOptions.LargestEntryBytesThreshold"/> trigger is independent.
/// </para>
/// </remarks>
public interface ISessionContextWindowResolver
{
    /// <summary>
    /// Resolves the actual model capacity for Configured/Model policies. Conversation explicitly
    /// selects conversation-over-agent-over-model working-window inheritance, bounded by known
    /// model capacity. A conversation's model selection still determines the actual model.
    /// </summary>
    /// <param name="agentId">The agent owning the session.</param>
    /// <param name="conversationId">The conversation the session belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The scoped window in tokens, or <see langword="null"/> when no layer supplies one - in which
    /// case the caller must keep <see cref="CompactionOptions.ContextWindowTokens"/> as configured.
    /// </returns>
    Task<int?> ResolveAsync(AgentId agentId, ConversationId conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The pure precedence rule behind <see cref="ISessionContextWindowResolver"/>, plus the helper that
/// applies a resolved window to a <see cref="CompactionOptions"/> instance. Kept separate from the
/// store-reading implementation so the precedence is unit-testable without any I/O.
/// </summary>
public static class ScopedCompactionWindow
{
    /// <summary>
    /// Applies most-specific-wins precedence across the three scoped layers. A non-positive value at
    /// any layer is treated as "unset" and falls through, mirroring
    /// <c>ConversationOverrideValidator.ValidateContextWindow</c>, which never admits a non-positive
    /// override in the first place.
    /// </summary>
    /// <param name="conversationOverride">The per-conversation override, when set.</param>
    /// <param name="agentWindow">The agent descriptor's configured window, when set.</param>
    /// <param name="modelWindow">The registered model's own context window, when known.</param>
    /// <returns>The most specific usable window, or <see langword="null"/> when no layer supplies one.</returns>
    public static int? Resolve(int? conversationOverride, int? agentWindow, int? modelWindow)
        => Usable(conversationOverride) ?? Usable(agentWindow) ?? Usable(modelWindow);

    /// <summary>
    /// Returns <paramref name="options"/> with <see cref="CompactionOptions.ContextWindowTokens"/>
    /// set to <paramref name="scopedWindow"/>, preserving the configured policy window separately,
    /// or the original instance untouched when no window was resolved. Every other option - notably
    /// <see cref="CompactionOptions.TokenThresholdRatio"/> and
    /// <see cref="CompactionOptions.LargestEntryBytesThreshold"/> - is carried through verbatim.
    /// </summary>
    public static CompactionOptions Apply(CompactionOptions options, int? scopedWindow)
    {
        ArgumentNullException.ThrowIfNull(options);
        var usable = Usable(scopedWindow);
        return usable.HasValue ? options.WithResolvedWindow(usable.Value) : options;
    }

    private static int? Usable(int? value) => value is > 0 ? value : null;
}
