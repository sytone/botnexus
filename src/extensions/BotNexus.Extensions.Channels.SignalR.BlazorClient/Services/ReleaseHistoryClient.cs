using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

public sealed class ReleaseHistoryClient(HttpClient http)
{
    public const string ArtifactUrl = "api/release-history";
    private const string SchemaVersion = "1.0.0";
    private static readonly Regex CommitPattern = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);

    public async Task<ReleaseHistoryResponse> GetAsync(bool refreshRemote = false, CancellationToken cancellationToken = default)
    {
        var url = refreshRemote ? ArtifactUrl + "?refresh=true" : ArtifactUrl;
        var response = await http.GetFromJsonAsync<ReleaseHistoryResponse>(url, cancellationToken)
            ?? throw new InvalidDataException("The release history response was empty.");
        Validate(response.Manifest);
        return response;
    }

    internal static void Validate(ReleaseHistoryManifest manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The release history format is not supported.");

        SemanticVersion? previous = null;
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in manifest.Releases)
        {
            var version = SemanticVersion.Parse(release.Version);
            if (!versions.Add(release.Version))
                throw new InvalidDataException("The release history contains a duplicate version.");
            if (previous is not null && previous.CompareTo(version) < 0)
                throw new InvalidDataException("The release history is not ordered newest first.");
            previous = version;

            if (!string.Equals(release.Tag, $"v{release.Version}", StringComparison.Ordinal) ||
                !CommitPattern.IsMatch(release.Commit) ||
                !DateOnly.TryParseExact(release.ReleasedAt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                !IsPublicUri(release.ReleaseUrl) ||
                !IsPublicUri(release.DocumentationUrl))
            {
                throw new InvalidDataException("The release history contains invalid release metadata.");
            }

            foreach (var category in release.Categories)
            {
                if (string.IsNullOrWhiteSpace(category.Name))
                    throw new InvalidDataException("The release history contains an unnamed category.");
                foreach (var change in category.Changes)
                {
                    if (string.IsNullOrWhiteSpace(change.Summary) || change.DocumentationUrls.Any(url => !IsPublicUri(url)))
                        throw new InvalidDataException("The release history contains invalid change metadata.");
                }
            }
        }
    }

    private static bool IsPublicUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return (uri.Host.Equals("sytone.github.io", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/botnexus/", StringComparison.OrdinalIgnoreCase)) ||
               (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/Sytone/botnexus/", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SemanticVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemanticVersion>
    {
        private static readonly Regex Pattern = new("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-([0-9A-Za-z.-]+))?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

        public static SemanticVersion Parse(string value)
        {
            var match = Pattern.Match(value);
            if (!match.Success)
                throw new InvalidDataException("The release history contains an invalid semantic version.");
            return new(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                match.Groups[4].Success ? match.Groups[4].Value : null);
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;
            var core = Major.CompareTo(other.Major);
            if (core == 0) core = Minor.CompareTo(other.Minor);
            if (core == 0) core = Patch.CompareTo(other.Patch);
            if (core != 0) return core;
            if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
            if (other.PreRelease is null) return -1;

            var left = PreRelease.Split('.');
            var right = other.PreRelease.Split('.');
            for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
            {
                if (index >= left.Length) return -1;
                if (index >= right.Length) return 1;
                var leftNumeric = int.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
                var rightNumeric = int.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
                var identifier = leftNumeric && rightNumeric
                    ? leftNumber.CompareTo(rightNumber)
                    : leftNumeric
                        ? -1
                        : rightNumeric
                            ? 1
                            : string.CompareOrdinal(left[index], right[index]);
                if (identifier != 0) return identifier;
            }

            return 0;
        }
    }
}

public sealed record ReleaseHistoryManifest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("releases")] IReadOnlyList<ReleaseHistoryEntry> Releases);

public sealed record ReleaseHistoryEntry(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("releasedAt")] string ReleasedAt,
    [property: JsonPropertyName("releaseUrl")] string ReleaseUrl,
    [property: JsonPropertyName("documentationUrl")] string DocumentationUrl,
    [property: JsonPropertyName("categories")] IReadOnlyList<ReleaseChangeCategory> Categories);

public sealed record ReleaseChangeCategory(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("changes")] IReadOnlyList<ReleaseChange> Changes);

public sealed record ReleaseChange(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("documentationUrls")] IReadOnlyList<string> DocumentationUrls);

public sealed record ReleaseHistoryResponse(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("releases")] IReadOnlyList<ReleaseHistoryEntry> Releases,
    [property: JsonPropertyName("sourceStatus")] SourceVersionStatus SourceStatus)
{
    public ReleaseHistoryManifest Manifest => new(SchemaVersion, Releases);
}

public sealed record SourceVersionStatus(
    [property: JsonPropertyName("runningCommit")] string RunningCommit,
    [property: JsonPropertyName("runningCommitShort")] string RunningCommitShort,
    [property: JsonPropertyName("checkoutHead")] string CheckoutHead,
    [property: JsonPropertyName("checkoutHeadShort")] string CheckoutHeadShort,
    [property: JsonPropertyName("latestReleaseVersion")] string? LatestReleaseVersion,
    [property: JsonPropertyName("latestReleaseCommit")] string? LatestReleaseCommit,
    [property: JsonPropertyName("releaseDistance")] GitDistance? ReleaseDistance,
    [property: JsonPropertyName("remoteName")] string RemoteName,
    [property: JsonPropertyName("remoteBranch")] string RemoteBranch,
    [property: JsonPropertyName("remoteHead")] string? RemoteHead,
    [property: JsonPropertyName("remoteDistance")] GitDistance? RemoteDistance,
    [property: JsonPropertyName("remoteRefreshError")] string? RemoteRefreshError);

public sealed record GitDistance(
    [property: JsonPropertyName("targetCommit")] string TargetCommit,
    [property: JsonPropertyName("targetCommitShort")] string TargetCommitShort,
    [property: JsonPropertyName("ahead")] int Ahead,
    [property: JsonPropertyName("behind")] int Behind);
