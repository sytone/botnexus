using System.Diagnostics;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.ProcessTool;

namespace BotNexus.Extensions.ProcessTool.Tests;

public sealed class ProcessToolAdditionalTests : IDisposable
{
    private readonly ProcessManager _manager = new();
    private readonly ProcessTool _tool;
    private readonly List<ManagedProcess> _spawnedProcesses = [];

    public ProcessToolAdditionalTests()
    {
        _tool = new ProcessTool(_manager);
    }

    public void Dispose()
    {
        _manager.Clear();
        foreach (var process in _spawnedProcesses)
            process.Dispose();
    }

    [Fact]
    public async Task UnknownAction_ReturnsSupportedActions()
    {
        var result = await _tool.ExecuteAsync("call-1", Args("mystery"));

        Text(result).ShouldContain("Unknown action");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("STATUSS")]
    public async Task InvalidActionVariants_ReturnUnknownActionMessage(string action)
    {
        var result = await _tool.ExecuteAsync("call-1", Args(action));

        Text(result).ShouldContain("Unknown action");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("output")]
    [InlineData("input")]
    [InlineData("kill")]
    public async Task MissingPidForAction_ReturnsError(string action)
    {
        var result = await _tool.ExecuteAsync("call-1", Args(action));

        Text(result).ShouldContain("pid is required");
    }

    [Fact]
    public async Task Input_WithoutContent_ReturnsError()
    {
        var managed = SpawnTestProcess("more", "cat", redirectInput: true);

        var result = await _tool.ExecuteAsync("call-1", Args("input", pid: managed.Pid));

        Text(result).ShouldContain("content is required");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("output")]
    [InlineData("kill")]
    public async Task NonExistentPidActions_ReturnNoTrackedProcess(string action)
    {
        var result = await _tool.ExecuteAsync("call-1", Args(action, pid: 654321));

        Text(result).ShouldContain("No tracked process");
    }

    [Fact]
    public async Task Kill_DoubleKillReportsAlreadyExitedOnSecondCall()
    {
        var managed = SpawnTestProcess("ping -n 60 127.0.0.1 >nul", "sleep 60");

        var firstKill = await _tool.ExecuteAsync("call-1", Args("kill", pid: managed.Pid));
        var secondKill = await _tool.ExecuteAsync("call-2", Args("kill", pid: managed.Pid));

        Text(firstKill).ShouldContain("terminated");
        Text(secondKill).ShouldContain("already exited");
    }

    [Fact]
    public async Task Status_ReportsNonZeroExitCode()
    {
        var managed = SpawnTestProcess("cmd /c exit 3", "sh -lc 'exit 3'");
        managed.WaitForExit(5_000);

        var result = await _tool.ExecuteAsync("call-1", Args("status", pid: managed.Pid));

        Text(result).ShouldContain("Exit Code: 3");
    }

    [Fact]
    public async Task Output_CapturesStandardError()
    {
        var managed = SpawnTestProcess("echo err-line 1>&2", "echo err-line 1>&2");
        managed.WaitForExit(5_000);

        await WaitForOutputContainsAsync(managed.Pid, "err-line");
        var output = await _tool.ExecuteAsync("call-1", Args("output", pid: managed.Pid));

        Text(output).ShouldContain("err-line");
    }

    [Fact]
    public async Task Output_NoCapturedContent_ReturnsNoOutputMessage()
    {
        var managed = SpawnTestProcess("cd .", "true");
        managed.WaitForExit(5_000);

        var result = await _tool.ExecuteAsync("call-1", Args("output", pid: managed.Pid));

        Text(result).ShouldContain("No output captured");
    }

