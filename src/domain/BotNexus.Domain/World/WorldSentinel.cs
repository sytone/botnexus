using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Domain.World;

/// <summary>
/// The world identity a BotNexus home declares on disk, and the rules for comparing it against the
/// identity a process is running as (#2836).
/// </summary>
/// <remarks>
/// <para><b>Why this is a pure type in Domain.</b> Three consumers need the same answer to "does
/// this directory belong to my world": <c>BotNexusHome</c> when it resolves a root, the file-backed
/// session store when it opens a store directory, and <c>doctor</c> when it probes a filesystem
/// location. Each does its own IO - one through <c>IFileSystem</c>, one through the CLI's direct
/// <c>System.IO</c> calls - so the shared piece is deliberately the <i>decision</i>, not the read.
/// A second spelling of the comparison is exactly the "one value, two derivations" failure the
/// sibling SQLite guard (#2833) was written to avoid: two independent implementations drift, agree
/// with each other while both being wrong, and the guard passes over corrupt data.</para>
/// <para><b>Why the file mirrors the SQLite stamp.</b> The keys are the same three the
/// <c>store_meta</c> table carries - <c>world_id</c>, <c>created_at</c>, <c>created_by_version</c> -
/// so a file-backed home and the SQLite stores inside it cannot describe their identity in two
/// different vocabularies. An operator reading either one sees the same fields.</para>
/// </remarks>
public static class WorldSentinel
{
    /// <summary>The sentinel file name, written at the root of a BotNexus home.</summary>
    public const string FileName = "world.json";

    /// <summary>The property carrying the world identity.</summary>
    public const string WorldIdKey = "world_id";

    /// <summary>The property carrying the stamping timestamp, for forensics.</summary>
    public const string CreatedAtKey = "created_at";

    /// <summary>The property carrying the stamping assembly version, for forensics.</summary>
    public const string CreatedByVersionKey = "created_by_version";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Parses sentinel contents, returning <see langword="null"/> when the text is missing, empty,
    /// not JSON, not an object, or carries no usable <c>world_id</c>.
    /// </summary>
    /// <remarks>
    /// Unreadable is deliberately <b>not</b> the same as mismatched. A malformed sentinel presents no
    /// competing identity, so refusing on it would turn a corrupted byte into an unrecoverable
    /// startup failure; it is treated as an adoption instead, exactly as the SQLite guard treats a
    /// <c>store_meta</c> table with no <c>world_id</c> row.
    /// </remarks>
    public static WorldSentinelDocument? Parse(string? contents)
    {
        if (string.IsNullOrWhiteSpace(contents))
            return null;

        try
        {
            using var document = JsonDocument.Parse(contents);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var worldId = ReadString(document.RootElement, WorldIdKey);
            if (string.IsNullOrWhiteSpace(worldId))
                return null;

            return new WorldSentinelDocument(
                worldId!,
                ReadString(document.RootElement, CreatedAtKey),
                ReadString(document.RootElement, CreatedByVersionKey));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Renders the sentinel payload for a freshly stamped home.</summary>
    public static string Serialize(string worldId, string createdByVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldId);

        return JsonSerializer.Serialize(
            new WorldSentinelDocument(
                worldId,
                DateTimeOffset.UtcNow.ToString("O"),
                string.IsNullOrWhiteSpace(createdByVersion) ? "unknown" : createdByVersion),
            SerializerOptions);
    }

    /// <summary>
    /// Classifies an on-disk sentinel against the running world. The single place the comparison is
    /// spelled - removing it is what makes the mismatch clauses fail by name (AC6).
    /// </summary>
    /// <param name="expectedWorldId">The world this process is running as.</param>
    /// <param name="sentinel">The parsed sentinel, or <see langword="null"/> when absent/unreadable.</param>
    /// <param name="homeIsPopulated">
    /// Whether the directory already holds state. Only meaningful when there is no sentinel: it is
    /// the difference between adopting existing data (which the operator must be told about) and
    /// stamping an empty directory (which is unremarkable).
    /// </param>
    public static WorldSentinelVerdict Classify(
        string expectedWorldId,
        WorldSentinelDocument? sentinel,
        bool homeIsPopulated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedWorldId);

        if (sentinel is null || string.IsNullOrWhiteSpace(sentinel.WorldId))
            return homeIsPopulated ? WorldSentinelVerdict.Adopt : WorldSentinelVerdict.Stamp;

        return string.Equals(sentinel.WorldId, expectedWorldId, StringComparison.OrdinalIgnoreCase)
            ? WorldSentinelVerdict.Match
            : WorldSentinelVerdict.Mismatch;
    }

    /// <summary>
    /// The single wording of a home-identity refusal, so the gateway, the session store and
    /// <c>doctor</c> all report the same failure in the same terms.
    /// </summary>
    public static string DescribeMismatch(string expectedWorldId, string actualWorldId, string homePath)
        => $"BotNexus home '{homePath}' belongs to world '{actualWorldId}' but this process is running as world " +
           $"'{expectedWorldId}'. Refusing to use it: continuing would read and write another world's sessions, " +
           "memory and agent workspaces. This usually means a home path resolved to a fallback location instead " +
           "of the configured home.";

