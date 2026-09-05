using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Plugins.Security;

/// <summary>
/// Trust verification posture applied to materialised content.
/// </summary>
/// <remarks>
/// The member names are identical to <c>BotNexus.Extensions.Skills.Security.SkillTrustMode</c> and
/// <c>BotNexus.Extensions.Mcp.Plugins.PluginTrustMode</c> by design, and
/// <c>PluginMcpRegistrationFenceArchitectureTests</c> fails the build if the three vocabularies
/// drift. A mode that is reportable in one vocabulary and absent from another is a posture an
/// operator can select and the platform cannot enforce (#2682).
/// </remarks>
public enum ContentTrustMode
{
    /// <summary>No verification - all content is allowed.</summary>
    Disabled,

    /// <summary>Verification failures are logged, but the content is still permitted.</summary>
    Warn,

    /// <summary>Verification failures are refused.</summary>
    Enforce,
}

/// <summary>
/// A trust catalog entry: the expected SHA-256 hash of one file, keyed by its forward-slash path
/// relative to the catalog's own directory.
/// </summary>
public sealed record TrustCatalogEntry
{
    /// <summary>Forward-slash path relative to the catalogued directory.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>Lowercase hex SHA-256 of the file's bytes.</summary>
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    /// <summary>When this entry was generated.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The on-disk trust catalog: a manifest of expected file hashes, stored as <c>trust.json</c> in
/// the catalogued directory.
/// </summary>
public sealed record TrustCatalog
{
    /// <summary>Catalog format version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>When the catalog was generated.</summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Every catalogued file.</summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<TrustCatalogEntry> Entries { get; init; } = [];
}

/// <summary>
/// Result of verifying a directory against its trust catalog.
/// </summary>
/// <param name="Trusted">Whether every catalogued file matched and no unlisted file was found.</param>
/// <param name="Violations">Every reason verification failed; empty when trusted.</param>
public sealed record TrustVerificationResult(
    bool Trusted,
    IReadOnlyList<string> Violations);

/// <summary>
/// The single content-hash trust implementation in the platform: SHA-256 catalog generation,
/// serialisation and verification over a directory of materialised files.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the Plugins extension (#2682).</b> Three consumers need one hasher: skill
/// discovery, plugin install, and plugin MCP registration. <c>BotNexus.Extensions.Skills</c>
/// ALREADY references <c>BotNexus.Extensions.Plugins</c> (#2684, plugin skill discovery), so the
/// dependency edge Skills -> Plugins exists and is documented. Referencing in the other direction -
/// Plugins -> Skills - would close that edge into a cycle and simply does not compile. Lifting the
/// implementation to the end of the existing edge therefore reuses the machinery with ZERO new
/// project references, which is what #3347 asked for; the alternative of duplicating a second
/// hasher for plugins is exactly the drift #2682 was filed to prevent.
/// </para>
/// <para>
/// <b>Two knobs, because the two consumers genuinely differ.</b> Skills catalogue only
/// <i>scannable</i> script files and tolerate other content beside them, so they pass a predicate
/// and leave unlisted-file detection off. A plugin's catalog must cover EVERY file install
/// materialised, so plugin verification turns unlisted-file detection ON - a file dropped into an
/// installed plugin after the fact is a modification of that plugin, and silently ignoring it
/// would make the catalog a statement about a subset nobody can name.
/// </para>
/// </remarks>
public static class ContentTrustCatalog
{
    /// <summary>File name of the catalog inside the directory it describes.</summary>
    public const string CatalogFileName = "trust.json";

    /// <summary>
    /// JSON policy for the catalog. Case-insensitive so a hand-edited camelCase catalog binds, and
    /// indented because a human debugging a refused install reads this file.
    /// </summary>
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Includes every file. The plugin policy: a catalog covers all materialised content.</summary>
    public static readonly Func<string, bool> IncludeEveryFile = static _ => true;

