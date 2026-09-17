using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Contracts.Updates;

/// <summary>Identifies how a source installation or update chooses its target.</summary>
public enum ReleaseTargetKind
{
    Stable,
    Latest,
    Exact,
}

/// <summary>A caller's release selection, independent of any Git transport.</summary>
public sealed record ReleaseTargetRequest
{
    private ReleaseTargetRequest(ReleaseTargetKind kind, string? version)
    {
        Kind = kind;
        Version = version;
    }

    public ReleaseTargetKind Kind { get; }

    public string? Version { get; }

    public static ReleaseTargetRequest Stable { get; } = new(ReleaseTargetKind.Stable, null);

    public static ReleaseTargetRequest Latest { get; } = new(ReleaseTargetKind.Latest, null);

    public static ReleaseTargetRequest Exact(string version) => new(ReleaseTargetKind.Exact, version);
}

/// <summary>A release tag and the immutable commit identity to which it was resolved.</summary>
public sealed record ReleaseTagReference(string TagName, string CommitSha);

/// <summary>The configured development branch and its resolved tip.</summary>
public sealed record DevelopmentTipReference(string SourceName, string CommitSha);

/// <summary>A fully resolved target safe for later check or checkout operations.</summary>
public sealed record ResolvedReleaseTarget(
    ReleaseTargetKind Kind,
    string? Version,
    string? TagName,
    string SourceName,
    string CommitSha);

/// <summary>Version and commit identity of an installed BotNexus deployment.</summary>
public sealed record ReleaseIdentity(string? Version, string CommitSha);

/// <summary>Shared installed-versus-target status used by update callers.</summary>
public sealed record ReleaseUpdateStatus(ReleaseIdentity Installed, ResolvedReleaseTarget Target);

/// <summary>Raised when a requested release target cannot be resolved without mutation.</summary>
public sealed class ReleaseTargetResolutionException : InvalidOperationException
{
    public ReleaseTargetResolutionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Resolves stable, latest, and exact targets from metadata fetched by a caller. The resolver is
/// deliberately pure: callers can resolve and validate a commit before stopping a gateway or
/// changing a checkout.
/// </summary>
public static partial class ReleaseTargetResolver
{
    public static ResolvedReleaseTarget Resolve(
        ReleaseTargetRequest request,
        IEnumerable<ReleaseTagReference> releaseTags,
        DevelopmentTipReference? developmentTip = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(releaseTags);

        return request.Kind switch
        {
            ReleaseTargetKind.Stable => ResolveStable(releaseTags),
            ReleaseTargetKind.Latest => ResolveLatest(developmentTip),
            ReleaseTargetKind.Exact => ResolveExact(request.Version, releaseTags),
            _ => throw new ReleaseTargetResolutionException($"Unsupported release target kind '{request.Kind}'."),
        };
    }

    private static ResolvedReleaseTarget ResolveStable(IEnumerable<ReleaseTagReference> releaseTags)
    {
        var candidate = ParseResolvableTags(releaseTags)
            .Where(item => item.Version.PreReleaseIdentifiers.Count == 0)
            .OrderByDescending(item => item.Version, SemanticVersionComparer.Instance)
            .ThenByDescending(item => item.Reference.TagName, StringComparer.Ordinal)
            .FirstOrDefault();

        if (candidate is null)
            throw new ReleaseTargetResolutionException("No resolvable stable release tag was found.");

        return ToResolvedTarget(ReleaseTargetKind.Stable, candidate);
    }

    private static ResolvedReleaseTarget ResolveExact(
        string? requestedVersion,
        IEnumerable<ReleaseTagReference> releaseTags)
    {
        if (!SemanticVersion.TryParse(requestedVersion, out var version))
            throw new ReleaseTargetResolutionException("The exact release must be a valid semantic version without a leading 'v'.");

        var expectedTag = $"v{requestedVersion}";
        var candidate = ParseResolvableTags(releaseTags)
            .FirstOrDefault(item => string.Equals(item.Reference.TagName, expectedTag, StringComparison.Ordinal));

        if (candidate is null)
            throw new ReleaseTargetResolutionException($"Release tag '{expectedTag}' was not found or did not resolve to a commit.");

        return new ResolvedReleaseTarget(
            ReleaseTargetKind.Exact,
            version!.Normalized,
            candidate.Reference.TagName,
            candidate.Reference.TagName,
            candidate.Reference.CommitSha);
    }

