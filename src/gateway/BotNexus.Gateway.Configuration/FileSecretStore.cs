using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Metadata about a stored secret. Every member is derived from the file's <b>name</b> or its
/// <b>filesystem metadata</b> - never from its content (#3528 AC2).
/// </summary>
/// <remarks>
/// There is deliberately no value, no prefix, no masked form and no hash. A masked form leaks the
/// length; a hash is an offline-guessable oracle for a short secret. The only way to recover a
/// value is to read the file on the host, which is the documented escape hatch.
/// </remarks>
/// <param name="Key">The secret key, which is verbatim the file name.</param>
/// <param name="CreatedUtc">File creation time, UTC.</param>
/// <param name="ModifiedUtc">File last-write time, UTC.</param>
/// <param name="SizeBytes">Size of the file in bytes.</param>
public sealed record SecretDescriptor(string Key, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc, long SizeBytes);

/// <summary>
/// Thrown when a caller supplies a secret key that is not a legal file name under the secrets
/// directory - a traversal attempt, a separator, an absolute path, or an out-of-charset name.
/// </summary>
public sealed class InvalidSecretKeyException(string key)
    : ArgumentException($"'{key}' is not a valid secret key. Keys must match {FileSecretStore.KeyPatternText}.")
{
    /// <summary>The rejected key, retained for logging and API error payloads.</summary>
    public string Key { get; } = key;
}

/// <summary>
/// The list/add/delete seam over the file-per-secret directory (#3528).
/// </summary>
/// <remarks>
/// <para><b>Why an interface at all when there is one implementation.</b> The stated deliverable of
/// #3528 is the seam, not the policy: per-agent key scoping is a follow-up, and having every caller
/// already routed through one abstraction makes that a change to one component rather than an audit
/// of every filesystem call in the tree.</para>
/// <para><b>There is deliberately no read method.</b> Adding one here is what would make a
/// read-value endpoint possible, so the absence is load-bearing rather than an oversight.</para>
/// </remarks>
public interface IFileSecretStore
{
    /// <summary>The directory secrets live in.</summary>
    string SecretsDirectory { get; }

    /// <summary>Lists metadata for every stored secret, ordered by key.</summary>
    IReadOnlyList<SecretDescriptor> List();

    /// <summary>
    /// Creates or overwrites the secret <paramref name="key"/> with <paramref name="value"/>.
    /// Overwrite requires the caller to supply the complete new value - there is no read-back to
    /// merge against.
    /// </summary>
    /// <exception cref="InvalidSecretKeyException">The key is not a legal secret key.</exception>
    SecretDescriptor Set(string key, string value);

    /// <summary>Deletes a secret. Returns false when the key did not exist.</summary>
    /// <exception cref="InvalidSecretKeyException">The key is not a legal secret key.</exception>
    bool Delete(string key);

    /// <summary>Reports whether a secret with this key exists, without touching its content.</summary>
    /// <exception cref="InvalidSecretKeyException">The key is not a legal secret key.</exception>
    bool Exists(string key);
}

/// <summary>
/// Filesystem-backed <see cref="IFileSecretStore"/>: one file per secret under
/// <see cref="BotNexusHome.SecretsPath"/>, file name is the key, file content is the raw value.
/// </summary>
/// <remarks>
/// <para><b>Why raw content and not a wrapper format.</b> The escape hatch is the point. An operator
/// on the host must be able to recover a value with <c>cat</c>; a JSON envelope or an encoding layer
/// would make the documented recovery path a tooling problem for no security gain, because the file
/// permissions - not the encoding - are what protect the value.</para>
/// <para><b>Owner-only, through the one seam.</b> Every write calls
/// <see cref="SecureFilePermissions.RestrictToOwner(IFileSystem, string)"/> (#2392). This class is
/// pinned by <c>SecretFilePermissionFenceArchitectureTests</c> for exactly the reason #3414 exists:
/// <c>config.db</c> was a secret store added after that seam that quietly skipped it.</para>
/// </remarks>
public sealed class FileSecretStore : IFileSecretStore
{
    /// <summary>Human-readable form of the key charset, used in validation error messages.</summary>
    public const string KeyPatternText = "^[A-Za-z0-9._-]{1,128}$";

