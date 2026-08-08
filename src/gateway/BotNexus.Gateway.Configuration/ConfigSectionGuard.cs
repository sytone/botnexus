using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The destructive-write guard for <c>config.json</c> (issue #2816): refuses any candidate
/// document that would drop or empty a populated top-level section the mutation never named.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> On 2026-07-31 a routine maintenance write reduced the entire
/// <c>channels</c> section of a production <c>config.json</c> to exactly <c>{"enabled": true}</c>,
/// destroying the Service Bus connection settings and both Telegram bot tokens. The gateway then
/// started DEGRADED and Teams was silently dead for four days. The credentials were unrecoverable;
/// only the writer's own automatic backup made the installation restorable at all.</para>
/// <para>The mechanism was a typed round-trip: <see cref="PlatformConfig"/> models only part of
/// what <c>config.json</c> legitimately contains, so serialising the typed graph back over the
/// whole document collapses any section the graph models incompletely. <see cref="ChannelConfig"/>
/// modelled three properties, so a channel block whose real content was nested collapsed to the
/// single defaulted <c>enabled</c> property. That specific hole is now closed
/// (<see cref="ChannelConfig.AdditionalSettings"/>), but the <em>class</em> of defect is not: any
/// future property, any future section, any future writer can reintroduce it, and it fails
/// silently - the destroying command reports success.</para>
/// <para><b>Why the guard lives here and not in the commands.</b> The exact producer of the
/// 2026-07-31 write was never attributed. A per-command check would therefore have to be re-derived
/// in every command that writes config, would be forgotten by the next one added, and would still
/// have missed the incident if the producer was the gateway rather than the CLI. The guard is a
/// property of the <em>writer</em>, applied once in its single private write pipeline
/// (<c>PlatformConfigWriter.MutateCoreAsync</c>), so every path - <c>MutateAsync</c>,
/// <c>MutateValidatedAsync</c>, <c>UpdatePlatformConfigAsync</c>, <c>UpdateSectionAsync</c>,
/// <c>UpdateSectionEntryAsync</c>, <c>MutateSectionAsync</c>, <c>RemoveSectionEntryAsync</c> - is
/// covered by this one implementation.</para>
/// <para><b>Why it cannot be bypassed casually.</b> There is no "force" flag. The only way past the
/// guard is for the caller to <em>name</em> the section it intends to destroy, via the
/// <c>namedSections</c> argument threaded through the writer. That keeps deliberate removal working
/// (a caller removing <c>channels</c> says so) while leaving collateral destruction - the actual
/// defect, where a write aimed at <c>providers</c> flattens <c>channels</c> - impossible to express
/// by accident. Adding a blanket bypass would restore the incident.</para>
/// <para><b>Deliberate scope limits.</b> Only <em>container</em> values (objects and arrays) at the
/// <em>top level</em> are guarded:</para>
/// <list type="bullet">
///   <item>Scalars are excluded because clearing a scalar (for example the root <c>apiKey</c>) is
///   an ordinary, intentional, single-value edit, not the loss of a structured subtree.</item>
///   <item>Nested loss is not guarded here. The guard is a blast-radius fence for the failure that
///   actually happened - a whole section flattened - not a general deep-diff approval workflow,
///   which would reject a great many legitimate edits and be turned off within a week.</item>
///   <item>A section counts as destroyed when it is dropped, emptied, <em>or</em> when every key it
///   previously held is gone. That third case is not pedantry: the 2026-07-31 damage left
///   <c>channels</c> non-empty (it held the single defaulted <c>enabled</c> property), so a guard
///   that only tested for emptiness would have watched the incident happen. Rewriting a section's
///   values in place, adding to it, or removing some-but-not-all of its entries all remain
///   ordinary edits and pass.</item>
/// </list>
/// </remarks>
public static class ConfigSectionGuard
{
    /// <summary>
    /// The wildcard declaration for the one legitimate whole-document rewrite: a caller whose
    /// declared job is to regenerate <c>config.json</c> from scratch (<c>botnexus init --force</c>).
    /// </summary>
    /// <remarks>
    /// This is not a bypass flag and must not be used as one. <c>init --force</c> is the only
    /// operation in the product whose <em>stated purpose</em> is "discard the existing document",
    /// which the operator opts into explicitly with <c>--force</c> after being told the file already
    /// exists. Every other caller names the specific sections it is entitled to destroy, so a write
    /// aimed at one section still cannot flatten another. Adding a second use of this constant is a
    /// design smell: it means a caller is destroying sections it has not thought about, which is
    /// precisely the #2816 defect.
    /// </remarks>
    public static readonly IReadOnlyCollection<string> EntireDocument = new[] { "*" };

    /// <summary>
    /// Returns the names of populated top-level sections that <paramref name="candidate"/> would
    /// drop or empty relative to <paramref name="current"/> without the mutation having named them.
    /// </summary>
    /// <param name="current">The document as it exists on disk, read inside the writer lock.</param>
    /// <param name="candidate">The document the mutation produced.</param>
    /// <param name="namedSections">
    /// Top-level section names the caller explicitly declared it is operating on. A section named
    /// here may legitimately be emptied or removed (issue #2816 acceptance criterion 5). Compared
    /// case-insensitively, because config keys are matched case-insensitively everywhere else in
    /// the loader.
    /// </param>
    /// <returns>The destroyed section names, in document order; empty when the write is safe.</returns>
    public static IReadOnlyList<string> FindDestroyedSections(
        JsonObject current,
        JsonObject candidate,
        IReadOnlyCollection<string>? namedSections)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);

        List<string>? destroyed = null;

        foreach (var (key, before) in current)
        {
            if (!IsPopulatedContainer(before))
                continue;

            if (IsNamed(namedSections, key))
                continue;

            // The candidate may spell the key with different casing (a typed round-trip
            // re-emits camelCase). Absence is only real absence when no case-insensitive
            // match exists either.
            if (!TryGetIgnoreCase(candidate, key, out var after))
            {
                (destroyed ??= []).Add(key);
                continue;
            }

            if (!IsPopulatedContainer(after))
            {
                (destroyed ??= []).Add(key);
                continue;
            }

            // The incident shape: the section still exists and is still non-empty, but not one of
            // the keys the operator had written survives. 'channels' went from
            // {servicebus: {...}, telegram: {...}} to {enabled: true} - non-empty, and totally
            // destroyed. An emptiness test alone would have permitted it.
            if (before is JsonObject beforeObj && after is JsonObject afterObj
                && !SharesAnyKey(beforeObj, afterObj))
            {
                (destroyed ??= []).Add(key);
            }
        }

        return (IReadOnlyList<string>?)destroyed ?? [];
    }

    /// <summary>
    /// Builds the operator-facing rejection message. It names every destroyed section explicitly:
    /// the 2026-07-31 incident was survivable only because a backup happened to exist, and an
    /// unnamed "write rejected" message would have been no more actionable than the silent success
    /// that actually occurred.
    /// </summary>
    public static string FormatRejection(string configPath, IReadOnlyList<string> destroyedSections)
    {
        ArgumentNullException.ThrowIfNull(destroyedSections);

        var sections = string.Join(", ", destroyedSections);
        return $"Refusing to write {configPath}: this change would remove or empty the populated "
            + $"config section(s) '{sections}', which the operation did not name. "
            + "Nothing was written and the file on disk is unchanged. "
            + "If removing that section is intended, perform the removal as an operation that "
            + "targets it by name. (#2816)";
    }

    /// <summary>
    /// Whether a node is a container carrying content: a non-empty object or a non-empty array.
    /// Scalars, JSON nulls and empty containers are all "nothing to lose".
    /// </summary>
    private static bool IsPopulatedContainer(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Count > 0,
        JsonArray arr => arr.Count > 0,
        _ => false
    };

    private static bool IsNamed(IReadOnlyCollection<string>? namedSections, string key)
    {
        if (namedSections is null || namedSections.Count == 0)
            return false;

        foreach (var named in namedSections)
        {
            if (named == "*")
                return true;
            if (string.Equals(named, key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the two objects have at least one property name in common (case-insensitively, to
    /// tolerate a typed round-trip re-emitting keys in camelCase).
    /// </summary>
    private static bool SharesAnyKey(JsonObject before, JsonObject after)
    {
        foreach (var (key, _) in before)
        {
            if (TryGetIgnoreCase(after, key, out _))
                return true;
        }

        return false;
    }

    private static bool TryGetIgnoreCase(JsonObject obj, string key, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(key, out value))
            return true;

        foreach (var (candidateKey, candidateValue) in obj)
        {
            if (string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }

        value = null;
        return false;
    }
}
