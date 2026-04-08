using System.Diagnostics;
using BotNexus.AgentCore.Types;
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

    private ManagedProcess SpawnTestProcess(string arguments, bool redirectInput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            CreateNoWindow = true
        };

        var process = Process.Start(psi)!;
        var managed = new ManagedProcess(process, arguments, DateTimeOffset.UtcNow);
        _spawnedProcesses.Add(managed);
        _manager.Register(process.Id, managed);
        return managed;
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
        var managed = SpawnTestProcess("echo hello");
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
        // ping with a long wait keeps the process alive
        var managed = SpawnTestProcess("ping -n 60 127.0.0.1 >nul");

        // Give the process a moment to start
        await Task.Delay(200);

        var result = await _tool.ExecuteAsync("c1", Args("status", pid: managed.Pid));
        var text = ResultText(result);

        text.Should().Contain("running");
        text.Should().Contain(managed.Pid.ToString());
    }

    [Fact]
    public async Task Status_ExitedProcess_ReportsExited()
    {
        var managed = SpawnTestProcess("echo done");
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
        var managed = SpawnTestProcess("echo test-output-line");
        managed.WaitForExit(5_000);
        // Allow async event handlers to fire
        await Task.Delay(200);

        var result = await _tool.ExecuteAsync("c1", Args("output", pid: managed.Pid));
        var text = ResultText(result);

        text.Should().Contain("test-output-line");
    }

    [Fact]
    public async Task Output_TailReturnsLastNLines()
    {
        var managed = SpawnTestProcess("echo line1 & echo line2 & echo line3 & echo line4 & echo line5");
        managed.WaitForExit(5_000);
        await Task.Delay(200);

        var result = await _tool.ExecuteAsync("c1", Args("output", pid: managed.Pid, tail: 2));
        var text = ResultText(result);

        // Should contain the last lines but not all of them
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().BeLessThanOrEqualTo(2);
    }

    // ───────────── kill ─────────────

    [Fact]
    public async Task Kill_TerminatesRunningProcess()
    {
        var managed = SpawnTestProcess("ping -n 60 127.0.0.1 >nul");
        await Task.Delay(300);

        managed.IsRunning.Should().BeTrue();

        var result = await _tool.ExecuteAsync("c1", Args("kill", pid: managed.Pid));
        var text = ResultText(result);

        text.Should().Contain("terminated");
        managed.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Kill_AlreadyExitedProcess_IsNoOp()
    {
        var managed = SpawnTestProcess("echo bye");
        managed.WaitForExit(5_000);

        var result = await _tool.ExecuteAsync("c1", Args("kill", pid: managed.Pid));
        var text = ResultText(result);

        text.Should().Contain("already exited");
    }
}
