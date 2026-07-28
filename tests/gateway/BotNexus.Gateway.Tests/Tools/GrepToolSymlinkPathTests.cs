using BotNexus.Gateway.Security;
using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Covers path re-anchoring for grep results. Symlink resolution is the containment check and must keep
/// running against the real target, but the paths reported back to the agent have to stay under the path
/// prefix the caller actually named - otherwise the model follows the reported path out of the tree it
/// asked about and hits confusing reads, diffs, or validator rejections. See issue #2384.
/// </summary>
public sealed class GrepToolSymlinkPathTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"botnexus-grep-symlink-{Guid.NewGuid():N}");

    public GrepToolSymlinkPathTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public async Task ExecuteAsync_WhenPathIsSymlinkedDirectory_ReturnsPathsUnderRequestedPrefix()
    {
        CreateLinkedFixture();

        var tool = new GrepTool(_workspace);
        var normalized = await GrepAsync(tool, "link");

        normalized.ShouldContain("link/hit.txt");
        normalized.ShouldNotContain("real/hit.txt");
    }

    [Fact]
    public async Task ExecuteAsync_WithPathValidator_WhenPathIsSymlinkedDirectory_ReturnsPathsUnderRequestedPrefix()
    {
        CreateLinkedFixture();

        // Mirrors the production wiring in DefaultAgentToolFactory, where GrepTool always gets a validator.
        var tool = new GrepTool(_workspace, new DefaultPathValidator(policy: null, _workspace));
        var normalized = await GrepAsync(tool, "link");

        normalized.ShouldContain("link/hit.txt");
        normalized.ShouldNotContain("real/hit.txt");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathIsNotSymlinked_ReturnsUnchangedPaths()
    {
        var plainDirectory = Path.Combine(_workspace, "plain");
        Directory.CreateDirectory(plainDirectory);
        await File.WriteAllTextAsync(Path.Combine(plainDirectory, "hit.txt"), "needle here");

        var tool = new GrepTool(_workspace);
        var normalized = await GrepAsync(tool, "plain");

        normalized.ShouldContain("plain/hit.txt");
    }

    private void CreateLinkedFixture()
    {
        var realDirectory = Path.Combine(_workspace, "real");
        Directory.CreateDirectory(realDirectory);
        File.WriteAllText(Path.Combine(realDirectory, "hit.txt"), "needle here");
        CreateDirectoryLink(Path.Combine(_workspace, "link"), realDirectory);
    }

    private static async Task<string> GrepAsync(GrepTool tool, string path)
    {
        var result = await tool.ExecuteAsync($"call-{Guid.NewGuid():N}", new Dictionary<string, object?>
        {
            ["pattern"] = "needle",
            ["path"] = path
        });

        return string.Concat(result.Content.Select(c => c.Value)).Replace('\\', '/');
    }

    /// <summary>
    /// Creates a directory symbolic link, falling back to a Windows directory junction when the process
    /// lacks the privilege to create symlinks (no admin rights and Developer Mode disabled). Junctions are
    /// reparse points too, so they exercise the same resolution path and need no elevation. If neither can
    /// be created the helper throws, so the test fails loudly rather than passing vacuously.
    /// </summary>
    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // Fall through to a junction, which needs no elevation.
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // Fall through to a junction, which needs no elevation.
        }

        CreateWindowsJunction(linkPath, targetPath);
    }

    private static void CreateWindowsJunction(string linkPath, string targetPath)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Unable to start cmd.exe to create a directory junction.");

        process.WaitForExit();
        if (!Directory.Exists(linkPath))
        {
            throw new InvalidOperationException(
                $"Unable to create a directory link at '{linkPath}'; symlink and junction creation both failed.");
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(_workspace))
        {
            return;
        }

        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp fixture.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp fixture.
        }
    }
}
