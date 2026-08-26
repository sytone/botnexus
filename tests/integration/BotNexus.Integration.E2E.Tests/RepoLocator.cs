namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Walks up from the test assembly directory until a folder containing
/// <c>Directory.Packages.props</c> is found. Used by the pack-and-install fixture to
/// locate the in-tree CLI csproj without hard-coding a relative path.
/// </summary>
internal static class RepoLocator
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate Directory.Packages.props walking up from '{AppContext.BaseDirectory}'.");
    }
}
