using System.Runtime.InteropServices;
using BotNexus.Agent.Core.Types;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Working-directory resolution contract for <see cref="ExecTool"/> (issue #2416).
/// <para>
/// <c>exec</c> and <c>shell</c> used to disagree about where a command runs: <c>shell</c> was built
/// per-session with the agent workspace, while <c>exec</c> was auto-registered as a bare DI singleton
/// with no working directory and therefore inherited the gateway process's current directory (the
/// user profile on Windows). The documented "write <c>tmp/q.py</c> then run it" recipe silently failed
/// from <c>exec</c> because the relative path resolved outside the workspace.
/// </para>
/// <para>
/// These tests pin the corrected contract: the configured workspace is the default working directory,
/// an explicit <c>workingDir</c> still wins, absolute paths are untouched, and a relative
/// <c>workingDir</c> resolves against the workspace rather than the process directory.
/// </para>
/// </summary>
public sealed class ExecToolWorkingDirectoryTests : IDisposable
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private readonly string _workspace;

    public ExecToolWorkingDirectoryTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "botnexus-exec-cwd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a lingering temp directory must never fail a test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>
    /// The exact #2416 reproduction: a file written into <c>&lt;workspace&gt;/tmp</c> must be reachable
    /// from <c>exec</c> through its workspace-relative path, with no <c>workingDir</c> argument.
    /// </summary>
    [Fact]
    public async Task RelativeScriptPath_ResolvesAgainstAgentWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "tmp"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, "tmp", "probe.txt"), "REPRO2416");

        var tool = new ExecTool(_workspace, new MockFileSystem());

        string[] command = IsWindows
            ? ["cmd.exe", "/c", "type tmp\\probe.txt"]
            : ["/bin/cat", "tmp/probe.txt"];

        var result = await tool.ExecuteAsync("t", await PrepareAsync(tool, command));

        GetText(result).ShouldContain("REPRO2416");
    }

    /// <summary>
    /// With no <c>workingDir</c>, the resolved current directory reported by the child process is the
    /// agent workspace - the same directory <c>shell</c> is constructed with by the workspace tool
    /// factory, so both execution tools now agree.
    /// </summary>
    [Fact]
    public async Task NoWorkingDir_ChildRunsInAgentWorkspace()
    {
        var tool = new ExecTool(_workspace, new MockFileSystem());

        var result = await tool.ExecuteAsync("t", await PrepareAsync(tool, PrintCwdCommand()));

        NormalizePath(GetText(result)).ShouldBe(NormalizePath(_workspace));
    }

    /// <summary>
    /// An explicit absolute <c>workingDir</c> must still override the workspace default - the parameter
    /// existed before the fix and callers relying on it must be unaffected.
    /// </summary>
    [Fact]
    public async Task ExplicitAbsoluteWorkingDir_WinsOverWorkspaceDefault()
    {
        var other = Path.Combine(Path.GetTempPath(), "botnexus-exec-cwd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var tool = new ExecTool(_workspace, new MockFileSystem());

            var result = await tool.ExecuteAsync("t", await PrepareAsync(tool, PrintCwdCommand(), workingDir: other));

            NormalizePath(GetText(result)).ShouldBe(NormalizePath(other));
            NormalizePath(GetText(result)).ShouldNotBe(NormalizePath(_workspace));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    /// <summary>
    /// An absolute <c>workingDir</c> is passed through unchanged - workspace resolution must never
    /// rewrite a rooted path.
    /// </summary>
    [Fact]
    public async Task AbsoluteWorkingDir_IsNotRebasedOntoWorkspace()
    {
        var tool = new ExecTool(_workspace, new MockFileSystem());
        var absolute = Path.GetFullPath(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

        var prepared = await tool.PrepareArgumentsAsync(BuildArgs(PrintCwdCommand(), absolute));

        NormalizePath((string)prepared["workingDir"]!).ShouldBe(NormalizePath(absolute));
    }

    /// <summary>
    /// A relative <c>workingDir</c> resolves against the agent workspace, not the gateway process
    /// directory - the same rebasing rule that makes the default correct.
    /// </summary>
    [Fact]
    public async Task RelativeWorkingDir_ResolvesAgainstWorkspaceNotProcessDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "tmp"));

        var tool = new ExecTool(_workspace, new MockFileSystem());

        var prepared = await tool.PrepareArgumentsAsync(BuildArgs(PrintCwdCommand(), "tmp"));

        NormalizePath((string)prepared["workingDir"]!)
            .ShouldBe(NormalizePath(Path.Combine(_workspace, "tmp")));
    }

    /// <summary>
    /// Sad path: with no workspace configured at all the tool must keep its previous behaviour and
    /// fall back to the host process directory rather than throwing or inventing a path.
    /// </summary>
    [Fact]
    public async Task NoWorkspaceConfigured_FallsBackToProcessDirectory()
    {
        var tool = new ExecTool(workingDirectory: null, fileSystem: new MockFileSystem());

        var prepared = await tool.PrepareArgumentsAsync(BuildArgs(PrintCwdCommand(), "tmp"));

        NormalizePath((string)prepared["workingDir"]!)
            .ShouldBe(NormalizePath(Path.GetFullPath("tmp")));
    }

    private static string[] PrintCwdCommand() =>
        IsWindows ? ["cmd.exe", "/c", "cd"] : ["/bin/pwd"];

    private static async Task<IReadOnlyDictionary<string, object?>> PrepareAsync(
        ExecTool tool,
        string[] command,
        string? workingDir = null)
        => await tool.PrepareArgumentsAsync(BuildArgs(command, workingDir));

    private static IReadOnlyDictionary<string, object?> BuildArgs(string[] command, string? workingDir)
        => new Dictionary<string, object?>
        {
            ["command"] = (IReadOnlyList<string>)command.ToList(),
            ["workingDir"] = workingDir,
        };

    private static string GetText(AgentToolResult result)
    {
        result.Content.ShouldNotBeEmpty();
        return result.Content[0].Value;
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
}
