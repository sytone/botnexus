namespace BotNexus.Gateway.Prompts;

/// <summary>
/// One instruction line carrying a STABLE identifier (#2433).
/// </summary>
/// <remarks>
/// The identifier is the whole point. Before #2433 a per-family instruction set was a bare
/// <c>string[]</c>, so the only way for a family to differ by one line was to restate all of them --
/// which is exactly how three arrays that were meant to share intent drifted into three unrelated
/// lists. With an id, a family variant can override, remove, or add a SINGLE rule and everything
/// else stays anchored to one source of truth.
/// </remarks>
/// <param name="Id">
/// The stable rule identifier, matched across the default rung and every variant that overlays it.
/// Ids are compared case-insensitively and must be non-blank.
/// </param>
/// <param name="Text">
/// The instruction text, or <see langword="null"/> to REMOVE the inherited rule with this id when
/// this rule appears in an overlay. A null-text rule on the default rung is meaningless and is
/// rejected at freeze time.
/// </param>
public sealed record PromptRule(string Id, string? Text)
{
    /// <summary>Creates a rule that removes the inherited rule with the given id.</summary>
    /// <param name="id">The stable id of the inherited rule to drop.</param>
    /// <returns>A rule whose <see cref="Text"/> is <see langword="null"/>.</returns>
    public static PromptRule Remove(string id) => new(id, null);
}