    [Fact]
    public async Task Status_WithTimeout_AllowsShortProcessToExit()
    {
        var managed = SpawnTestProcess("ping -n 2 127.0.0.1 >nul", "sleep 1");

        var result = await _tool.ExecuteAsync("call-1", Args("status", pid: managed.Pid, timeout: 4_000));

        Text(result).ShouldContain("Status: exited");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Output_WithNonPositiveTail_ReturnsFullOutput(int tail)
    {
        var managed = SpawnTestProcess("echo alpha & echo beta", "echo alpha; echo beta");
        managed.WaitForExit(5_000);
        await WaitForOutputContainsAsync(managed.Pid, "beta");

        var result = await _tool.ExecuteAsync("call-1", Args("output", pid: managed.Pid, tail: tail));

        var text = Text(result);
        text.ShouldContain("alpha");
        text.ShouldContain("beta");
    }

    [Fact]
    public async Task Output_TailGreaterThanLineCount_ReturnsAllLines()
    {
        var managed = SpawnTestProcess("echo line1 & echo line2", "echo line1; echo line2");
        managed.WaitForExit(5_000);
        await WaitForOutputContainsAsync(managed.Pid, "line2");

        var result = await _tool.ExecuteAsync("call-1", Args("output", pid: managed.Pid, tail: 1000));

        var text = Text(result);
        text.ShouldContain("line1");
        text.ShouldContain("line2");
    }

    [Fact]
    public async Task Execute_NullArguments_Throws()
    {
        var act = () => _tool.ExecuteAsync("call-1", null!);

        await act.ShouldThrowAsync<NullReferenceException>();
    }

    [Fact]
    public async Task PrepareArguments_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _tool.PrepareArgumentsAsync(new Dictionary<string, object?>(), cts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Status_WithStringPid_ParsesAndReturnsStatus()
    {
        var managed = SpawnTestProcess("echo done", "echo done");
        managed.WaitForExit(5_000);

        var args = new Dictionary<string, object?> { ["action"] = "status", ["pid"] = managed.Pid.ToString() };
        var result = await _tool.ExecuteAsync("call-1", args);

        Text(result).ShouldContain($"PID: {managed.Pid}");
    }

    [Fact]
    public async Task MultipleConcurrentCalls_AreSafe()
    {
        var managed = SpawnTestProcess("echo one & echo two & echo three", "echo one; echo two; echo three");
        managed.WaitForExit(5_000);
        await WaitForOutputContainsAsync(managed.Pid, "three");

        var tasks = Enumerable.Range(0, 30)
            .Select(i => (i % 3) switch
            {
                0 => _tool.ExecuteAsync($"list-{i}", Args("list")),
                1 => _tool.ExecuteAsync($"status-{i}", Args("status", pid: managed.Pid)),
                _ => _tool.ExecuteAsync($"output-{i}", Args("output", pid: managed.Pid, tail: 2))
            });

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(result => result.Content.Count > 0);
    }

    [Fact]
    public async Task KillDuringOutputRead_CompletesWithoutErrors()
    {
        var managed = SpawnTestProcess(
            "for /L %i in (1,1,200) do @echo line%i & @ping -n 2 127.0.0.1 >nul",
            "for i in $(seq 1 200); do echo line$i; sleep 0.05; done");

        var readTask = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await _tool.ExecuteAsync("read", Args("output", pid: managed.Pid, tail: 5));
                await Task.Delay(30);
            }
        });

        var killTask = _tool.ExecuteAsync("kill", Args("kill", pid: managed.Pid));

        await Task.WhenAll(readTask, killTask);
        managed.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task MultipleToolsUsingSameManager_ObserveSameProcess()
    {
        var secondTool = new ProcessTool(_manager);
        var managed = SpawnTestProcess("echo shared-process", "echo shared-process");
        managed.WaitForExit(5_000);
        await WaitForOutputContainsAsync(managed.Pid, "shared-process");

        var statusResult = await secondTool.ExecuteAsync("call-1", Args("status", pid: managed.Pid));
        var outputResult = await _tool.ExecuteAsync("call-2", Args("output", pid: managed.Pid));

        Text(statusResult).ShouldContain(managed.Pid.ToString());
        Text(outputResult).ShouldContain("shared-process");
    }

    [Fact]
    public async Task VeryLongCommandArguments_AreHandled()
    {
        var payload = new string('x', 8_000);
        var managed = SpawnTestProcess($"echo {payload}", $"echo {payload}");
        managed.WaitForExit(5_000);

        await WaitForOutputContainsAsync(managed.Pid, payload[..100]);
        var result = await _tool.ExecuteAsync("call-1", Args("output", pid: managed.Pid));

        Text(result).ShouldContain(payload[..100]);
    }

    [Fact]
    public async Task SpecialCharactersInCommand_AreHandled()
    {
        var token = "quotes-and-pipes-token";
        var managed = SpawnTestProcess(
            $"echo \"{token}\" ^| findstr {token}",
            $"printf '\"{token}\"\\n' | grep {token}");
        managed.WaitForExit(5_000);

        await WaitForOutputContainsAsync(managed.Pid, token);
        var result = await _tool.ExecuteAsync("call-1", Args("output", pid: managed.Pid));

        Text(result).ShouldContain(token);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Input_WithShellMetacharacters_WritesLiteralText()
    {
        var managed = SpawnTestProcess("more", "cat", redirectInput: true);
        var payload = "safe-text ; | ` $HOME \"quoted\"";

        var inputResult = await _tool.ExecuteAsync("call-1", Args("input", pid: managed.Pid, content: payload + Environment.NewLine));
        await WaitForOutputContainsAsync(managed.Pid, "safe-text");
        var outputResult = await _tool.ExecuteAsync("call-2", Args("output", pid: managed.Pid));

        Text(inputResult).ShouldContain("Sent");
        var outputText = Text(outputResult);
        outputText.ShouldContain("safe-text");
        outputText.ShouldContain("quoted");
    }

    [Theory]
    [InlineData("1;echo pwned")]
    [InlineData("1|echo pwned")]
    [InlineData("`1`")]
    [Trait("Category", "Security")]
    public async Task InjectionLikePidValues_AreRejected(string pidValue)
    {
        var args = new Dictionary<string, object?> { ["action"] = "kill", ["pid"] = pidValue };

        var result = await _tool.ExecuteAsync("call-1", args);

        Text(result).ShouldContain("pid is required");
    }

    private static IReadOnlyDictionary<string, object?> Args(string action, int? pid = null, string? content = null, int? tail = null, int? timeout = null)
    {
        var dict = new Dictionary<string, object?> { ["action"] = action };
        if (pid is not null) dict["pid"] = pid;
        if (content is not null) dict["content"] = content;
        if (tail is not null) dict["tail"] = tail;
        if (timeout is not null) dict["timeout"] = timeout;
        return dict;
    }

    private static string Text(AgentToolResult result) => string.Join("", result.Content.Select(content => content.Value));

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

    private async Task WaitForOutputContainsAsync(int pid, string expectedText)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            var result = await _tool.ExecuteAsync("call", Args("output", pid: pid));
            if (Text(result).Contains(expectedText, StringComparison.Ordinal))
                return;

            await Task.Delay(50);
        }

