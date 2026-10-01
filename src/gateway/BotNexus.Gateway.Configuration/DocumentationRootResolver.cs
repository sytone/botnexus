using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration;

/// <summary>Identifies the ordered strategy that supplied the current BotNexus documentation tree.</summary>
public enum DocumentationRootStrategy
{
    /// <summary>No accessible documentation tree was found.</summary>
    Unresolved,
    /// <summary>An operator-provided path supplied the documentation tree.</summary>
    Override,
    /// <summary>A repository root was found by walking upward from the running source checkout.</summary>
    SourceCheckout,
    /// <summary>The documented released-install source location supplied the documentation tree.</summary>
    InstalledRelease
}

/// <summary>Describes a documentation-root resolution without hiding an unavailable installation.</summary>
public sealed record DocumentationRootResolution(
    DocumentationRootStrategy Strategy,
    string? DocsPath,
    string Message)
{
    /// <summary>True only when <see cref="DocsPath"/> names a verified documentation directory.</summary>
    public bool IsResolved => DocsPath is not null;
}

/// <summary>
/// Resolves the one documentation root consumed by Trailguide's bundled guidance skills.
/// </summary>
public static class DocumentationRootResolver
{
    /// <summary>Environment override checked before checkout and installed-layout strategies.</summary>
    public const string OverrideEnvironmentVariable = "BOTNEXUS_DOCUMENTATION_ROOT";

    /// <summary>
    /// Resolves documentation in explicit order: override, source-checkout ancestors, then the
    /// installed/released source root. A miss is represented explicitly rather than guessed.
    /// </summary>
    public static DocumentationRootResolution Resolve(
        IFileSystem fileSystem,
        string? overrideRoot = null,
        string? sourceStart = null,
        string? installedRoot = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var configuredOverride = string.IsNullOrWhiteSpace(overrideRoot)
            ? Environment.GetEnvironmentVariable(OverrideEnvironmentVariable)
            : overrideRoot;
        if (TryResolveCandidate(fileSystem, configuredOverride, out var overrideDocs))
            return Resolved(DocumentationRootStrategy.Override, overrideDocs);

        var current = string.IsNullOrWhiteSpace(sourceStart)
            ? Environment.CurrentDirectory
            : sourceStart;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (TryResolveCandidate(fileSystem, current, out var sourceDocs))
                return Resolved(DocumentationRootStrategy.SourceCheckout, sourceDocs);

            var parent = fileSystem.DirectoryInfo.New(current).Parent?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }

        var releaseRoot = string.IsNullOrWhiteSpace(installedRoot)
            ? fileSystem.Path.Combine(ResolveUserHome(), "botnexus")
            : installedRoot;
        if (TryResolveCandidate(fileSystem, releaseRoot, out var installedDocs))
            return Resolved(DocumentationRootStrategy.InstalledRelease, installedDocs);

        return new DocumentationRootResolution(
            DocumentationRootStrategy.Unresolved,
            null,
            "Documentation is not available on this installation. Set BOTNEXUS_DOCUMENTATION_ROOT to an accessible BotNexus repository root; do not present recalled platform detail as verified.");
    }

    private static DocumentationRootResolution Resolved(DocumentationRootStrategy strategy, string docsPath)
        => new(strategy, docsPath, $"Documentation resolved by {strategy} at '{docsPath}'.");

    private static bool TryResolveCandidate(IFileSystem fileSystem, string? candidate, out string docsPath)
    {
        docsPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var root = fileSystem.Path.GetFullPath(candidate);
        var directDocs = string.Equals(fileSystem.Path.GetFileName(root), "docs", StringComparison.OrdinalIgnoreCase)
            ? root
            : fileSystem.Path.Combine(root, "docs");
        var repositoryRoot = string.Equals(directDocs, root, StringComparison.OrdinalIgnoreCase)
            ? fileSystem.DirectoryInfo.New(root).Parent?.FullName
            : root;

        if (!fileSystem.Directory.Exists(directDocs) || string.IsNullOrWhiteSpace(repositoryRoot))
            return false;

        // Do not accept a generic repository merely because it has docs plus README/src. The
        // resolved tree becomes authoritative prompt input, so an unrelated checkout would be both
        // incorrect grounding and a local prompt-injection boundary.
        if (!fileSystem.File.Exists(fileSystem.Path.Combine(repositoryRoot, "BotNexus.slnx")))
            return false;

        docsPath = directDocs;
        return true;
    }

    private static string ResolveUserHome()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
            ? home
            : Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory;
}
