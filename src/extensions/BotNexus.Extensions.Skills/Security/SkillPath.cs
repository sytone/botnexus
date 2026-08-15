using System.IO.Abstractions;

namespace BotNexus.Extensions.Skills.Security;

/// <summary>
/// A filesystem path that has been proven to resolve inside a trusted skills root, or a trusted
/// root itself. It cannot be constructed from arbitrary input.
/// </summary>
/// <remarks>
/// <para>
/// The skill sandbox boundary was previously enforced entirely by convention: every method took a
/// bare <see cref="string"/>, so nothing at the type level distinguished a path that had survived
/// <see cref="SkillPathValidator.TryValidate"/> from raw tool input. A forgotten validation call was
/// invisible to the compiler and detectable only by review.
/// </para>
/// <para>
/// There are exactly two ways to obtain an instance, and both are validating:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="CreateRoot"/> / <see cref="TryCreateRoot"/> — a root directory taken from
/// configuration, not from user input. A root is trivially contained in itself, so it is the base
/// case of the containment relation rather than an exception to it.
/// </description></item>
/// <item><description>
/// <see cref="SkillPathValidator.TryValidate"/> — the only route from a candidate string to a
/// contained path. It resolves every symlink in the path and returns <c>false</c> with a
/// <c>default</c> <see cref="SkillPath"/> when the resolved location escapes the root.
/// </description></item>
/// </list>
/// <para>
/// The candidate argument of <see cref="SkillPathValidator.TryValidate"/> deliberately remains a
/// <see cref="string"/>: it is untrusted input, and a value that has not yet been validated is
/// precisely what this type must not be able to represent. The validator is the boundary; the type
/// is the proof that the boundary was crossed.
/// </para>
/// <para>
/// There is no implicit conversion from <see cref="string"/> in either direction, and the raw value
/// is reached through the explicit <see cref="Value"/> property so unwrapping sites stay greppable.
/// </para>
/// </remarks>
public readonly record struct SkillPath
{
    private readonly string? _value;

    private SkillPath(string value) => _value = value;

    /// <summary>
    /// True when this instance was produced by a validating factory. A <c>default(SkillPath)</c>
    /// is false and carries no path.
    /// </summary>
    public bool HasValue => _value is not null;

    /// <summary>
    /// The absolute, symlink-resolved path.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance is <c>default</c> and holds no path.</exception>
    public string Value => _value
        ?? throw new InvalidOperationException("SkillPath has no value; it was never created through a validating factory.");

    /// <summary>
    /// The single privileged constructor, reserved for <see cref="SkillPathValidator"/>, which is the
    /// only code permitted to assert that a path has been resolved and contained. Deliberately
    /// internal and deliberately named so the architecture fence
    /// <c>SkillPathConstructionArchitectureTests</c> can assert no other file calls it.
    /// </summary>
    internal static SkillPath FromResolved(string resolvedAbsolutePath) => new(resolvedAbsolutePath);

    /// <summary>
    /// Creates a trusted skills root from a configured directory path. Roots come from configuration
    /// rather than from tool or HTTP input, so the only requirement is that the value is a non-empty
    /// path that can be made absolute.
    /// </summary>
    /// <exception cref="ArgumentException">The value is null, empty, or not a usable path.</exception>
    public static SkillPath CreateRoot(string absolutePath, IFileSystem fileSystem)
        => TryCreateRoot(absolutePath, fileSystem, out var root)
            ? root
            : throw new ArgumentException("Value is not a usable skills root path.", nameof(absolutePath));

    /// <summary>
    /// Attempts to create a trusted skills root from a configured directory path.
    /// </summary>
    /// <param name="absolutePath">Configured root directory; may be null or relative.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="root">The normalised root on success; <c>default</c> on failure.</param>
    /// <returns>True when a root could be formed.</returns>
    public static bool TryCreateRoot(string? absolutePath, IFileSystem fileSystem, out SkillPath root)
    {
        root = default;

        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        try
        {
            root = new SkillPath(fileSystem.Path.GetFullPath(absolutePath));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the path, or a marker when the instance is <c>default</c>. Unlike
    /// <c>WebhookSecret</c> a path is not a secret, so it is safe to render in diagnostics — and the
    /// error messages produced by <see cref="SkillPathValidator"/> depend on it.
    /// </summary>
    public override string ToString() => _value ?? "SkillPath(none)";
}