        var finalResult = await _tool.ExecuteAsync("call", Args("output", pid: pid));
        Text(finalResult).ShouldContain(expectedText);
    }
}

public sealed class ProcessManagerAndManagedProcessTests : IDisposable
{
    private readonly ProcessManager _manager = new();
    private readonly List<ManagedProcess> _spawnedProcesses = [];

    public void Dispose()
    {
        _manager.Clear();
        foreach (var process in _spawnedProcesses)
            process.Dispose();
    }

    [Fact]
    public void Kill_NonExistentPid_ReturnsFalse()
    {
        _manager.Kill(999999).ShouldBeFalse();
    }

    [Fact]
    public void Remove_UnknownPid_ReturnsFalse()
    {
        _manager.Remove(888888).ShouldBeFalse();
    }

    [Fact]
    public void Register_NullProcess_Throws()
    {
        var act = () => _manager.Register(1, null!);

        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void List_ReturnsRegisteredProcessInformation()
    {
        var running = SpawnManagedProcess("ping -n 60 127.0.0.1 >nul", "sleep 60");
        var exited = SpawnManagedProcess("echo done", "echo done");
        exited.WaitForExit(5_000);

        var items = _manager.List();

        items.Where(item => item.Pid == running.Pid && item.IsRunning).ShouldHaveSingleItem();
        items.Where(item => item.Pid == exited.Pid && item.ExitCode.HasValue).ShouldHaveSingleItem();
    }

    [Fact]
    public void Clear_DisposesTrackedProcesses()
    {
        var running = SpawnManagedProcess("ping -n 60 127.0.0.1 >nul", "sleep 60");

        _manager.Clear();

        running.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void WriteInput_AfterExit_ThrowsInvalidOperationException()
    {
        var process = SpawnManagedProcess("echo done", "echo done", redirectInput: true);
        process.WaitForExit(5_000);

        var act = () => process.WriteInput("hello");

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void WriteInput_AfterDispose_ThrowsObjectDisposedException()
    {
        var process = SpawnManagedProcess("more", "cat", redirectInput: true);
        process.Dispose();

        var act = () => process.WriteInput("hello");

        act.ShouldThrow<ObjectDisposedException>();
    }

    [Fact]
    public void Kill_AfterExit_DoesNotThrow()
    {
        var process = SpawnManagedProcess("echo done", "echo done");
        process.WaitForExit(5_000);

        var act = () => process.Kill();

        act.ShouldNotThrow();
    }

    [Fact]
    public void GetOutput_WithTailLines_ReturnsLastLines()
    {
        var process = SpawnManagedProcess("echo one & echo two & echo three", "echo one; echo two; echo three");
        process.WaitForExit(5_000);
        SpinWaitFor(() => process.GetOutput().Contains("three", StringComparison.Ordinal));

        var output = process.GetOutput(2);

        output.ShouldContain("three");
        output.ShouldNotContain("one");
    }

    [Fact]
    public void Dispose_KillsUnderlyingProcess()
    {
        var process = SpawnManagedProcess("ping -n 60 127.0.0.1 >nul", "sleep 60");

        process.IsRunning.ShouldBeTrue();
        process.Dispose();
        process.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void Kill_IsIdempotent()
    {
        var process = SpawnManagedProcess("ping -n 60 127.0.0.1 >nul", "sleep 60");

        process.Kill();
        Action second = () => process.Kill();

        second.ShouldNotThrow();
    }

    [Fact]
    public void ManagedProcess_CapturesBothOutputStreams()
    {
        var process = SpawnManagedProcess("echo out-line & echo err-line 1>&2", "echo out-line; echo err-line 1>&2");
        process.WaitForExit(5_000);
        SpinWaitFor(() => process.GetOutput().Contains("err-line", StringComparison.Ordinal));

        var output = process.GetOutput();
        output.ShouldContain("out-line");
        output.ShouldContain("err-line");
    }

    private ManagedProcess SpawnManagedProcess(string windowsCommand, string unixCommand, bool redirectInput = false)
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

    private static void SpinWaitFor(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (condition())
                return;

            Thread.Sleep(25);
        }
    }
}