    /// <summary>Includes every directory. The plugin policy - see <see cref="IncludeEveryFile"/>.</summary>
    /// <remarks>
    /// Plugins deliberately descend into dot-directories. A plugin's own manifest lives in
    /// <c>.botnexus-plugin/plugin.json</c>, so a hardwired dot-skip would leave the single most
    /// security-relevant file in the tree uncatalogued and freely editable post-install. The
    /// transport's <c>.git</c> metadata is not a counter-example: <c>Promote</c> already refuses to
    /// copy it, so it never exists in a materialised plugin directory to begin with.
    /// </remarks>
    public static readonly Func<string, bool> IncludeEveryDirectory = static _ => true;

    /// <summary>
    /// Skips dot-directories and <c>node_modules</c>. The skills policy: the first is editor or
    /// transport metadata rather than skill content, and the second is a dependency tree whose size
    /// makes hashing it a denial of service rather than a security control.
    /// </summary>
    public static readonly Func<string, bool> SkipHiddenAndVendorDirectories = static name =>
        !name.StartsWith('.') && !string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Verifies <paramref name="directory"/> against the <c>trust.json</c> it contains.
    /// </summary>
    /// <param name="directory">Absolute directory holding both the content and its catalog.</param>
    /// <param name="fileSystem">File system abstraction; defaults to the real filesystem.</param>
    /// <param name="includeFile">
    /// Predicate selecting which files the catalog is expected to cover. Only consulted when
    /// <paramref name="detectUnlistedFiles"/> is true.
    /// </param>
    /// <param name="detectUnlistedFiles">
    /// When true, a file present on disk and absent from the catalog is a violation. Off by default
    /// so the skills posture - catalogue scripts, tolerate neighbouring content - is unchanged.
    /// </param>
    /// <param name="includeDirectory">
    /// Predicate over directory NAMES deciding which subtrees are walked. Defaults to
    /// <see cref="IncludeEveryDirectory"/>. Must match whatever was passed to
    /// <see cref="GenerateCatalog"/>, or generation and verification disagree about the file set.
    /// </param>
    public static TrustVerificationResult Verify(
        string directory,
        IFileSystem? fileSystem = null,
        Func<string, bool>? includeFile = null,
        bool detectUnlistedFiles = false,
        Func<string, bool>? includeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fs = fileSystem ?? new FileSystem();
        var catalogPath = fs.Path.Combine(directory, CatalogFileName);

        if (!fs.File.Exists(catalogPath))
        {
            // No catalog means verification is not possible, not that content is fine. The caller's
            // trust mode decides what to do about it.
            return new TrustVerificationResult(false, ["No trust catalog found"]);
        }

        TrustCatalog? catalog;
        try
        {
            var json = fs.File.ReadAllText(catalogPath);
            catalog = JsonSerializer.Deserialize<TrustCatalog>(json, CatalogJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TrustVerificationResult(false, [$"Failed to parse trust catalog: {ex.Message}"]);
        }

        if (catalog is null || catalog.Entries.Count == 0)
        {
            return new TrustVerificationResult(false, ["Trust catalog is empty or invalid"]);
        }

        var violations = new List<string>();

        foreach (var entry in catalog.Entries)
        {
            var filePath = fs.Path.Combine(directory, entry.Path.Replace('/', fs.Path.DirectorySeparatorChar));

            if (!fs.File.Exists(filePath))
            {
                violations.Add($"Missing file: {entry.Path}");
                continue;
            }

            var actualHash = ComputeSha256(fs.File.ReadAllBytes(filePath));

            if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"Hash mismatch: {entry.Path} (expected {Prefix(entry.Sha256)}..., got {Prefix(actualHash)}...)");
            }
        }

        if (detectUnlistedFiles)
        {
            var catalogued = catalog.Entries
                .Select(e => e.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var relative in EnumerateRelativeFiles(directory, fs, includeFile ?? IncludeEveryFile, includeDirectory ?? IncludeEveryDirectory))
            {
                // The catalog cannot contain its own hash, so its own presence is never unlisted.
                if (string.Equals(relative, CatalogFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!catalogued.Contains(relative))
                {
                    violations.Add($"Unlisted file: {relative}");
                }
            }
        }

        return new TrustVerificationResult(violations.Count == 0, violations);
    }