    /// <summary>
    /// Strict allowlist for key names (#3528 AC5). Anchored and charset-based rather than a
    /// blocklist: enumerating the ways a path can escape a directory across two operating systems is
    /// a losing game, so nothing that is not explicitly permitted gets through. Note the charset
    /// excludes <c>/</c>, <c>\</c>, <c>:</c> and the null byte by construction, which is what makes
    /// separator and drive-qualified traversal unrepresentable rather than merely filtered.
    /// </summary>
    private static readonly Regex KeyPattern = new(KeyPatternText, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IFileSystem _fileSystem;

    /// <inheritdoc />
    public string SecretsDirectory { get; }

    /// <summary>Creates a store rooted at <paramref name="secretsDirectory"/>.</summary>
    public FileSecretStore(string secretsDirectory, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);
        SecretsDirectory = secretsDirectory;
        _fileSystem = fileSystem;
    }

    /// <summary>Creates a store rooted at the home's <see cref="BotNexusHome.SecretsPath"/>.</summary>
    public FileSecretStore(BotNexusHome home, IFileSystem fileSystem)
        : this(ArgNotNull(home).SecretsPath, fileSystem)
    {
    }

    private static BotNexusHome ArgNotNull(BotNexusHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return home;
    }

    /// <summary>
    /// Validates a key against the allowlist. Returns false for anything that is not a plain,
    /// in-charset file name - including <c>.</c> and <c>..</c>, which match the charset but are
    /// directory references rather than file names.
    /// </summary>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        if (key is "." or "..")
            return false;
        return KeyPattern.IsMatch(key);
    }

    /// <inheritdoc />
    public IReadOnlyList<SecretDescriptor> List()
    {
        if (!_fileSystem.Directory.Exists(SecretsDirectory))
            return [];

        var results = new List<SecretDescriptor>();
        foreach (var path in _fileSystem.Directory.EnumerateFiles(SecretsDirectory))
        {
            var name = _fileSystem.Path.GetFileName(path);

            // A file already on disk whose name is not a legal key (dropped there by hand, or left
            // by an older tool) is not listed. Surfacing it would hand the UI a key it can neither
            // overwrite nor delete through the validated write path.
            if (!IsValidKey(name))
                continue;

            var info = _fileSystem.FileInfo.New(path);
            results.Add(new SecretDescriptor(
                name,
                new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Length));
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return results;
    }

    /// <inheritdoc />
    public SecretDescriptor Set(string key, string value)
    {
        var path = ResolvePath(key);
        _fileSystem.Directory.CreateDirectory(SecretsDirectory);

        // No BOM and no trailing newline: the file content IS the value, byte for byte, so anything
        // this writer adds is something the consuming script has to strip back off.
        _fileSystem.File.WriteAllText(path, value ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // #2392 seam. Applied after the write because the file must exist for the ACL/mode call to
        // address it, and non-fatally because a failure to narrow must not lose a written secret.
        SecureFilePermissions.RestrictToOwner(_fileSystem, path);

        var info = _fileSystem.FileInfo.New(path);
        return new SecretDescriptor(
            key,
            new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            info.Length);
    }

    /// <inheritdoc />
    public bool Delete(string key)
    {
        var path = ResolvePath(key);
        if (!_fileSystem.File.Exists(path))
            return false;
        _fileSystem.File.Delete(path);
        return true;
    }

    /// <inheritdoc />
    public bool Exists(string key) => _fileSystem.File.Exists(ResolvePath(key));

    /// <summary>
    /// Maps a key to its file path, refusing anything the allowlist rejects.
    /// </summary>
    /// <remarks>
    /// The containment check after the combine is deliberate belt-and-braces. The charset alone
    /// already makes escape unrepresentable, but this is a secret store: if a future edit loosens
    /// the pattern, the failure should be a rejected key rather than a write outside the directory.
    /// </remarks>
    private string ResolvePath(string key)
    {
        if (!IsValidKey(key))
            throw new InvalidSecretKeyException(key ?? string.Empty);

        var root = _fileSystem.Path.GetFullPath(SecretsDirectory);
        var candidate = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(root, key));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new InvalidSecretKeyException(key);

        return candidate;
    }
}
