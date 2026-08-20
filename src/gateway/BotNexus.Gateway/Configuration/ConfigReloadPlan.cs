using System.Collections.Frozen;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Describes which configuration paths a reload actually changed, so expensive reload consumers
/// can decide whether the change can possibly affect them (#2728).
/// </summary>
/// <remarks>
/// <para>This is a <b>scoping</b> record only. It does not change the reload mechanism (#31), what
/// is cached, or how long a cache lives — it exists so a consumer can skip recomputation that the
/// change cannot influence.</para>
/// <para><b>Fail-open by construction.</b> A plan with no path set, or one flagged as a
/// whole-document write, reports every consumer as affected. Predicates built over this record
/// therefore treat classification completeness as a <i>performance</i> property, never a
/// correctness one: an unclassified or unrecognised path always performs the full reload.</para>
/// </remarks>
public sealed record ConfigReloadPlan
{
    private static readonly FrozenSet<string> EmptyPaths = FrozenSet<string>.Empty;

    private ConfigReloadPlan(bool isWholeDocument, FrozenSet<string> changedPaths)
    {
        IsWholeDocument = isWholeDocument;
        ChangedPaths = changedPaths;
    }

    /// <summary>
    /// True when the write replaced the whole configuration document, or when the writer could not
    /// attribute the change to specific paths. Consumers must perform the full reload.
    /// </summary>
    public bool IsWholeDocument { get; }

    /// <summary>
    /// The configuration paths this reload changed, in <c>section:subsection</c> or
    /// <c>section.subsection</c> form. Empty when <see cref="IsWholeDocument"/> is true.
    /// </summary>
    public IReadOnlySet<string> ChangedPaths { get; }

    /// <summary>
    /// A plan describing a whole-document replacement — the fail-open path every consumer honours.
    /// </summary>
    public static ConfigReloadPlan WholeDocument { get; } = new(isWholeDocument: true, EmptyPaths);

    /// <summary>
    /// Builds a plan for a set of changed configuration paths. A null or empty set degrades to
    /// <see cref="WholeDocument"/> so an absent classification can never cause a consumer to skip.
    /// </summary>
    public static ConfigReloadPlan ForPaths(IEnumerable<string>? changedPaths)
    {
        if (changedPaths is null)
            return WholeDocument;

        var normalised = changedPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        return normalised.Count == 0
            ? WholeDocument
            : new ConfigReloadPlan(isWholeDocument: false, normalised);
    }

    /// <summary>Convenience overload for the common single/short path list.</summary>
    public static ConfigReloadPlan ForPaths(params string[] changedPaths)
        => ForPaths((IEnumerable<string>?)changedPaths);

    /// <summary>
    /// Splits a configuration path into its segments, accepting either <c>:</c> or <c>.</c> as the
    /// separator so callers on the IConfiguration side and the JSON side agree.
    /// </summary>
    public static string[] SplitPath(string path)
        => string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split([':', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
