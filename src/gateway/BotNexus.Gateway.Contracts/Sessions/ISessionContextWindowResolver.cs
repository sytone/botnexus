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
/// (already asynchronous) callers, which narrow <see cref="CompactionOptions.ContextWindowTokens"/>
/// to the scoped window before handing the options to the compactor. That keeps the compactor's
/// arithmetic - and therefore <see cref="CompactionOptions.TokenThresholdRatio"/> semantics and the
/// byte-based <see cref="CompactionOptions.LargestEntryBytesThreshold"/> bloat trigger - completely
/// unchanged, and makes the behaviour for an agent with no scoped window identical to before.
/// </para>
/// </remarks>
public interface ISessionContextWindowResolver
{
    /// <summary>
    /// Resolves the scoped context window for the supplied agent and conversation, using the same
    /// precedence chain the dispatch path uses: conversation override, then the agent descriptor's
    /// <c>ContextWindow</c>, then the resolved model's own context window.
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
    /// narrowed to <paramref name="scopedWindow"/>, or the original instance untouched when no scoped
    /// window was resolved. Only the base window changes; every other option - notably
    /// <see cref="CompactionOptions.TokenThresholdRatio"/> and
    /// <see cref="CompactionOptions.LargestEntryBytesThreshold"/> - is carried through verbatim.
    /// </summary>
    public static CompactionOptions Apply(CompactionOptions options, int? scopedWindow)
    {
        ArgumentNullException.ThrowIfNull(options);
        var usable = Usable(scopedWindow);
        return usable.HasValue ? options with { ContextWindowTokens = usable.Value } : options;
    }

    private static int? Usable(int? value) => value is > 0 ? value : null;
}
