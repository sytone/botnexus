using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

// Mechanism diagnostics, not a post-completion timeout or cancellation-precedence contract.
public sealed class ShellToolTimeoutOwnershipTests
{
    private static readonly TimeSpan DiagnosticDeadline = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WhenTimeoutFiresWhileReadyChildIsAlive_ReturnsTimeout(bool perCallOverride)
    {
        await using var child = new SignalledChild();
        var clock = new ControlledExecutionTimeProvider();
        var tool = new ShellTool(defaultTimeoutSeconds: perCallOverride ? 10 : 1, shellPreference: ShellPreference.Pwsh)
        {
            ExecutionTimeProvider = clock,
            WaitForProcessExitAsync = async (process, token) =>
            {
                child.Track(process);
                await child.ReadyAsync();
                clock.TimerDueTime.ShouldBe(TimeSpan.FromSeconds(1));
                clock.TimerPeriod.ShouldBe(Timeout.InfiniteTimeSpan);
                clock.TimerCount.ShouldBe(1);
                process.HasExited.ShouldBeFalse();
                token.IsCancellationRequested.ShouldBeFalse();
                // Readiness and exit are separate: the child cannot finish until released.
                clock.Advance(TimeSpan.FromSeconds(1));
                token.IsCancellationRequested.ShouldBeFalse();
                process.HasExited.ShouldBeFalse();
                clock.Fire();
                token.IsCancellationRequested.ShouldBeTrue();
                process.HasExited.ShouldBeFalse();
                await process.WaitForExitAsync(token);
            }
        };
        using var safety = new CancellationTokenSource(DiagnosticDeadline);
        var result = await tool.ExecuteAsync("live-timeout", child.Arguments(perCallOverride), safety.Token);
        var details = result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>();
        details.TimedOut.ShouldBeTrue();
        details.ExitCode.ShouldBe(-1);
        details.IsError.ShouldBeTrue();
        result.Content[0].Value.ShouldContain("timed out after 1 seconds");
        await child.AssertExitedAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WhenChildCompletesBeforeExpiry_ReturnsNormalResult(bool perCallOverride)
    {
        await using var child = new SignalledChild();
        var clock = new ControlledExecutionTimeProvider();
        var tool = new ShellTool(defaultTimeoutSeconds: perCallOverride ? 10 : 1, shellPreference: ShellPreference.Pwsh)
        {
            ExecutionTimeProvider = clock,
            WaitForProcessExitAsync = async (process, token) =>
            {
                child.Track(process);
                await child.ReadyAsync();
                clock.TimerDueTime.ShouldBe(TimeSpan.FromSeconds(1));
                process.HasExited.ShouldBeFalse();
                await child.ReleaseAsync();
                await process.WaitForExitAsync(token);
                process.HasExited.ShouldBeTrue();
                token.IsCancellationRequested.ShouldBeFalse();
                clock.GetTimestamp().ShouldBe(0);
            }
        };
        using var safety = new CancellationTokenSource(DiagnosticDeadline);
        var result = await tool.ExecuteAsync("completed-before-expiry", child.Arguments(perCallOverride), safety.Token);
        var details = result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>();
        details.TimedOut.ShouldBeFalse();
        details.ExitCode.ShouldBe(0);
        details.IsError.ShouldBeFalse();
        result.Content[0].Value.ShouldContain("ownership-marker");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoTimeoutConfigured_DoesNotArmTimer()
    {
        await using var child = new SignalledChild();
        var clock = new ControlledExecutionTimeProvider();
        var tool = new ShellTool(defaultTimeoutSeconds: null, shellPreference: ShellPreference.Pwsh)
        {
            ExecutionTimeProvider = clock,
            WaitForProcessExitAsync = async (process, token) =>
            {
                child.Track(process);
                await child.ReadyAsync();
                process.HasExited.ShouldBeFalse();
                clock.TimerCount.ShouldBe(0);
                clock.TimerDueTime.ShouldBeNull();
                clock.Advance(TimeSpan.FromSeconds(1));
                token.IsCancellationRequested.ShouldBeFalse();
                await child.ReleaseAsync();
                await process.WaitForExitAsync(token);
            }
        };
        using var safety = new CancellationTokenSource(DiagnosticDeadline);
        var result = await tool.ExecuteAsync("no-timeout", child.Arguments(false), safety.Token);
        var details = result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>();
        details.TimedOut.ShouldBeFalse();
        details.ExitCode.ShouldBe(0);
        details.IsError.ShouldBeFalse();
        result.Content[0].Value.ShouldContain("ownership-marker");
        clock.TimerCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCancelsReadyLiveChildWithoutTimeout_ReturnsCancelledResult()
    {
        await using var child = new SignalledChild();
        using var caller = new CancellationTokenSource(DiagnosticDeadline);
        var clock = new ControlledExecutionTimeProvider();
        var tool = new ShellTool(defaultTimeoutSeconds: null, shellPreference: ShellPreference.Pwsh)
        {
            ExecutionTimeProvider = clock,
            WaitForProcessExitAsync = async (process, token) =>
            {
                child.Track(process);
                await child.ReadyAsync();
                process.HasExited.ShouldBeFalse();
                clock.TimerCount.ShouldBe(0);
                caller.Cancel();
                token.IsCancellationRequested.ShouldBeTrue();
                await process.WaitForExitAsync(token);
            }
        };
        var result = await tool.ExecuteAsync("caller-only", child.Arguments(false), caller.Token);
        var details = result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>();
        details.TimedOut.ShouldBeFalse();
        details.ExitCode.ShouldBe(-1);
        details.IsError.ShouldBeTrue();
        result.Content[0].Value.ShouldContain("cancelled");
        await child.AssertExitedAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadyChildWaitsBeforeTimerArm_ArmsFullDueTimeAfterThatInterval()
    {
        await using var child = new SignalledChild();
        var clock = new ControlledExecutionTimeProvider();
        var tool = new ShellTool(defaultTimeoutSeconds: 10, shellPreference: ShellPreference.Pwsh)
        {
            ExecutionTimeProvider = clock,
            BeforeTimeoutArmAsync = async process =>
            {
                child.Track(process);
                await child.ReadyAsync();
                process.HasExited.ShouldBeFalse();
                clock.TimerCount.ShouldBe(0);
                // Model a pre-arm scheduling interval without consuming wall-clock time.
                clock.Advance(TimeSpan.FromSeconds(2));
                clock.TimerCount.ShouldBe(0);
                process.HasExited.ShouldBeFalse();
            },
            WaitForProcessExitAsync = async (process, token) =>
            {
                clock.GetTimestamp().ShouldBe(TimeSpan.FromSeconds(2).Ticks);
                clock.TimerDueTime.ShouldBe(TimeSpan.FromSeconds(1));
                clock.TimerCount.ShouldBe(1);
                token.IsCancellationRequested.ShouldBeFalse();
                process.HasExited.ShouldBeFalse();
                await child.ReleaseAsync();
                await process.WaitForExitAsync(token);
            }
        };
        using var safety = new CancellationTokenSource(DiagnosticDeadline);
        var result = await tool.ExecuteAsync("pre-arm-interval", child.Arguments(true), safety.Token);
        var details = result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>();
        details.TimedOut.ShouldBeFalse();
        details.ExitCode.ShouldBe(0);
        details.IsError.ShouldBeFalse();
        result.Content[0].Value.ShouldContain("ownership-marker");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationBoundFixtureIsReadyBeforeTimerArm_ReturnsTimeoutAndKillsChild()
    {
        // GREEN counterpart of the finite-fixture RED: the same pre-arm gate now
        // observes readiness, not completion. No post-completion timeout rule is added.
        await using var child = new SignalledChild();
        using var safety = new CancellationTokenSource(DiagnosticDeadline);
        var clock = new ControlledExecutionTimeProvider();
        var readyBeforeArm = false;
        var tool = new ShellTool(defaultTimeoutSeconds: 10, shellPreference: ShellPreference.Pwsh)
        {
            ExecutionTimeProvider = clock,
            BeforeTimeoutArmAsync = async process =>
            {
                child.Track(process);
                await child.ReadyAsync();
                clock.TimerCount.ShouldBe(0);
                process.HasExited.ShouldBeFalse();
                readyBeforeArm = true;
            },
            WaitForProcessExitAsync = async (process, token) =>
            {
                readyBeforeArm.ShouldBeTrue();
                process.HasExited.ShouldBeFalse();
                clock.TimerCount.ShouldBe(1);
                clock.TimerDueTime.ShouldBe(TimeSpan.FromSeconds(1));
                clock.TimerPeriod.ShouldBe(Timeout.InfiniteTimeSpan);
                token.IsCancellationRequested.ShouldBeFalse();
                clock.Fire();
                token.IsCancellationRequested.ShouldBeTrue();
                await process.WaitForExitAsync(token);
            }
        };
        var result = await tool.ExecuteAsync("live-fixture-green", child.Arguments(true), safety.Token);
        var details = result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>();
        details.TimedOut.ShouldBeTrue();
        details.ExitCode.ShouldBe(-1);
        details.IsError.ShouldBeTrue();
        result.Content[0].Value.ShouldContain("timed out after 1 seconds");
        await child.AssertExitedAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WhenFiniteSleepFixtureExitsAndDrainsBeforeTimerArm_ReturnsNormalResultCharacterization()
    {
        // Preserve the observed RED mechanism as a normal-completion characterization.
        // The original finite fixture was invalid for a live-child timeout expectation;
        // the cause of historical failures remains unknown.
        await using var cleanup = new SignalledChild();
        using var safety = new CancellationTokenSource(DiagnosticDeadline);
        var clock = new ControlledExecutionTimeProvider();
        var exitedAndDrainedBeforeArm = false;
        var tool = new ShellTool(defaultTimeoutSeconds: 10)
        {
            ExecutionTimeProvider = clock,
            BeforeTimeoutArmAsync = async process =>
            {
                cleanup.Track(process);
                clock.TimerCount.ShouldBe(0);
                // WaitForExitAsync on the actual Process also awaits EOF for both redirected
                // asynchronous readers; no fabricated exit task or elapsed-time inference.
                await process.WaitForExitAsync(safety.Token).WaitAsync(DiagnosticDeadline);
                process.HasExited.ShouldBeTrue();
                process.ExitCode.ShouldBe(0);
                clock.TimerCount.ShouldBe(0);
                exitedAndDrainedBeforeArm = true;
            },
            WaitForProcessExitAsync = async (process, token) =>
            {
                exitedAndDrainedBeforeArm.ShouldBeTrue();
                process.HasExited.ShouldBeTrue();
                process.ExitCode.ShouldBe(0);
                clock.TimerCount.ShouldBe(1);
                clock.TimerDueTime.ShouldBe(TimeSpan.FromSeconds(1));
                clock.TimerPeriod.ShouldBe(Timeout.InfiniteTimeSpan);
                token.IsCancellationRequested.ShouldBeFalse();
                await process.WaitForExitAsync(token).WaitAsync(DiagnosticDeadline);
            }
        };
        var result = await tool.ExecuteAsync("finite-fixture-characterization", new Dictionary<string, object?>
        {
            ["command"] = "sleep 2",
            ["timeout"] = 1
        }, safety.Token);
        var details = result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>();
        exitedAndDrainedBeforeArm.ShouldBeTrue();
        details.ExitCode.ShouldBe(0);
        details.IsError.ShouldBeFalse();
        details.TimedOut.ShouldBeFalse();
        await cleanup.AssertExitedAsync();
        result.Content[0].Value.ShouldBeEmpty();
    }

    private sealed class SignalledChild : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private TcpClient? _connection;
        private Process? _tracked;

        public SignalledChild() => _listener.Start();

        public Dictionary<string, object?> Arguments(bool perCallOverride)
        {
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            // The socket provides an explicit readiness/release boundary, not a timed sleep.
            // ReadTimeout is a fail-safe: even loss of the test host cannot strand this child.
            var command = $"$c = [System.Net.Sockets.TcpClient]::new('127.0.0.1', {port}); " +
                "try { $s = $c.GetStream(); $s.ReadTimeout = 30000; " +
                "$s.WriteByte(1); if ($s.ReadByte() -ne 2) { throw 'missing release' }; " +
                "Write-Output 'ownership-marker' } finally { $c.Dispose() }";
            var arguments = new Dictionary<string, object?> { ["command"] = command };
            if (perCallOverride)
            {
                arguments["timeout"] = 1;
            }
            return arguments;
        }

        public void Track(Process process) => _tracked = Process.GetProcessById(process.Id);

        public async Task ReadyAsync()
        {
            _connection = await _listener.AcceptTcpClientAsync().WaitAsync(DiagnosticDeadline);
            var signal = new byte[1];
            var count = await _connection.GetStream().ReadAsync(signal).AsTask().WaitAsync(DiagnosticDeadline);
            count.ShouldBe(1);
            signal[0].ShouldBe((byte)1);
        }

        public async Task ReleaseAsync()
        {
            var connection = _connection ?? throw new InvalidOperationException("Child is not ready.");
            await connection.GetStream().WriteAsync(new byte[] { 2 }).AsTask().WaitAsync(DiagnosticDeadline);
        }

        public async Task AssertExitedAsync()
        {
            var process = _tracked ?? throw new InvalidOperationException("Child was not tracked.");
            await process.WaitForExitAsync().WaitAsync(DiagnosticDeadline);
            process.HasExited.ShouldBeTrue();
        }

        public async ValueTask DisposeAsync()
        {
            _connection?.Dispose();
            _listener.Stop();
            if (_tracked is { } process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    await process.WaitForExitAsync().WaitAsync(DiagnosticDeadline);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private sealed class ControlledExecutionTimeProvider : TimeProvider
    {
        private long _timestamp;
        private ControlledTimer? _timer;
        public TimeSpan? TimerDueTime { get; private set; }
        public TimeSpan? TimerPeriod { get; private set; }
        public int TimerCount { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCount++;
            TimerDueTime = dueTime;
            TimerPeriod = period;
            _timer = new ControlledTimer(callback, state);
            return _timer;
        }

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        public void Fire() => (_timer ?? throw new InvalidOperationException("Shell timeout timer was not armed.")).Fire();

        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
