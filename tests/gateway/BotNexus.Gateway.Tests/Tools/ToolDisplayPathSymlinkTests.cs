using System.IO.Abstractions;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Tools;
using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Regression coverage for issue #2404. Path validation resolves symlinks before the containment check and
/// that must stay exactly as-is — it is the security boundary. What must NOT leak is the resolved path in the
/// text handed back to the agent: a caller who asked about <c>link/</c> has to see results under <c>link/</c>,
/// not under the link's real target, otherwise the model follows reported paths out of the tree it reasoned
/// about.
/// </summary>
/// <remarks>
/// Every test here ends in an unconditional assertion. <see cref="SymlinkFixture.CreateDirectoryLink"/> throws
/// when it cannot create a link, so setup failure fails the test loudly rather than passing vacuously — no
/// early return, no conditional skip, no catch-and-continue.
/// </remarks>
public sealed class ToolDisplayPathSymlinkTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"botnexus-display-path-{Guid.NewGuid():N}");

    public ToolDisplayPathSymlinkTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public async Task GlobTool_WhenBasePathIsSymlinkedDirectory_ReturnsPathsUnderRequestedPrefix()
    {
        CreateLinkedFixture();

        var tool = new GlobTool(_workspace, new DefaultPathValidator(policy: null, _workspace));
        var output = await ExecuteAsync(
            (id, args, ct) => tool.ExecuteAsync(id, args, ct),
            new Dictionary<string, object?> { ["pattern"] = "**/*.txt", ["path"] = "link" });

        output.ShouldContain("link/hit.txt");
        output.ShouldNotContain("real/hit.txt");
    }

    [Fact]
    public async Task GlobTool_WhenBasePathIsNotSymlinked_ReturnsUnchangedPaths()
    {
        var plain = Path.Combine(_workspace, "plain");
        Directory.CreateDirectory(plain);
        await File.WriteAllTextAsync(Path.Combine(plain, "hit.txt"), "content");

        var tool = new GlobTool(_workspace, new DefaultPathValidator(policy: null, _workspace));
        var output = await ExecuteAsync(
            (id, args, ct) => tool.ExecuteAsync(id, args, ct),
            new Dictionary<string, object?> { ["pattern"] = "**/*.txt", ["path"] = "plain" });

        output.ShouldContain("plain/hit.txt");
    }

    [Fact]
    public async Task ReadTool_WhenDirectoryIsSymlinkedAndEmpty_ReportsRequestedPrefix()
    {
        var realDirectory = Path.Combine(_workspace, "real");
        Directory.CreateDirectory(realDirectory);
        SymlinkFixture.CreateDirectoryLink(Path.Combine(_workspace, "link"), realDirectory);

        var tool = new ReadTool(_workspace, new DefaultPathValidator(policy: null, _workspace));
        var output = await ExecuteAsync(
            (id, args, ct) => tool.ExecuteAsync(id, args, ct),
            new Dictionary<string, object?> { ["path"] = "link" });

        output.ShouldContain("link");
        output.ShouldNotContain($"'{_workspace.Replace('\\', '/')}/real'");
    }

    [Fact]
    public async Task AgentFilesTool_WhenPathIsSymlinkedDirectory_ReportsRequestedPrefix()
    {
        var realDirectory = Path.Combine(_workspace, "real");
        Directory.CreateDirectory(Path.Combine(realDirectory, ".git"));
        await File.WriteAllTextAsync(Path.Combine(realDirectory, "AGENTS.md"), "# conventions");
        var linkPath = Path.Combine(_workspace, "link");
        SymlinkFixture.CreateDirectoryLink(linkPath, realDirectory);

        var tool = new AgentFilesTool(new DefaultPathValidator(policy: null, _workspace), new FileSystem());
        var output = await ExecuteAsync(
            (id, args, ct) => tool.ExecuteAsync(id, args, ct),
            new Dictionary<string, object?> { ["path"] = linkPath });

        var normalized = output.Replace('\\', '/');
        normalized.ShouldContain("link/AGENTS.md");
        normalized.ShouldNotContain("real/AGENTS.md");
    }

    [Fact]
    public async Task AgentFilesTool_WhenPathIsNotSymlinked_ReportsUnchangedPath()
    {
        var plain = Path.Combine(_workspace, "plain");
        Directory.CreateDirectory(Path.Combine(plain, ".git"));
        await File.WriteAllTextAsync(Path.Combine(plain, "AGENTS.md"), "# conventions");

        var tool = new AgentFilesTool(new DefaultPathValidator(policy: null, _workspace), new FileSystem());
        var output = await ExecuteAsync(
            (id, args, ct) => tool.ExecuteAsync(id, args, ct),
            new Dictionary<string, object?> { ["path"] = plain });

        output.Replace('\\', '/').ShouldContain("plain/AGENTS.md");
    }

    private void CreateLinkedFixture()
    {
        var realDirectory = Path.Combine(_workspace, "real");
        Directory.CreateDirectory(realDirectory);
        File.WriteAllText(Path.Combine(realDirectory, "hit.txt"), "content");
        SymlinkFixture.CreateDirectoryLink(Path.Combine(_workspace, "link"), realDirectory);
    }

    private static async Task<string> ExecuteAsync(
        Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<Agent.Core.Types.AgentToolResult>> execute,
        Dictionary<string, object?> arguments)
    {
        var result = await execute($"call-{Guid.NewGuid():N}", arguments, CancellationToken.None);
        return string.Concat(result.Content.Select(c => c.Value)).Replace('\\', '/');
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

/// <summary>
/// Shared link-creation helper for display-path regression tests. Fails loudly when no link can be created so
/// no test in this area can pass vacuously.
/// </summary>
internal static class SymlinkFixture
{
    /// <summary>
    /// Creates a directory symbolic link, falling back to a Windows directory junction when the process lacks
    /// the privilege to create symlinks. Junctions are reparse points too, so they exercise the same
    /// resolution path and need no elevation. Throws if neither can be created.
    /// </summary>
    internal static void CreateDirectoryLink(string linkPath, string targetPath)
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
}
