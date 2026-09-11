namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Ranks dotted-numeric version strings, so a catalog's pinned tag can be compared with the tags a
/// repository actually has.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow, and deliberately refuses to rank what it cannot. A leading <c>v</c> is
/// tolerated because tags carry one, and a missing trailing segment counts as zero so <c>1.2</c>
/// and <c>1.2.0</c> are one release. Anything else - a pre-release suffix, a date, a commit - is
/// unrankable, and unrankable means silent: a wrong ranking here would tell an operator a release
/// exists that does not, which is worse than saying nothing.
/// </para>
/// <para>
/// The portal has a parallel comparison for a different question - an offering against the
/// INSTALLED version - which cannot share this type: that code runs in a WebAssembly client that
/// does not reference gateway assemblies. The rules are intentionally the same; if one changes,
/// change both.
/// </para>
/// </remarks>
public static class VersionOrder
{
    /// <summary>Orders two rankable versions; the result is meaningless unless both are rankable.</summary>
    public static IComparer<string> Comparer { get; } = new VersionComparer();

    /// <summary>Whether a version string can be ranked at all.</summary>
    /// <param name="version">Candidate version or tag name.</param>
    public static bool IsRankable(string? version) => Segments(version) is not null;

    /// <summary>
    /// Compares two versions. Returns 0 when they are equal OR when either cannot be ranked, so
    /// callers must gate on <see cref="IsRankable"/> before reading a direction from the result.
    /// </summary>
    /// <param name="left">First version.</param>
    /// <param name="right">Second version.</param>
    public static int Compare(string? left, string? right)
    {
        var a = Segments(left);
        var b = Segments(right);

        if (a is null || b is null)
            return 0;

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
                return x < y ? -1 : 1;
        }

        return 0;
    }

    private static int[]? Segments(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        if (text.Length == 0)
            return null;

        var parts = text.Split('.');
        var result = new int[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var value) || value < 0)
                return null;

            result[i] = value;
        }

        return result;
    }

    private sealed class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y) => VersionOrder.Compare(x, y);
    }
}
