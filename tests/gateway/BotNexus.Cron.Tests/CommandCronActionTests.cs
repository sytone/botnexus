using System.Diagnostics;
using BotNexus.Testing;
using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class CommandCronActionTests
{
    private readonly CommandCronAction _action = new();

    [Fact]
    public void ActionType_IsCommand()
    {
        Assert.Equal("command", _action.ActionType);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenShellCommandIsNull()
    {
        var context = BuildContext(shellCommand: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("ShellCommand is null or empty", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenShellCommandIsWhitespace()
    {
        var context = BuildContext(shellCommand: "   ");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("ShellCommand is null or empty", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulCommand_DoesNotThrow()
    {
        var context = BuildContext(shellCommand: "Write-Output 'hello'");

        // Should complete without exception
        await _action.ExecuteAsync(context);
    }

    [Fact]
    public async Task ExecuteAsync_FailingCommand_ThrowsWithExitCode()
    {
        var context = BuildContext(shellCommand: "exit 42");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("exited with code 42", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_TimedOutCommand_ThrowsTimeoutException()
    {
        // Command that sleeps longer than timeout
        var context = BuildContext(
            shellCommand: "Start-Sleep -Seconds 60",
            timeoutSeconds: 2);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("timed out after 2s", ex.Message);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesStdout()
    {
        var result = await CommandCronAction.RunProcessAsync(
            "Write-Output 'captured-output'",
            timeoutSeconds: 30,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("captured-output", result.Output);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesStderr()
    {
        var result = await CommandCronAction.RunProcessAsync(
            "Write-Error 'error-output'",
            timeoutSeconds: 30,
            CancellationToken.None);

        // Write-Error in pwsh exits with 0 but writes to stderr
        Assert.False(result.TimedOut);
        Assert.Contains("error-output", result.Output);
    }

    [Fact]
    public async Task RunProcessAsync_NonZeroExitCode()
    {
        var result = await CommandCronAction.RunProcessAsync(
            "exit 7",
            timeoutSeconds: 30,
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunProcessAsync_PreCancelledToken_ThrowsCancellationWithoutSpawning()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A start failure would win over cancellation if Process.Start were reached.
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CommandCronAction.RunProcessAsync(
                "Write-Output 'must-not-start'",
                timeoutSeconds: -1,
                cts.Token));
        exception.CancellationToken.ShouldBe(cts.Token);
    }

    [Fact]
    public void KillProcessTree_WhenTerminationCannotBeRequested_FailsClosed()
    {
        using var process = new Process();
        process.Dispose();

        var exception = Assert.Throws<InvalidOperationException>(
            () => CommandCronAction.KillProcessTree(process));

        Assert.Contains("process cleanup is incomplete", exception.Message);
    }

    [Fact]
    public async Task RunProcessAsync_CallerCancellation_StopsFixtureTreeAndDrainsRoot()
    {
        var signalDirectory = Path.Combine(Path.GetTempPath(), $"command-cron-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(signalDirectory);
        var fixturePath = Path.Combine(signalDirectory, "process-tree-fixture.ps1");
        await File.WriteAllTextAsync(fixturePath, ProcessTreeFixture);

        using var cts = new CancellationTokenSource();
        var command = $"& '{EscapePowerShellLiteral(fixturePath)}' -SignalDirectory '{EscapePowerShellLiteral(signalDirectory)}'";
        var runTask = CommandCronAction.RunProcessAsync(command, timeoutSeconds: 120, cts.Token);
        Process? parent = null;
        Process? child = null;

        try
        {
            await TestAwait.EventuallyAsync(
                () => File.Exists(Path.Combine(signalDirectory, "child.pid")) || runTask.IsCompleted,
                "the fixture descendant identity to be published", timeout: TimeSpan.FromSeconds(30));
            if (runTask.IsCompleted)
            {
                var earlyResult = await runTask;
                throw new InvalidOperationException($"Fixture exited before readiness: {earlyResult.ExitCode}: {earlyResult.Output}");
            }
            parent = GetVerifiedFixtureProcess(Path.Combine(signalDirectory, "parent.pid"));
            child = GetVerifiedFixtureProcess(Path.Combine(signalDirectory, "child.pid"));

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runTask.WaitAsync(TimeSpan.FromSeconds(10)));
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await TestAwait.EventuallyAsync(
                () => HasStopped(child), "the fixture descendant to stop executing",
                timeout: TimeSpan.FromSeconds(30));
            parent.HasExited.ShouldBeTrue();
            HasStopped(child).ShouldBeTrue();
        }
        finally
        {
            cts.Cancel();
            parent ??= TryGetProcess(signalDirectory, "parent.pid");
            child ??= TryGetProcess(signalDirectory, "child.pid");

            Exception? cleanupFailure = null;
            try
            {
                TryKillForCleanup(parent, ref cleanupFailure);
                TryKillForCleanup(child, ref cleanupFailure);
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is the expected outcome when the test exits before its assertion.
                }
                catch (Exception ex)
                {
                    cleanupFailure ??= ex;
                }

                await WaitForExitForCleanupAsync(parent, recordFailure: ex => cleanupFailure ??= ex);
                await WaitForExitForCleanupAsync(child, recordFailure: ex => cleanupFailure ??= ex);
            }
            finally
            {
                parent?.Dispose();
                child?.Dispose();
                try
                {
                    Directory.Delete(signalDirectory, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                    // Already cleaned up.
                }
                catch (Exception ex)
                {
                    cleanupFailure ??= ex;
                }
            }

            if (cleanupFailure is not null)
                throw new InvalidOperationException("Failed to clean up the command cancellation fixture.", cleanupFailure);
        }
    }

    private static Process? TryGetProcess(string signalDirectory, string fileName)
    {
        var path = Path.Combine(signalDirectory, fileName);
        if (!File.Exists(path))
            return null;

        try
        {
            return GetVerifiedFixtureProcess(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    private static void TryKillForCleanup(Process? process, ref Exception? cleanupFailure)
    {
        if (process is null)
            return;

        try
        {
            if (HasStopped(process))
                return;

            CommandCronAction.KillProcessTree(process);
        }
        catch (Exception ex)
        {
            cleanupFailure ??= ex;
        }
    }

    private static async Task WaitForExitForCleanupAsync(Process? process, Action<Exception> recordFailure)
    {
        if (process is null)
            return;

        try
        {
            await TestAwait.EventuallyAsync(
                () => HasStopped(process), "the fixture process to stop during cleanup",
                timeout: TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            recordFailure(ex);
        }
    }

    private static Process GetVerifiedFixtureProcess(string identityFile)
    {
        var identity = File.ReadAllText(identityFile).Split('|');
        var process = Process.GetProcessById(int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture));
        var observedGeneration = OperatingSystem.IsLinux()
            ? File.ReadAllText($"/proc/{process.Id}/stat").Split(')')[^1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[19]
            : process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (observedGeneration != identity[1])
        {
            process.Dispose();
            throw new InvalidOperationException("Fixture process identity changed; refusing PID-only cleanup.");
        }
        return process;
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("esrch")]
    public void HasStopped_ProcStatDisappears_ReturnsTrue(string failure)
    {
        Exception exception = failure switch
        {
            "file" => new FileNotFoundException(),
            "directory" => new DirectoryNotFoundException(),
            _ => new IOException("arbitrary localized message", 3)
        };
        HasStoppedCore(hasExited: false, isLinux: true, () => throw exception).ShouldBeTrue();
    }

    [Theory]
    [InlineData(5)] // EIO
    [InlineData(1)] // EPERM
    [InlineData(13)] // EACCES
    [InlineData(unchecked((int)0x80070003))] // Windows path-not-found HRESULT, not Linux ESRCH
    [InlineData(unchecked((int)0x80131620))] // Default IOException HRESULT
    public void HasStopped_OtherIoFailure_Propagates(int errorCode)
    {
        var exception = new IOException("No such process", errorCode);
        Should.Throw<IOException>(() => HasStoppedCore(false, true, () => throw exception))
            .ShouldBeSameAs(exception);
    }

    [Fact]
    public void HasStopped_PermissionFailure_Propagates()
    {
        var exception = new UnauthorizedAccessException("No such process");
        Should.Throw<UnauthorizedAccessException>(() => HasStoppedCore(false, true, () => throw exception))
            .ShouldBeSameAs(exception);
    }

    [Theory]
    [InlineData("123 (fixture) Z 1 2", true)]
    [InlineData("123 (fixture with ) parentheses) Z 1 2", true)]
    [InlineData("123 (fixture) R 1 2", false)]
    [InlineData("123 (fixture) S 1 2", false)]
    [InlineData("", false)]
    [InlineData("malformed", false)]
    [InlineData("123 (fixture)", false)]
    [InlineData("123 (fixture) ", false)]
    public void HasStopped_ProcStatState_OnlyZombieIsStopped(string stat, bool expected)
    {
        HasStoppedCore(false, true, () => stat).ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    public void HasStopped_ExitedOrNonLinux_DoesNotReadProcStat(bool hasExited, bool isLinux, bool expected)
    {
        HasStoppedCore(hasExited, isLinux,
            () => throw new InvalidOperationException("proc stat must not be read")).ShouldBe(expected);
    }

    [Fact]
    public void HasStopped_CurrentProcess_IsStillExecuting()
    {
        using var process = Process.GetCurrentProcess();
        HasStopped(process).ShouldBeFalse();
    }

    private static bool HasStopped(Process process)
    {
        process.Refresh();
        return HasStoppedCore(process.HasExited, OperatingSystem.IsLinux(),
            () => File.ReadAllText($"/proc/{process.Id}/stat"));
    }

    // A per-call read seam makes the post-refresh disappearance race deterministic in tests.
    // Only HasStopped supplies real process observations; identity verification stays separate.
    private static bool HasStoppedCore(bool hasExited, bool isLinux, Func<string> readStat)
    {
        if (hasExited)
            return true;
        if (!isLinux)
            return false;

        // Container PID 1 may not reap an orphan. A zombie cannot execute, but is not proof
        // of OS reaping. Keep that distinction explicit; restart ownership is outside this slice.
        try
        {
            var stat = readStat();
            var stateOffset = stat.LastIndexOf(')') + 2;
            return stateOffset > 1 && stateOffset < stat.Length && stat[stateOffset] == 'Z';
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException ex) when (ex.HResult == 3) // Linux ESRCH (no such process)
        {
            // The task can disappear after Refresh, including during an open proc stat read.
            // .NET's Unix IOErrors preserves raw errno in HResult; do not match localized text
            // or treat unrelated I/O/permission failures as evidence that execution stopped.
            return true;
        }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private const string ProcessTreeFixture = """
        param([Parameter(Mandatory)][string]$SignalDirectory, [switch]$Child)

        function Write-ProcessId([string]$Name) {
            $destination = Join-Path $SignalDirectory $Name
            $temporary = "$destination.tmp"
            $started = if ($IsLinux) {
                $stat = [System.IO.File]::ReadAllText("/proc/$PID/stat")
                $stat.Substring($stat.LastIndexOf(')') + 2).Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)[19]
            } else {
                [System.Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString([System.Globalization.CultureInfo]::InvariantCulture)
            }
            [System.IO.File]::WriteAllText($temporary, "$PID|$started")
            [System.IO.File]::Move($temporary, $destination)
        }

        if ($Child) {
            Write-ProcessId 'child.pid'
            while ($true) { Start-Sleep -Seconds 1 }
        }

        Write-ProcessId 'parent.pid'
        $pwsh = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $pwsh
        $startInfo.UseShellExecute = $false
        foreach ($argument in @('-NoProfile', '-File', $PSCommandPath, '-SignalDirectory', $SignalDirectory, '-Child')) {
            $startInfo.ArgumentList.Add($argument)
        }
        $descendant = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $descendant) { throw 'Fixture descendant did not start.' }
        while ($true) { Start-Sleep -Seconds 1 }
        """;

    [Fact]
    public async Task ExecuteAsync_MetadataTimeoutOverridesDefault()
    {
        // Use a very short timeout via metadata
        var metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = 1 };
        var context = BuildContext(
            shellCommand: "Start-Sleep -Seconds 60",
            metadata: metadata);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("timed out after 1s", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_StringMetadataTimeout_ParsedCorrectly()
    {
        var metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = "2" };
        var context = BuildContext(
            shellCommand: "Start-Sleep -Seconds 60",
            metadata: metadata);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("timed out after 2s", ex.Message);
    }

    private static CronExecutionContext BuildContext(
        string? shellCommand,
        int? timeoutSeconds = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var effectiveMetadata = metadata;
        if (timeoutSeconds.HasValue && metadata is null)
            effectiveMetadata = new Dictionary<string, object?> { ["timeoutSeconds"] = timeoutSeconds.Value };

        var job = new CronJob
        {
            Id = JobId.From("test-command-job"),
            Name = "Test Command",
            Schedule = "* * * * *",
            ActionType = "command",
            ShellCommand = shellCommand,
            Metadata = effectiveMetadata
        };

        // #2462: command firing now passes an authorization gate that fails closed without a
        // tool policy provider. These tests exercise process/timeout behaviour, so the fixture
        // supplies a permissive policy. Denial behaviour is covered in CommandCronAuthorizationTests.
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddSingleton<BotNexus.Gateway.Abstractions.Security.IToolPolicyProvider>(
                new PermissiveToolPolicyProvider())
            .BuildServiceProvider();

        return new CronExecutionContext
        {
            Job = job,
            RunId = RunId.From("run-1"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Manual,
            Services = services
        };
    }

    /// <summary>Allows everything; used so these process-behaviour tests are not gated (#2462).</summary>
    private sealed class PermissiveToolPolicyProvider
        : BotNexus.Gateway.Abstractions.Security.IToolPolicyProvider
    {
        public BotNexus.Gateway.Abstractions.Security.ToolRiskLevel GetRiskLevel(string toolName)
            => BotNexus.Gateway.Abstractions.Security.ToolRiskLevel.Safe;

        public bool RequiresApproval(string toolName, string? agentId = null) => false;

        public BotNexus.Gateway.Abstractions.Security.ToolApprovalFallback GetApprovalFallback(
            string toolName, string? agentId = null)
            => BotNexus.Gateway.Abstractions.Security.ToolApprovalFallback.Allow;

        public IReadOnlyList<string> GetDeniedForHttp() => [];
    }
}
