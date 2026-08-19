namespace BotNexus.SourceGenerators;

using System;
using System.Collections.Generic;

/// <summary>
/// One declared doctor check, advisory or config check, as read off a <c>[DoctorCheck]</c>
/// attribute (#3319).
/// </summary>
/// <remarks>
/// A plain value type with structural equality so the incremental pipeline can cache on it. A model
/// compared by reference would re-run emission on every keystroke in an unrelated file.
/// </remarks>
public sealed class DoctorCheckModel : IEquatable<DoctorCheckModel>, IComparable<DoctorCheckModel>
{
    /// <summary>Stable id the check reports, matching its own <c>Id</c> property.</summary>
    public string Id { get; set; }

    /// <summary>
    /// Which generated list this declaration belongs to: <c>Aggregate</c>, <c>Config</c> or
    /// <c>Advisory</c>. Declared, never inferred - <c>DoctorConfigCommand</c> documents that an
    /// advisory has no <c>Apply</c> and must never be reached by <c>--yes</c>, so the kind is part
    /// of the declaration rather than something a heuristic could get wrong.
    /// </summary>
    public string Suite { get; set; }

    /// <summary>
    /// Explicit ordering key within the suite. Roslyn does not promise a stable enumeration order
    /// across the syntax trees of a compilation, and the aggregate suite's order is operator-visible
    /// (it is the order sections print in), so the order is declared rather than derived.
    /// </summary>
    public int Order { get; set; }

    /// <summary>Fully-qualified type name used to construct the instance in the generated list.</summary>
    public string TypeName { get; set; }

    /// <summary>Orders by <see cref="Order"/>, then by <see cref="Id"/> so ties are still deterministic.</summary>
    public int CompareTo(DoctorCheckModel other)
    {
        if (other is null)
        {
            return 1;
        }

        var byOrder = Order.CompareTo(other.Order);
        return byOrder != 0 ? byOrder : string.CompareOrdinal(Id ?? string.Empty, other.Id ?? string.Empty);
    }

    /// <inheritdoc />
    public bool Equals(DoctorCheckModel other)
        => other is not null
            && Id == other.Id
            && Suite == other.Suite
            && Order == other.Order
            && TypeName == other.TypeName;

    /// <inheritdoc />
    public override bool Equals(object obj) => Equals(obj as DoctorCheckModel);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + (Id?.GetHashCode() ?? 0);
            hash = (hash * 31) + (Suite?.GetHashCode() ?? 0);
            hash = (hash * 31) + Order;
            hash = (hash * 31) + (TypeName?.GetHashCode() ?? 0);
            return hash;
        }
    }
}

/// <summary>The three suites a <c>[DoctorCheck]</c> declaration can belong to (#3319).</summary>
public static class DoctorSuiteNames
{
    /// <summary>The aggregate <c>botnexus doctor</c> suite - <c>IDoctorCheck</c> implementations.</summary>
    public const string Aggregate = "Aggregate";

    /// <summary><c>doctor config</c> auto-applicable checks - <c>IConfigCheck</c> implementations.</summary>
    public const string Config = "Config";

    /// <summary><c>doctor config</c> read-only findings - <c>IConfigAdvisory</c> implementations.</summary>
    public const string Advisory = "Advisory";

    /// <summary>Every suite name, in the order the generated inventory lists them.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Aggregate, Config, Advisory };
}
