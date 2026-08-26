using System.Diagnostics;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.ProcessTool;

namespace BotNexus.Extensions.ProcessTool.Tests;

public sealed class ProcessToolTests : IDisposable
{
    private readonly ProcessManager _manager = new();
    private readonly ProcessTool _tool;
    private readonly List<ManagedProcess> _spawnedProcesses = [];

    public ProcessToolTests()
    {
        _tool = new ProcessTool(_manager);
    }

    public void Dispose()
    {
        _manager.Clear();
        foreach (var p in _spawnedProcesses)
            p.Dispose();
    }

    // ───────────── helpers ─────────────

    private static IReadOnlyDictionary<string, object?> Args(
        string action,
        int? pid = null,
        string? content = null,
        int? tail = null,
        int? timeout = null)
    {
        var dict = new Dictionary<string, object?> { ["action"] = action };
        if (pid is not null) dict["pid"] = pid;
        if (content is not null) dict["content"] = content;
        if (tail is not null) dict["tail"] = tail;
        if (timeout is not null) dict["timeout"] = timeout;
        return dict;
    }

    private static string ResultText(AgentToolResult result)
        => string.Join("", result.Content.Select(c => c.Value));

    private ManagedProcess SpawnTestProcess(string windowsCommand, string unixCommand, bool redirectInput = false)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {windowsCommand}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = redirectInput,
                CreateNoWindow = true
            }
            : new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-lc \"{unixCommand.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = redirectInput,
                CreateNoWindow = true
            };

        var process = Process.Start(psi)!;
        var managed = new ManagedProcess(process, OperatingSystem.IsWindows() ? windowsCommand : unixCommand, DateTimeOffset.UtcNow);
        _spawnedProcesses.Add(managed);
        _manager.Register(process.Id, managed);
        return managed;
    }

    private Task WaitForOutputContainsAsync(int pid, string expectedText, int? tail = null)
        => TestAwait.EventuallyAsync(
            async () =>
            {
                var result = await _tool.ExecuteAsync("c1", Args("output", pid: pid, tail: tail));
                return ResultText(result).Contains(expectedText, StringComparison.Ordinal);
            },
            $"process {pid} output to contain '{expectedText}'");

    // ───────────── list ─────────────

    [Fact]
    public async Task List_WithNoProcesses_ReturnsEmpty()
    {
        var result = await _tool.ExecuteAsync("c1", Args("list"));
        var text = ResultText(result);

        text.ShouldContain("No tracked processes");
    }

    [Fact]
    public async Task List_AfterRegister_ShowsProcess()
    {
        var managed = SpawnTestProcess("echo hello", "echo hello");
        managed.WaitForExit(5_000);

        var result = await _tool.ExecuteAsync("c1", Args("list"));
        var text = ResultText(result);

        text.ShouldContain(managed.Pid.ToString());
        text.ShouldContain("echo hello");
    }

    // ───────────── status ─────────────

    [Fact]
    public async Task Status_RunningProcess_ReportsRunning()
    {
        var managed = SpawnTestProcess("ping -n 60 127.0.0.1 >nul", "sleep 60");
        var text = string.Empty;
        await TestAwait.EventuallyAsync(
            async () =>
            {
                var result = await _tool.ExecuteAsync("c1", Args("status", pid: managed.Pid));
                text = ResultText(result);
                return text.Contains("running", StringComparison.Ordinal);
            },
            $"process {managed.Pid} status to report running");

        text.ShouldContain("running");
        text.ShouldContain(managed.Pid.ToString());
    }

    [Fact]
    public async Task Status_ExitedProcess_ReportsExited()
    {
        var managed = SpawnTestProcess("echo done", "echo done");
        managed.WaitForExit(5_000);

        var result = await _tool.ExecuteAsync("c1", Args("status", pid: managed.Pid));
        var text = ResultText(result);

        text.ShouldContain("exited");
        text.ShouldContain("Exit Code");
    }

    [Fact]
    public async Task Status_UnknownPid_ReturnsError()
    {
        var result = await _tool.ExecuteAsync("c1", Args("status", pid: 99999));
        var text = ResultText(result);

        text.ShouldContain("No tracked process");
    }

    // ───────────── output ─────────────

    [Fact]
    public async Task Output_CapturesStdout()
    {
        var managed = SpawnTestProcess("echo test-output-line", "echo test-output-line");
        managed.WaitForExit(5_000);
        await WaitForOutputContainsAsync(managed.Pid, "test-output-line");
        var result = await _tool.ExecuteAsync("c1", Args("output", pid: managed.Pid));
        var text = ResultText(result);

        text.ShouldContain("test-output-line");
    }

    [Fact]
    public async Task Output_TailReturnsLastNLines()
    {
        var managed = SpawnTestProcess(
            "echo line1 & echo line2 & echo line3 & echo line4 & echo line5",
            "echo line1; echo line2; echo line3; echo line4; echo line5");
        managed.WaitForExit(5_000);
        await WaitForOutputContainsAsync(managed.Pid, "line5");

        var result = await _tool.ExecuteAsync("c1", Args("output", pid: managed.Pid, tail: 2));
        var text = ResultText(result);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBeGreaterThanOrEqualTo(1);
        text.ShouldContain("line5");
        text.ShouldNotContain("line1");
    }

    // ───────────── kill ─────────────

    [Fact]
    public async Task Kill_TerminatesRunningProcess()
    {
        var managed = SpawnTestProcess("ping -n 60 127.0.0.1 >nul", "sleep 60");
        await TestAwait.EventuallyAsync(
            () => managed.IsRunning,
            $"process {managed.Pid} to report running");

        managed.IsRunning.ShouldBeTrue();

        var result = await _tool.ExecuteAsync("c1", Args("kill", pid: managed.Pid));
        var text = ResultText(result);

        text.ShouldContain("terminated");
        await TestAwait.EventuallyAsync(
            () => !managed.IsRunning,
            $"process {managed.Pid} to stop after kill");
        managed.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task Kill_AlreadyExitedProcess_IsNoOp()
    {
        var managed = SpawnTestProcess("echo bye", "echo bye");
        managed.WaitForExit(5_000);

        var result = await _tool.ExecuteAsync("c1", Args("kill", pid: managed.Pid));
        var text = ResultText(result);

        text.ShouldContain("already exited");
    }
}
