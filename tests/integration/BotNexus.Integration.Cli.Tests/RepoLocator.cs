using System.IO;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Locates the repository root by walking up from the test assembly's base directory
/// until a marker file (.git or Directory.Packages.props) is found. Used by integration tests
/// that need to clone or build from the in-tree repo.
/// </summary>
internal static class RepoLocator
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repo root (no .git or Directory.Packages.props marker) walking up from {AppContext.BaseDirectory}.");
    }
}
