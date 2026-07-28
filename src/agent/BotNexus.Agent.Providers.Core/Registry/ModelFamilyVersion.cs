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
/// Handles every id shape the providers actually see, in BOTH orderings:
/// family-first (<c>claude-opus-4.6</c>, <c>claude-opus-4-5-20250929</c>, <c>opus-4-6</c>,
/// <c>copilot/claude-opus-5</c>) and version-first (<c>claude-4.7-opus</c>, the spelling used by
/// SAP AI Core and several broker gateways). The identical model must classify identically no
/// matter which broker served it, so both orderings resolve to the same
/// <see cref="ModelVersion"/>.
/// </para>
/// <para>
/// A numeric component of three or more digits is a release DATE stamp, never a version component:
/// without that cap <c>claude-opus-4-20250514</c> parses as major 4 minor 20250514 (or, if the
/// minor were merely truncated, the equally wrong 4.20) and would leapfrog every version floor in
/// the codebase. The cap applies to both orderings.
/// </para>
/// </summary>
public static class ModelFamilyVersion
{
    // A version component of 3+ digits is a release date stamp (e.g. the 20250514 in
    // claude-opus-4-20250514), never a version component. Real version components are 1-2 digits.
    private const int MaxComponentDigits = 2;

    /// <summary>
    /// Attempts to read the version attached to <paramref name="family"/> in
    /// <paramref name="modelId"/>, accepting the version either AFTER the family token
    /// (<c>claude-opus-4.7</c>) or BEFORE it (<c>claude-4.7-opus</c>). The family token must sit on
    /// a token boundary so <c>opus</c> does not match inside an unrelated word such as
    /// <c>octopus5</c>.
    /// </summary>
    /// <param name="modelId">The provider model id, for example <c>claude-opus-4.6</c>.</param>
    /// <param name="family">The family token to locate, for example <c>opus</c> or <c>gpt</c>.</param>
    /// <param name="version">The parsed version when this returns true; otherwise the default.</param>
    /// <returns>True when the family token is present on a token boundary and carries a numeric version.</returns>
    public static bool TryParse(string? modelId, string family, out ModelVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        version = default;
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var span = modelId.AsSpan();
        var searchFrom = 0;

        // Scan every occurrence: an id may legitimately mention the token more than once and only
        // one of them carries a version.
        while (searchFrom < span.Length)
        {
            var index = span[searchFrom..].IndexOf(family, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var start = searchFrom + index;
            var end = start + family.Length;

            if (HasLeadingBoundary(span, start))
            {
                // Family-first: claude-opus-4.7
                if (IsFollowedBySeparator(span, end) && TryParseVersionAfter(span[end..], out version))
                    return true;

                // Version-first: claude-4.7-opus (SAP AI Core and friends).
                if (HasTrailingBoundary(span, end) && TryParseVersionBefore(span[..start], out version))
                    return true;
            }

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

    /// <summary>
    /// True when <paramref name="modelId"/> mentions <paramref name="family"/> as a whole token,
    /// regardless of whether a version is attached. This is the recognition half of the
    /// fail-open-to-modern rule in <see cref="ModelCapabilityHeuristics"/> (issue #2374): an id we
    /// can identify as Claude but cannot version must still be treated as a current generation.
    /// Because it is used to WIDEN behaviour, the boundary test is strict on both sides --
    /// <c>octopus5</c> is not an <c>opus</c> and <c>clauded-out</c> is not a <c>claude</c>.
    /// </summary>
    /// <param name="modelId">The provider model id.</param>
    /// <param name="family">The family token to look for.</param>
    /// <returns>True when the family token appears on a token boundary.</returns>
    public static bool ContainsFamilyToken(string? modelId, string family)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var span = modelId.AsSpan();
        var searchFrom = 0;

        while (searchFrom < span.Length)
        {
            var index = span[searchFrom..].IndexOf(family, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var start = searchFrom + index;
            var end = start + family.Length;

            if (HasLeadingBoundary(span, start) && HasTrailingBoundary(span, end))
                return true;

            searchFrom = start + 1;
        }

        return false;
    }

    // The family token must not be glued to a preceding letter/digit: "opus" in "claude-opus-5" is
    // a real token, but "opus" inside "octopus5" is not.
    private static bool HasLeadingBoundary(ReadOnlySpan<char> span, int start) =>
        start == 0 || !char.IsLetterOrDigit(span[start - 1]);

    private static bool HasTrailingBoundary(ReadOnlySpan<char> span, int end) =>
        end >= span.Length || !char.IsLetterOrDigit(span[end]);

    private static bool IsFollowedBySeparator(ReadOnlySpan<char> span, int end) =>
        end < span.Length && IsSeparator(span[end]);

    private static bool IsSeparator(char value) => value is '-' or '.' or '_';

    // Family-first: the tail begins at the separator that follows the family token.
    private static bool TryParseVersionAfter(ReadOnlySpan<char> tail, out ModelVersion version)
    {
        version = default;

        var rest = tail[1..];
        var majorLength = DigitRunLength(rest);
        // A 3+ digit leading run is a bare date stamp (claude-3-5-haiku-20241022 read from
        // "haiku"), not a major version.
        if (majorLength is 0 || majorLength > MaxComponentDigits)
            return false;

        var major = ParseRun(rest[..majorLength]);
        rest = rest[majorLength..];

        var minor = 0;
        if (rest.Length > 1 && (rest[0] == '-' || rest[0] == '.'))
        {
            var minorRun = DigitRunLength(rest[1..]);
            // A 3+ digit run here is a release date stamp (claude-opus-4-20250514), not a minor.
            if (minorRun > 0 && minorRun <= MaxComponentDigits)
                minor = ParseRun(rest.Slice(1, minorRun));
        }

        version = new ModelVersion(major, minor);
        return true;
    }

    // Version-first: the head is everything before the family token, and its final numeric
    // component(s) are the version -- "claude-4.7-" in "claude-4.7-opus".
    private static bool TryParseVersionBefore(ReadOnlySpan<char> head, out ModelVersion version)
    {
        version = default;

        // Must be separated from the family token by a real separator.
        if (head.Length < 2 || !IsSeparator(head[^1]))
            return false;

        var body = head[..^1];
        if (!TryTakeTrailingRun(body, out var lastRun, out var beforeLast))
            return false;

        // Optionally a second component in front of it: "4" then "7" in claude-4-7-opus.
        if (beforeLast.Length > 1 && (beforeLast[^1] == '-' || beforeLast[^1] == '.') &&
            TryTakeTrailingRun(beforeLast[..^1], out var firstRun, out _))
        {
            version = new ModelVersion(firstRun, lastRun);
            return true;
        }

        version = new ModelVersion(lastRun, 0);
        return true;
    }

    // Reads the digit run that ENDS at the end of <paramref name="body"/>, rejecting a 3+ digit run
    // (a date stamp) so the date-stamp guard covers the version-first ordering too.
    private static bool TryTakeTrailingRun(ReadOnlySpan<char> body, out int value, out ReadOnlySpan<char> remainder)
    {
        value = 0;
        remainder = body;

        var length = 0;
        while (length < body.Length && char.IsAsciiDigit(body[^(length + 1)]))
            length++;

        if (length is 0 || length > MaxComponentDigits)
            return false;

        value = ParseRun(body[^length..]);
        remainder = body[..^length];
        return true;
    }

    private static int ParseRun(ReadOnlySpan<char> run) =>
        int.Parse(run, System.Globalization.CultureInfo.InvariantCulture);

    private static int DigitRunLength(ReadOnlySpan<char> span)
    {
        var length = 0;
        while (length < span.Length && char.IsAsciiDigit(span[length]))
            length++;

        // Guard against an absurdly long run overflowing int.Parse.
        return length > 9 ? 9 : length;
    }
}
