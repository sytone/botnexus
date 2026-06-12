using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Skills.Security;

/// <summary>
/// Trust verification mode for skill scripts.
/// </summary>
public enum SkillTrustMode
{
    /// <summary>No trust verification — all skills are allowed (legacy behavior).</summary>
    Disabled,

    /// <summary>Log a warning when trust verification fails, but allow execution.</summary>
    Warn,

    /// <summary>Block execution of skills that fail trust verification.</summary>
    Enforce,
}

/// <summary>
/// A trust catalog entry representing the expected SHA-256 hash of a skill file.
/// </summary>
public sealed record TrustCatalogEntry
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The trust catalog file format: a manifest of expected file hashes for a skill.
/// Stored as trust.json in the skill root directory.
/// </summary>
public sealed record TrustCatalog
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("entries")]
    public IReadOnlyList<TrustCatalogEntry> Entries { get; init; } = [];
}

/// <summary>
/// Result of verifying a skill against its trust catalog.
/// </summary>
public sealed record TrustVerificationResult(
    bool Trusted,
    IReadOnlyList<string> Violations);

/// <summary>
/// Verifies skill script integrity using SHA-256 hash catalogs.
/// Skills with a trust.json catalog are verified; skills without one are
/// treated according to the configured trust mode.
/// </summary>
public static class SkillTrustVerifier
{
    public const string CatalogFileName = "trust.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Verifies a skill directory against its trust catalog.
    /// Returns a result indicating whether the skill is trusted.
    /// </summary>
    /// <param name="skillDir">Absolute path to the skill directory.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <returns>Verification result with any violations found.</returns>
    public static TrustVerificationResult Verify(string skillDir, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var catalogPath = fs.Path.Combine(skillDir, CatalogFileName);

        if (!fs.File.Exists(catalogPath))
        {
            // No catalog = no verification possible — caller decides based on trust mode
            return new TrustVerificationResult(false, ["No trust catalog found"]);
        }

        TrustCatalog? catalog;
        try
        {
            var json = fs.File.ReadAllText(catalogPath);
            catalog = JsonSerializer.Deserialize<TrustCatalog>(json, JsonOptions);
        }
        catch (Exception ex)
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
            var filePath = fs.Path.Combine(skillDir, entry.Path.Replace('/', fs.Path.DirectorySeparatorChar));

            if (!fs.File.Exists(filePath))
            {
                violations.Add($"Missing file: {entry.Path}");
                continue;
            }

            var fileBytes = fs.File.ReadAllBytes(filePath);
            var actualHash = ComputeSha256(fileBytes);

            if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Hash mismatch: {entry.Path} (expected {entry.Sha256[..12]}..., got {actualHash[..12]}...)");
            }
        }

        return new TrustVerificationResult(violations.Count == 0, violations);
    }

    /// <summary>
    /// Generates a trust catalog for a skill directory by hashing all scannable files.
    /// </summary>
    /// <param name="skillDir">Absolute path to the skill directory.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <returns>A new trust catalog covering all script files in the skill.</returns>
    public static TrustCatalog GenerateCatalog(string skillDir, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var entries = new List<TrustCatalogEntry>();
        var now = DateTimeOffset.UtcNow;

        if (!fs.Directory.Exists(skillDir))
            return new TrustCatalog { GeneratedAt = now, Entries = entries };

        var files = CollectTrustableFiles(skillDir, fs);

        foreach (var file in files)
        {
            var relativePath = fs.Path.GetRelativePath(skillDir, file)
                .Replace(fs.Path.DirectorySeparatorChar, '/');

            var fileBytes = fs.File.ReadAllBytes(file);
            var hash = ComputeSha256(fileBytes);

            entries.Add(new TrustCatalogEntry
            {
                Path = relativePath,
                Sha256 = hash,
                UpdatedAt = now,
            });
        }

        return new TrustCatalog { GeneratedAt = now, Entries = entries };
    }

    /// <summary>
    /// Writes a trust catalog to the skill directory as trust.json.
    /// </summary>
    public static void WriteCatalog(string skillDir, TrustCatalog catalog, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var catalogPath = fs.Path.Combine(skillDir, CatalogFileName);
        var json = JsonSerializer.Serialize(catalog, JsonOptions);
        fs.File.WriteAllText(catalogPath, json);
    }

    internal static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static List<string> CollectTrustableFiles(string dirPath, IFileSystem fs)
    {
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(dirPath);

        while (stack.Count > 0)
        {
            var currentDir = stack.Pop();
            try
            {
                foreach (var entry in fs.Directory.EnumerateFileSystemEntries(currentDir))
                {
                    var name = fs.Path.GetFileName(entry);
                    if (name.StartsWith('.') || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (fs.Directory.Exists(entry))
                    {
                        stack.Push(entry);
                    }
                    else if (fs.File.Exists(entry) && SkillSecurityScanner.IsScannable(entry))
                    {
                        files.Add(entry);
                    }
                }
            }
            catch
            {
                // Skip inaccessible directories
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
