namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// A parsed <c>major.minor</c> model version (issue #2374). Exists so capability gating can ask
/// "is this model at least Opus 4.6?" instead of pattern-matching a hand-maintained list of literal
/// id substrings. Substring matching silently mis-ordered ids (<c>claude-opus-4.50</c> compared as
/// if its minor were <c>5</c>) and, worse, failed CLOSED on any id the list had never seen -- which
/// is exactly how <c>claude-opus-5</c> ended up unclassifiable by every provider.
/// </summary>
/// <param name="Major">The major version component (the <c>4</c> in <c>opus-4.6</c>).</param>
/// <param name="Minor">The minor version component (the <c>6</c> in <c>opus-4.6</c>), or 0 when the id carries no minor.</param>
public readonly record struct ModelVersion(int Major, int Minor) : IComparable<ModelVersion>
{
    /// <summary>
    /// Orders two versions numerically by major then minor, so <c>4.50</c> correctly sorts ABOVE
    /// <c>4.6</c> rather than being compared one character at a time.
    /// </summary>
    /// <param name="other">The version to compare against.</param>
    /// <returns>A negative value, zero, or a positive value per <see cref="IComparable{T}"/>.</returns>
    public int CompareTo(ModelVersion other)
    {
        var major = Major.CompareTo(other.Major);
        return major != 0 ? major : Minor.CompareTo(other.Minor);
    }

    /// <summary>True when this version is at least <paramref name="other"/>.</summary>
    /// <param name="other">The minimum version to test against.</param>
    /// <returns>True when this version is greater than or equal to <paramref name="other"/>.</returns>
    public bool AtLeast(ModelVersion other) => CompareTo(other) >= 0;

    /// <summary>Renders the version as <c>major.minor</c> for diagnostics.</summary>
    /// <returns>The dotted version string.</returns>
    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>
/// Parses a model family token and its version out of a provider model id (issue #2374). This is
/// the ONE place in the agent that knows how vendors spell a version, so adding the next model
/// generation is a registry entry rather than an edit to four duplicated substring lists.
/// <para>
/// Handles every id shape the providers actually see: Copilot's dotted ids
/// (<c>claude-opus-4.6</c>), Anthropic's dashed + date-stamped ids
/// (<c>claude-opus-4-5-20250929</c>), bare family fragments (<c>opus-4.6</c>) and prefixed ids
/// (<c>copilot/claude-opus-5</c>). A trailing release-date component is deliberately NOT read as a
/// minor version -- a component of three or more digits is a date stamp, not a version.
/// </para>
/// </summary>
public static class ModelFamilyVersion
{
    // A version component of 3+ digits is a release date stamp (e.g. the 20250929 in
    // claude-opus-4-5-20250929), never a minor version. Real minor versions are 1-2 digits.
    private const int MaxMinorDigits = 2;

    /// <summary>
    /// Attempts to read the version that immediately follows <paramref name="family"/> in
    /// <paramref name="modelId"/>. The family token must sit on a token boundary so
    /// <c>opus</c> does not match inside an unrelated word.
    /// </summary>
    /// <param name="modelId">The provider model id, for example <c>claude-opus-4.6</c>.</param>
    /// <param name="family">The family token to locate, for example <c>opus</c> or <c>gpt</c>.</param>
    /// <param name="version">The parsed version when this returns true; otherwise the default.</param>
    /// <returns>True when the family token is present and followed by a numeric version.</returns>
    public static bool TryParse(string? modelId, string family, out ModelVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        version = default;
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var span = modelId.AsSpan();
        var searchFrom = 0;

        // Scan every occurrence: an id may legitimately mention the token more than once and only
        // one of them is followed by a version.
        while (searchFrom < span.Length)
        {
            var index = span[searchFrom..].IndexOf(family, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var start = searchFrom + index;
            var end = start + family.Length;

            if (IsTokenBoundary(span, start, end) && TryParseVersionAt(span[end..], out version))
                return true;

            searchFrom = start + 1;
        }

        return false;
    }

    /// <summary>
    /// Convenience predicate: true when <paramref name="modelId"/> belongs to
    /// <paramref name="family"/> and its version is at least <paramref name="major"/>.<paramref name="minor"/>.
    /// An unparseable or unrelated id degrades safely to false rather than throwing.
    /// </summary>
    /// <param name="modelId">The provider model id.</param>
    /// <param name="family">The family token to require.</param>
    /// <param name="major">The minimum major version.</param>
    /// <param name="minor">The minimum minor version.</param>
    /// <returns>True when the id parses into the family at or above the given version.</returns>
    public static bool IsAtLeast(string? modelId, string family, int major, int minor = 0) =>
        TryParse(modelId, family, out var version) && version.AtLeast(new ModelVersion(major, minor));

    // The family token must not be glued to surrounding letters/digits: "opus" in "claude-opus-5"
    // is a real token, but "opus" inside "octopus5" is not.
    private static bool IsTokenBoundary(ReadOnlySpan<char> span, int start, int end)
    {
        if (start > 0 && char.IsLetterOrDigit(span[start - 1]))
            return false;

        // Must be followed by a separator then the version; a bare family with no trailing version
        // is not a version match.
        return end < span.Length && (span[end] == '-' || span[end] == '.' || span[end] == '_');
    }

    private static bool TryParseVersionAt(ReadOnlySpan<char> tail, out ModelVersion version)
    {
        version = default;

        // tail starts at the separator that follows the family token.
        var rest = tail[1..];
        var majorLength = DigitRunLength(rest);
        if (majorLength == 0)
            return false;

        var major = int.Parse(rest[..majorLength], System.Globalization.CultureInfo.InvariantCulture);
        rest = rest[majorLength..];

        var minor = 0;
        if (rest.Length > 1 && (rest[0] == '-' || rest[0] == '.'))
        {
            var minorRun = DigitRunLength(rest[1..]);
            // A 3+ digit run here is a release date stamp (claude-opus-4-5-20250929), not a minor.
            if (minorRun > 0 && minorRun <= MaxMinorDigits)
                minor = int.Parse(rest.Slice(1, minorRun), System.Globalization.CultureInfo.InvariantCulture);
        }

        version = new ModelVersion(major, minor);
        return true;
    }

    private static int DigitRunLength(ReadOnlySpan<char> span)
    {
        var length = 0;
        while (length < span.Length && char.IsAsciiDigit(span[length]))
            length++;

        // Guard against an absurdly long run overflowing int.Parse.
        return length > 9 ? 9 : length;
    }
}