    private static string? ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}

/// <summary>The parsed contents of a home's <c>world.json</c> sentinel.</summary>
/// <param name="WorldId">The world the home declares it belongs to.</param>
/// <param name="CreatedAt">When the home was stamped, round-trip formatted. Forensics only.</param>
/// <param name="CreatedByVersion">The assembly version that stamped it. Forensics only.</param>
public sealed record WorldSentinelDocument(
    [property: JsonPropertyName(WorldSentinel.WorldIdKey)] string WorldId,
    [property: JsonPropertyName(WorldSentinel.CreatedAtKey)] string? CreatedAt,
    [property: JsonPropertyName(WorldSentinel.CreatedByVersionKey)] string? CreatedByVersion);

/// <summary>
/// A BotNexus home root whose world sentinel has already been verified (#2836).
/// </summary>
/// <remarks>
/// <para>This exists so a file-backed store can <b>consume</b> a verified root instead of resolving
/// one. AC4 of #2836 requires that no file-backed store constructs a home path independently: a store
/// that built its own <c>~/.botnexus</c> would reproduce the #2819 fallback in a second place, and
/// would do so with no sentinel check because it never went through the resolver.</para>
/// <para>It is an interface in Domain rather than a reference to <c>BotNexusHome</c> because
/// <c>BotNexus.Gateway.Sessions</c> sits below <c>BotNexus.Gateway.Configuration</c> in the dependency
/// graph and must stay there.</para>
/// </remarks>
public interface IVerifiedHome
{
    /// <summary>The verified home root every file-backed path must live under.</summary>
    string RootPath { get; }

    /// <summary>The world the home was verified against, or <see langword="null"/> when the guard is inert.</summary>
    string? WorldId { get; }
}

/// <summary>What a process should do with a home, given its sentinel.</summary>
public enum WorldSentinelVerdict
{
    /// <summary>Empty directory: stamp it, say nothing.</summary>
    Stamp,

    /// <summary>Populated but unstamped: stamp it and warn once, because it may be the wrong home.</summary>
    Adopt,

    /// <summary>The sentinel names this world. Proceed and leave the stamp untouched.</summary>
    Match,

    /// <summary>The sentinel names another world. Always fatal - never auto-recover.</summary>
    Mismatch
}

/// <summary>
/// Raised when a BotNexus home on disk declares a different world than the process resolving it.
/// Always fatal: "recovering" from an identity mismatch means writing into another world's live
/// state, which is the incident (#2819) this guard exists to prevent.
/// </summary>
public sealed class HomeWorldIdentityMismatchException : InvalidOperationException
{
    /// <summary>Creates a home-identity refusal.</summary>
    public HomeWorldIdentityMismatchException(string expectedWorldId, string actualWorldId, string homePath)
        : base(WorldSentinel.DescribeMismatch(expectedWorldId, actualWorldId, homePath))
    {
        ExpectedWorldId = expectedWorldId;
        ActualWorldId = actualWorldId;
        HomePath = homePath;
    }

    /// <summary>The world the running process belongs to.</summary>
    public string ExpectedWorldId { get; }

    /// <summary>The world stamped into the home on disk.</summary>
    public string ActualWorldId { get; }

    /// <summary>The home directory that was refused.</summary>
    public string HomePath { get; }
}

/// <summary>
/// Raised when a file-backed store is handed a path that does not live under its verified home
/// (#2836, AC4).
/// </summary>
/// <remarks>
/// A store path outside the verified home is, by construction, a path the sentinel never vouched for.
/// Silently accepting it is how the guard acquires a hole precisely where path resolution is already
/// known to be inconsistent - the case #2819 actually hit.
/// </remarks>
public sealed class HomeScopeViolationException : InvalidOperationException
{
    /// <summary>Creates a scope refusal.</summary>
    public HomeScopeViolationException(string storePath, string homePath)
        : base($"Store path '{storePath}' is not inside the verified BotNexus home '{homePath}'. Refusing to use it: " +
               "a path resolved outside the verified home has never been checked against this world's sentinel, so " +
               "writing to it may corrupt another world's state.")
    {
        StorePath = storePath;
        HomePath = homePath;
    }

    /// <summary>The store path that was refused.</summary>
    public string StorePath { get; }

    /// <summary>The verified home it was expected to live under.</summary>
    public string HomePath { get; }
}

/// <summary>The single containment rule shared by every file-backed store (#2836, AC4).</summary>
public static class HomeScope
{
    /// <summary>
    /// Throws unless <paramref name="storePath"/> resolves inside <paramref name="home"/>. A null home
    /// means the guard is not configured and the check is inert, matching the rest of the design.
    /// </summary>
    public static void EnsureWithin(IVerifiedHome? home, string storePath)
    {
        if (home is null || string.IsNullOrWhiteSpace(storePath))
            return;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home.RootPath));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storePath));

        var isWithin = candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (!isWithin)
            throw new HomeScopeViolationException(candidate, root);
    }
}
