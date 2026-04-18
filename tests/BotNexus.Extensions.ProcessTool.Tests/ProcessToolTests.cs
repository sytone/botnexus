using System.Diagnostics;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.ProcessTool;
using FluentAssertions;

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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(interval);
        }

        condition().Should().BeTrue("condition should be met within timeout");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(interval);
        }

        (await condition()).Should().BeTrue("condition should be met within timeout");
    }

    private async Task WaitForOutputContainsAsync(int pid, string expectedText, int? tail = null)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            var result = await _tool.ExecuteAsync("c1", Args("output", pid: pid, tail: tail));
            var text = ResultText(result);
            if (text.Contains(expectedText, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        var finalResult = await _tool.ExecuteAsync("c1", Args("output", pid: pid, tail: tail));
        ResultText(finalResult).Should().Contain(expectedText);
    }

    // ───────────── list ─────────────

    [Fact]
    public async Task List_WithNoProcesses_ReturnsEmpty()
    {
        var result = await _tool.ExecuteAsync("c1", Args("list"));
        var text = ResultText(result);

        text.Should().Contain("No tracked processes");
    }

    [Fact]
    public async Task List_AfterRegister_ShowsProcess()
    {
        var managed = SpawnTestProcess("echo hello", "echo hello");
        managed.WaitForExit(5_000);

        var result = await _tool.ExecuteAsync("c1", Args("list"));
        var text = ResultText(result);

        text.Should().Contain(managed.Pid.ToString());
        text.Should().Contain("echo hello");
    }

    // ───────────── status ─────────────

    [Fact]
    public async Task Status_RunningProcess_ReportsRunning()
    {
        var managed = SpawnTestProcess("ping -n 60 127.0.0.1 >nul", "sleep 60");
        var text = string.Empty;
        await WaitUntilAsync(async () =>
        {
            var result = await _tool.ExecuteAsync("c1", Args("status", pid: managed.Pid));
            text = ResultText(result);
            return text.Contains("running", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));

        text.Should().Contain("running");
        text.Should().Contain(managed.Pid.ToString());
    }

    [Fact]
    public async Task Status_ExitedProcess_ReportsExited()
    {
        var managed = SpawnTestProcess("echo done", "echo done");
        managed.WaitForExit(5_000);

        var result = await _tool.ExecuteAsync("c1", Args("status", pid: managed.Pid));
        var text = ResultText(result);

        text.Should().Contain("exited");
        text.Should().Contain("Exit Code");
    }

    [Fact]
    public async Task Status_UnknownPid_ReturnsError()
    {
        var result = await _tool.ExecuteAsync("c1", Args("status", pid: 99999));
        var text = ResultText(result);

        text.Should().Contain("No tracked process");
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

        text.Should().Contain("test-output-line");
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
        lines.Length.Should().BeGreaterThanOrEqualTo(1);
        text.Should().Contain("line5");
        text.Should().NotContain("line1");
    }

    // ───────────── kill ─────────────

    [Fact]
    public async Task Kill_TerminatesRunningProcess()
    {
        var managed = SpawnTestProcess("ping -n 60 127.0.0.1 >nul", "sleep 60");
        await WaitUntilAsync(() => managed.IsRunning, TimeSpan.FromSeconds(5));

        managed.IsRunning.Should().BeTrue();

        var result = await _tool.ExecuteAsync("c1", Args("kill", pid: managed.Pid));
        var text = ResultText(result);

        text.Should().Contain("terminated");
        await WaitUntilAsync(() => !managed.IsRunning, TimeSpan.FromSeconds(5));
        managed.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Kill_AlreadyExitedProcess_IsNoOp()
    {
        var managed = SpawnTestProcess("echo bye", "echo bye");
        managed.WaitForExit(5_000);

        var result = await _tool.ExecuteAsync("c1", Args("kill", pid: managed.Pid));
        var text = ResultText(result);

        text.Should().Contain("already exited");
    }
}
