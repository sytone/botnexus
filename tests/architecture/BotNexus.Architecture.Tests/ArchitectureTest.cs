namespace BotNexus.Architecture.Tests;

/// <summary>
/// Gives architecture tests one instance-scoped view of the repository layout so filesystem
/// fences do not each implement their own root discovery.
/// </summary>
public abstract class ArchitectureTest
{
    /// <summary>
    /// Resolves paths from the repository that contains the running test assembly.
    /// </summary>
    protected RepositoryLayout Repository { get; } = new(AppContext.BaseDirectory);
}

/// <summary>
/// Represents the stable top-level directories architecture tests inspect.
/// </summary>
public sealed class RepositoryLayout
{
    /// <summary>
    /// Locates the repository containing <paramref name="startDirectory"/> and validates its
    /// source and test directories once for all consumers.
    /// </summary>
    public RepositoryLayout(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null && !File.Exists(System.IO.Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;

        Root = current?.FullName
            ?? throw new DirectoryNotFoundException($"Could not locate repository root from {startDirectory}.");
        SourceRoot = RequireDirectory("src");
        TestsRoot = RequireDirectory("tests");
    }

    /// <summary>The repository root that owns the running architecture test assembly.</summary>
    public string Root { get; }

    /// <summary>The repository's production project tree.</summary>
    public string SourceRoot { get; }

    /// <summary>The repository's test project tree.</summary>
    public string TestsRoot { get; }

    /// <summary>
    /// Combines repository-relative path segments without exposing platform-specific separators.
    /// </summary>
    public string Path(params string[] relativeParts) =>
        System.IO.Path.Combine(new[] { Root }.Concat(relativeParts).ToArray());

    private string RequireDirectory(string name)
    {
        var path = System.IO.Path.Combine(Root, name);
        return Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException($"Expected repository directory at {path}.");
    }
}