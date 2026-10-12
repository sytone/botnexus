using System.Diagnostics;
using System.IO.Pipes;
using BotNexus.Cli.Services;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cli.Tests.Services;

// Owns the process from the launch boundary, not from the eventual StartAsync result/PID file.
// A unique pipe provides positive readiness and keeps the child alive until explicitly released.
internal sealed class ControlledReadinessChild : ILogger<GatewayProcessManager>, IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);
    private readonly string _pipeName = $"botnexus-readiness-{Guid.NewGuid():N}";
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<Process> _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<int> _ready;
    private Process? _process;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task<GatewayStartResult>? _startTask;
    private CancellationTokenSource? _startCancellation;

    internal ControlledReadinessChild()
    {
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _ready = ConnectAsync();
    }

    internal Task<int> Ready => _ready;
    internal bool HasExited => (_process ?? throw new InvalidOperationException("Child has not launched.")).HasExited;

    internal GatewayStartOptions Options(string home, TimeSpan? timeout = null, string? healthUrl = null) => new(
        ExecutablePath: Path.Combine(AppContext.BaseDirectory, "ControlledReadinessChild", "ControlledReadinessChild.dll"),
        Arguments: _pipeName,
        HomePath: home,
        HealthUrl: healthUrl,
        ReadinessTimeout: timeout);

    internal Task<GatewayStartResult> StartAsync(IHealthChecker healthChecker, GatewayStartOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_startTask is not null)
            throw new InvalidOperationException("Each fixture owns exactly one launch.");
        var manager = new GatewayProcessManager(healthChecker, this);
        _startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _startTask = manager.StartAsync(options, _startCancellation.Token);
        return _startTask;
    }

    internal async Task ExitAsync(int exitCode)
    {
        _ = await _ready.WaitAsync(Deadline);
        var writer = _writer ?? throw new InvalidOperationException("Child is not connected.");
        await writer.WriteLineAsync($"exit:{exitCode}");
        var process = await _launched.Task.WaitAsync(Deadline);
        await process.WaitForExitAsync().WaitAsync(Deadline);
        // This observer attached by PID; the manager's original launch handle owns the exit code.
        // The calling test asserts that exact code on the production result.
    }

    private async Task<int> ConnectAsync()
    {
        await _pipe.WaitForConnectionAsync(_lifetime.Token).WaitAsync(Deadline, _lifetime.Token);
        _reader = new StreamReader(_pipe, leaveOpen: true);
        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
        var line = await _reader.ReadLineAsync(_lifetime.Token).AsTask().WaitAsync(Deadline);
        var process = await _launched.Task.WaitAsync(Deadline, _lifetime.Token);
        line.ShouldBe($"ready:{process.Id}");
        process.HasExited.ShouldBeFalse();
        return process.Id;
    }

    /// <summary>Reaps the owned child even if readiness, assertions, or caller cancellation fail.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            // Drain launch first: StartAsync may still be reading a stale PID file when the
            // test fails. Otherwise it could launch AFTER we checked _process for cleanup.
            try { if (_startTask is not null) _ = await _startTask.WaitAsync(Deadline); }
            catch (Exception)
            {
                // The test observes the operation's failure; cleanup must still reap its child.
            }
            // Drain the handshake before disposing its streams or capturing the final handle.
            try { _ = await _ready; }
            catch (Exception)
            {
                // Cancellation/handshake failure does not release ownership of the child.
            }
            // Kill by the captured process handle only; never discover or signal by a stale PID file.
            if (_process is { } process)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The explicit exit handshake may have won the race with cleanup.
                }
                await process.WaitForExitAsync().WaitAsync(Deadline);
                process.HasExited.ShouldBeTrue();
            }
        }
        finally
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _pipe.Dispose();
            _process?.Dispose();
            _startCancellation?.Dispose();
            _lifetime.Dispose();
        }
    }

    /// <summary>Launch ownership is recorded synchronously before the manager writes its PID file.</summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> values &&
            values.Any(pair => pair.Key == "{OriginalFormat}" &&
                Equals(pair.Value, "Gateway process started with PID {Pid}")))
        {
            var pid = (int)(values.Single(pair => pair.Key == "Pid").Value
                ?? throw new InvalidOperationException("Launch log omitted the child PID."));
            _process = Process.GetProcessById(pid);
            _launched.SetResult(_process);
        }
    }

    /// <summary>All levels are enabled so the launch boundary cannot be filtered out.</summary>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <summary>No logging scope state is needed by this process-owning test observer.</summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
