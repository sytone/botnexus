using BotNexus.Gateway.Abstractions.Configuration;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Resolves <c>@location</c> path references against the configured location set.
/// </summary>
/// <remarks>
/// <para>
/// Issue #3547: this logic existed only inside <see cref="PlatformConfigAgentSource"/>, which
/// resolves aliases to absolute paths when materialising a descriptor. The agent WRITER needs the
/// identical resolution to recognise that an incoming absolute path is merely the resolved form of
/// a stored alias, and must therefore be written back in its portable spelling rather than as a
/// machine-specific absolute path.
/// </para>
/// <para>
/// It lives here as a single shared seam rather than as a second private copy in the writer,
/// because two independent implementations of the same resolution rule is precisely the drift that
/// lets a read and a write disagree about what a path means.
/// </para>
/// </remarks>
public static class LocationReferenceResolver
{
    /// <summary>Returns true when the value is an <c>@location</c> reference.</summary>
    public static bool IsReference(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith('@');

    /// <summary>
    /// Resolves an <c>@location</c> reference to an absolute path, or null when the value is not a
    /// reference, names no location, or names one the resolver does not know.
    /// </summary>
    public static string? Resolve(string? path, ILocationResolver? locationResolver)
    {
        if (locationResolver is null || !IsReference(path))
            return null;

        var reference = path![1..];
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var separatorIndex = reference.IndexOfAny(['/', '\\']);
        var locationName = separatorIndex >= 0 ? reference[..separatorIndex] : reference;
        if (string.IsNullOrWhiteSpace(locationName))
            return null;

        var basePath = locationResolver.ResolvePath(locationName);
        if (string.IsNullOrWhiteSpace(basePath))
            return null;

        if (separatorIndex < 0 || separatorIndex == reference.Length - 1)
            return Path.GetFullPath(basePath);

        var subPath = reference[(separatorIndex + 1)..];
        if (string.IsNullOrWhiteSpace(subPath))
            return Path.GetFullPath(basePath);

        var normalizedSubPath = subPath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(basePath, normalizedSubPath));
    }
}