    /// <summary>
    /// Generates a catalog over the files of <paramref name="directory"/> that
    /// <paramref name="includeFile"/> selects.
    /// </summary>
    /// <param name="directory">Absolute directory to catalogue.</param>
    /// <param name="fileSystem">File system abstraction; defaults to the real filesystem.</param>
    /// <param name="includeFile">
    /// Predicate over absolute file paths deciding what the catalog covers. Defaults to every file.
    /// </param>
    /// <param name="generatedAt">Generation timestamp; injectable so tests are deterministic.</param>
    /// <param name="includeDirectory">
    /// Predicate over directory NAMES deciding which subtrees are walked. Defaults to
    /// <see cref="IncludeEveryDirectory"/>.
    /// </param>
    public static TrustCatalog GenerateCatalog(
        string directory,
        IFileSystem? fileSystem = null,
        Func<string, bool>? includeFile = null,
        DateTimeOffset? generatedAt = null,
        Func<string, bool>? includeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fs = fileSystem ?? new FileSystem();
        var now = generatedAt ?? DateTimeOffset.UtcNow;

        if (!fs.Directory.Exists(directory))
        {
            return new TrustCatalog { GeneratedAt = now, Entries = [] };
        }

        var selector = includeFile ?? IncludeEveryFile;
        var entries = new List<TrustCatalogEntry>();

        foreach (var relative in EnumerateRelativeFiles(directory, fs, selector, includeDirectory ?? IncludeEveryDirectory))
        {
            // A catalog never hashes itself: doing so would be unverifiable by construction.
            if (string.Equals(relative, CatalogFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var absolute = fs.Path.Combine(directory, relative.Replace('/', fs.Path.DirectorySeparatorChar));
            entries.Add(new TrustCatalogEntry
            {
                Path = relative,
                Sha256 = ComputeSha256(fs.File.ReadAllBytes(absolute)),
                UpdatedAt = now,
            });
        }

        return new TrustCatalog { GeneratedAt = now, Entries = entries };
    }

    /// <summary>Writes <paramref name="catalog"/> into <paramref name="directory"/> as <c>trust.json</c>.</summary>
    /// <param name="directory">Directory the catalog describes.</param>
    /// <param name="catalog">Catalog to persist.</param>
    /// <param name="fileSystem">File system abstraction; defaults to the real filesystem.</param>
    public static void WriteCatalog(string directory, TrustCatalog catalog, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(catalog);

        var fs = fileSystem ?? new FileSystem();
        fs.Directory.CreateDirectory(directory);
        fs.File.WriteAllText(
            fs.Path.Combine(directory, CatalogFileName),
            JsonSerializer.Serialize(catalog, CatalogJsonOptions));
    }

    /// <summary>Lowercase hex SHA-256 of <paramref name="data"/>.</summary>
    /// <param name="data">Bytes to hash.</param>
    public static string ComputeSha256(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    /// <summary>
    /// Every selected file under <paramref name="directory"/> as sorted forward-slash relative
    /// paths, descending only into subtrees <paramref name="includeDirectory"/> admits.
    /// </summary>
    private static List<string> EnumerateRelativeFiles(
        string directory,
        IFileSystem fs,
        Func<string, bool> includeFile,
        Func<string, bool> includeDirectory)
    {
        var relatives = new List<string>();
        if (!fs.Directory.Exists(directory))
        {
            return relatives;
        }

        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<string> children;
            try
            {
                children = fs.Directory.EnumerateFileSystemEntries(current).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in children)
            {
                var name = fs.Path.GetFileName(entry);

                if (fs.Directory.Exists(entry))
                {
                    if (includeDirectory(name))
                    {
                        stack.Push(entry);
                    }
                }
                else if (fs.File.Exists(entry) && includeFile(entry))
                {
                    relatives.Add(fs.Path.GetRelativePath(directory, entry)
                        .Replace(fs.Path.DirectorySeparatorChar, '/')
                        .Replace('\\', '/'));
                }
            }
        }

        relatives.Sort(StringComparer.OrdinalIgnoreCase);
        return relatives;
    }

    /// <summary>
    /// First twelve characters of a hash for diagnostics. Slicing is safe here and NOWHERE else:
    /// the input is generated lowercase hex, never user text, so no surrogate pair exists to split
    /// (the #2924 rule).
    /// </summary>
    private static string Prefix(string hash) =>
        hash.Length >= 12 ? hash[..12] : hash;
}
