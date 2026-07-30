using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Security;
using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Covers display-path re-anchoring for the remaining path-returning tools (issue #2404). Symlink
/// resolution stays the containment check and must keep running against the real target, but the paths
/// reported back to the agent have to stay under the prefix the caller actually named - otherwise the
/// model follows the reported path out of the tree it asked about.
/// </summary>
public sealed class ToolSymlinkDisplayPathTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"botnexus-displaypath-{Guid.NewGuid():N}");

    public ToolSymlinkDisplayPathTests() => Directory.CreateDirectory(_workspace);

    // ---------------- GlobTool ----------------

    [Fact]
    public async Task GlobTool_WhenPathIsSymlinkedDirectory_ReturnsPathsUnderRequestedPrefix()
    {
        CreateLinkedFixture();
        var tool = new GlobTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, new Dictionary<string, object?>
        {
            ["pattern"] = "*.txt",
            ["path"] = "link"
        });

        output.ShouldContain("link/hit.txt");
        output.ShouldNotContain("real/hit.txt");
    }

    [Fact]
    public async Task GlobTool_WhenPathIsNotSymlinked_ReturnsUnchangedPaths()
    {
        CreatePlainFixture();
        var tool = new GlobTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, new Dictionary<string, object?>
        {
            ["pattern"] = "*.txt",
            ["path"] = "plain"
        });

        output.ShouldBe("plain/hit.txt");
    }

    [Fact]
    public async Task GlobTool_WhenEscapingThroughLink_IsStillRejected()
    {
        CreateLinkedFixture();
        var tool = new GlobTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, new Dictionary<string, object?>
        {
            ["pattern"] = "*.txt",
            ["path"] = "link/../../escape"
        });

        output.ShouldContain("not permitted for read");
    }

    // ---------------- WriteTool ----------------

    [Fact]
    public async Task WriteTool_WhenPathIsSymlinkedDirectory_ReportsRequestedPrefix()
    {
        CreateLinkedFixture();
        var tool = new WriteTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = "link/written.txt",
            ["content"] = "hello"
        });

        output.ShouldContain("link/written.txt");
        output.ShouldNotContain("real/written.txt");
    }

    [Fact]
    public async Task WriteTool_WhenPathIsNotSymlinked_ReportsUnchangedPath()
    {
        CreatePlainFixture();
        var tool = new WriteTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = "plain/written.txt",
            ["content"] = "hello"
        });

        output.ShouldBe("Wrote 'plain/written.txt' (5 bytes).");
    }

    [Fact]
    public async Task WriteTool_WhenEscapingThroughLink_IsStillRejected()
    {
        CreateLinkedFixture();
        var tool = new WriteTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = "link/../../escape.txt",
            ["content"] = "hello"
        });

        output.ShouldContain("not permitted for write");
    }

    // ---------------- EditTool ----------------

    [Fact]
    public async Task EditTool_WhenPathIsSymlinkedDirectory_ReportsRequestedPrefix()
    {
        CreateLinkedFixture();
        var tool = new EditTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, EditArguments("link/hit.txt"));

        output.ShouldContain("link/hit.txt");
        output.ShouldNotContain("real/hit.txt");
    }

    [Fact]
    public async Task EditTool_WhenPathIsNotSymlinked_ReportsUnchangedPath()
    {
        CreatePlainFixture();
        var tool = new EditTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, EditArguments("plain/hit.txt"));

        output.ShouldContain("Successfully replaced 1 block(s) in 'plain/hit.txt'.");
        output.ShouldContain("--- a/plain/hit.txt");
        output.ShouldContain("+++ b/plain/hit.txt");
    }

    [Fact]
    public async Task EditTool_WhenEscapingThroughLink_IsStillRejected()
    {
        CreateLinkedFixture();
        var tool = new EditTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, EditArguments("link/../../escape.txt"));

        output.ShouldContain("not permitted for write");
    }

    // ---------------- ReadTool (empty-directory notice) ----------------

    [Fact]
    public async Task ReadTool_WhenEmptyDirectoryIsSymlinked_ReportsRequestedPrefix()
    {
        var realDirectory = Path.Combine(_workspace, "realempty");
        Directory.CreateDirectory(realDirectory);
        CreateDirectoryLink(Path.Combine(_workspace, "linkempty"), realDirectory);
        var tool = new ReadTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, new Dictionary<string, object?> { ["path"] = "linkempty" });

        output.ShouldContain("linkempty");
        output.ShouldNotContain("realempty");
    }

    [Fact]
    public async Task ReadTool_WhenEmptyDirectoryIsNotSymlinked_ReportsUnchangedPath()
    {
        var plainDirectory = Path.Combine(_workspace, "plainempty");
        Directory.CreateDirectory(plainDirectory);
        var tool = new ReadTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, new Dictionary<string, object?> { ["path"] = "plainempty" });

        output.ShouldBe($"Directory '{plainDirectory.Replace('\\', '/')}' is empty (within depth 2).");
    }

    [Fact]
    public async Task ReadTool_WhenEscapingThroughLink_IsStillRejected()
    {
        CreateLinkedFixture();
        var tool = new ReadTool(_workspace, new DefaultPathValidator(policy: null, _workspace));

        var output = await RunAsync(tool, new Dictionary<string, object?> { ["path"] = "link/../../escape.txt" });

        output.ShouldContain("not permitted for read");
    }

    // ---------------- helpers ----------------

    private static Dictionary<string, object?> EditArguments(string path) => new()
    {
        ["path"] = path,
        ["edits"] = new List<object?>
        {
            new Dictionary<string, object?> { ["oldText"] = "needle", ["newText"] = "pin" }
        }
    };

    private static async Task<string> RunAsync(IAgentTool tool, IReadOnlyDictionary<string, object?> arguments)
    {
        var prepared = await tool.PrepareArgumentsAsync(arguments);
        var result = await tool.ExecuteAsync($"call-{Guid.NewGuid():N}", prepared);
        return string.Concat(result.Content.Select(c => c.Value)).Replace('\\', '/');
    }

    private void CreateLinkedFixture()
    {
        var realDirectory = Path.Combine(_workspace, "real");
        Directory.CreateDirectory(realDirectory);
        File.WriteAllText(Path.Combine(realDirectory, "hit.txt"), "needle here");
        CreateDirectoryLink(Path.Combine(_workspace, "link"), realDirectory);
    }

    private void CreatePlainFixture()
    {
        var plainDirectory = Path.Combine(_workspace, "plain");
        Directory.CreateDirectory(plainDirectory);
        File.WriteAllText(Path.Combine(plainDirectory, "hit.txt"), "needle here");
    }

    /// <summary>
    /// Creates a directory symbolic link, falling back to a Windows directory junction when the process
    /// lacks the privilege to create symlinks. Junctions are reparse points too, so they exercise the same
    /// resolution path and need no elevation. If neither can be created the helper throws, so the test
    /// fails loudly rather than passing vacuously.
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
