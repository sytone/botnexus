using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// A filesystem directory an agent operates against - the workspace root handed to the file tools,
/// or an explicit working directory for a spawned process. Construct via <see cref="From(string)"/>;
/// the value must be a non-empty, syntactically valid directory path and is stored trimmed with
/// forward and backslash separators preserved as written.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this type does and does not promise.</b> It guarantees <em>path shape</em>: non-empty,
/// no characters the platform rejects in a path, and within the platform path-length ceiling. It
/// deliberately does <b>not</b> promise the directory exists, is absolute, or is reachable - those
/// are runtime facts that change under the caller's feet, and a value object that claimed them
/// would be a guard that cannot hold. Containment and traversal safety remain the job of
/// <c>PathUtils.ResolvePath</c> and <c>IPathValidator</c>; this type removes the "is this string
/// even a path?" question from every call site so those guards start from a known-good input.
/// </para>
/// <para>
/// <b>Why no trailing-separator normalisation.</b> <c>Path.GetFullPath</c> is the codebase's
/// canonicalisation seam and callers already apply it where an absolute path is required. Trimming
/// separators here would silently change <c>"C:\"</c> into <c>"C:"</c>, which resolves to a
/// process-relative drive-current directory on Windows - a real path but a different one. The
/// normaliser therefore only trims surrounding whitespace.
/// </para>
/// <para>Introduced by #502 (primitive obsession phase 3).</para>
/// </remarks>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct WorkingDir
{
    /// <summary>
    /// Maximum accepted path length. Chosen as the extended-length Windows ceiling rather than the
    /// legacy 260-character <c>MAX_PATH</c>, because long-path support is enabled on modern Windows
    /// and Linux allows 4096 - rejecting at 260 would refuse paths the runtime accepts.
    /// </summary>
    public const int MaxLength = 4096;

    private static Validation Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Validation.Invalid("WorkingDir cannot be null, empty, or whitespace.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            return Validation.Invalid(
                $"WorkingDir must be {MaxLength} characters or fewer (was {trimmed.Length}).");
        }

        // Path.GetInvalidPathChars() is platform-specific by design: the same string may be a legal
        // path on Linux and illegal on Windows. Validating against the running platform is correct -
        // the value is about to be handed to that platform's filesystem APIs.
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return Validation.Invalid(
                "WorkingDir contains characters that are not valid in a filesystem path.");
        }

        // A null byte terminates a path in the native APIs, so a value containing one would be
        // silently truncated rather than rejected. GetInvalidPathChars does not include it on every
        // platform, so check explicitly.
        return trimmed.Contains('\0', StringComparison.Ordinal)
            ? Validation.Invalid("WorkingDir cannot contain a null character.")
            : Validation.Ok;
    }

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