    private static ResolvedReleaseTarget ResolveLatest(DevelopmentTipReference? developmentTip)
    {
        if (developmentTip is null
            || string.IsNullOrWhiteSpace(developmentTip.SourceName)
            || string.IsNullOrWhiteSpace(developmentTip.CommitSha))
        {
            throw new ReleaseTargetResolutionException("The configured development tip did not resolve to a commit.");
        }

        return new ResolvedReleaseTarget(
            ReleaseTargetKind.Latest,
            Version: null,
            TagName: null,
            developmentTip.SourceName,
            developmentTip.CommitSha);
    }

    private static ResolvedReleaseTarget ToResolvedTarget(ReleaseTargetKind kind, ParsedReleaseTag candidate) =>
        new(
            kind,
            candidate.Version.Normalized,
            candidate.Reference.TagName,
            candidate.Reference.TagName,
            candidate.Reference.CommitSha);

    private static IEnumerable<ParsedReleaseTag> ParseResolvableTags(IEnumerable<ReleaseTagReference> releaseTags)
    {
        foreach (var reference in releaseTags)
        {
            if (reference is null
                || string.IsNullOrWhiteSpace(reference.CommitSha)
                || reference.TagName is null
                || !reference.TagName.StartsWith('v')
                || !SemanticVersion.TryParse(reference.TagName[1..], out var version))
            {
                continue;
            }

            yield return new ParsedReleaseTag(reference, version!);
        }
    }

    private sealed record ParsedReleaseTag(ReleaseTagReference Reference, SemanticVersion Version);

    private sealed class SemanticVersion
    {
        private SemanticVersion(
            string normalized,
            string major,
            string minor,
            string patch,
            IReadOnlyList<string> preReleaseIdentifiers)
        {
            Normalized = normalized;
            Major = major;
            Minor = minor;
            Patch = patch;
            PreReleaseIdentifiers = preReleaseIdentifiers;
        }

        public string Normalized { get; }

        public string Major { get; }

        public string Minor { get; }

        public string Patch { get; }

        public IReadOnlyList<string> PreReleaseIdentifiers { get; }

        public static bool TryParse(string? value, out SemanticVersion? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var match = SemanticVersionPattern().Match(value);
            if (!match.Success)
                return false;

            var prerelease = match.Groups["prerelease"].Success
                ? match.Groups["prerelease"].Value.Split('.')
                : Array.Empty<string>();

            version = new SemanticVersion(
                value,
                match.Groups["major"].Value,
                match.Groups["minor"].Value,
                match.Groups["patch"].Value,
                prerelease);
            return true;
        }
    }

    private sealed class SemanticVersionComparer : IComparer<SemanticVersion>
    {
        public static SemanticVersionComparer Instance { get; } = new();

        public int Compare(SemanticVersion? left, SemanticVersion? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;

            var result = CompareNumeric(left.Major, right.Major);
            if (result != 0)
                return result;
            result = CompareNumeric(left.Minor, right.Minor);
            if (result != 0)
                return result;
            result = CompareNumeric(left.Patch, right.Patch);
            if (result != 0)
                return result;

            return ComparePreRelease(left.PreReleaseIdentifiers, right.PreReleaseIdentifiers);
        }

        private static int ComparePreRelease(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left.Count == 0)
                return right.Count == 0 ? 0 : 1;
            if (right.Count == 0)
                return -1;

            for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
            {
                var leftNumeric = IsNumeric(left[index]);
                var rightNumeric = IsNumeric(right[index]);
                int result;
                if (leftNumeric && rightNumeric)
                    result = CompareNumeric(left[index], right[index]);
                else if (leftNumeric)
                    result = -1;
                else if (rightNumeric)
                    result = 1;
                else
                    result = string.CompareOrdinal(left[index], right[index]);

                if (result != 0)
                    return result;
            }

            return left.Count.CompareTo(right.Count);
        }

        private static int CompareNumeric(string left, string right)
        {
            var lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
        }

        private static bool IsNumeric(string value) => value.All(char.IsAsciiDigit);
    }

    [GeneratedRegex(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();
}
