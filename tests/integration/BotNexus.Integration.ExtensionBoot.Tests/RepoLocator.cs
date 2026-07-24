using System.IO;

namespace BotNexus.Integration.ExtensionBoot.Tests;

/// <summary>
/// Locates the repository root by walking up from the test assembly's base directory
/// until a marker file (.git or BotNexus.slnx) is found. The extension-boot gate needs
/// the repo root to (a) find the built CLI dll and (b) point the gateway's --source at
/// the in-tree extension set for deployment.
/// </summary>
internal static class RepoLocator
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, "BotNexus.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repo root (no .git or BotNexus.slnx marker) walking up from {AppContext.BaseDirectory}.");
    }
}
