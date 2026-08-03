using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR.Client;

namespace BotNexus.Gateway.Tests.Integration;

/// <summary>
/// Thrown when a SignalR test fixture operation (hub start, hub disposal) does not complete
/// within its declared bound. Carries the owning fixture and test names so a CI failure is
/// diagnosable from the assertion message alone (issue #2628, AC3).
/// </summary>
public sealed class HubFixtureTimeoutException : Exception
{
    public HubFixtureTimeoutException(string fixtureName, string testName, string operationName, TimeSpan timeout)
        : base($"Hub fixture '{fixtureName}' operation '{operationName}' for test '{testName}' did not complete within {timeout.TotalSeconds:0.###}s. " +
               "The SignalR test harness is hung or the hub is unreachable. This is a FAILING TEST rather than a stalled job by design (issue #2628); " +
               "inspect the named fixture/test rather than re-running CI.")
    {
        FixtureName = fixtureName;
        TestName = testName;
        OperationName = operationName;
        Timeout = timeout;
    }

    public string FixtureName { get; }

    public string TestName { get; }

    public string OperationName { get; }

    public TimeSpan Timeout { get; }
}

/// <summary>
/// Bounds SignalR integration-test fixture operations so a hung or unreachable hub fails the
/// affected test with a diagnosable message instead of stalling the CI job until the runner
/// cancels it and reports <c>Terminate orphan process: pid (NNNN) (dotnet)</c> (issue #2628).
/// </summary>
/// <remarks>
/// The bound is enforced two ways deliberately. The operation is handed a linked cancellation
/// token (co-operative path), and the wait itself races a timer (non-co-operative path). The
/// second is load-bearing: <see cref="HubConnection.StartAsync(CancellationToken)"/> observed on
/// CI did not honour its token, which is precisely how the harness deadlocked.
/// </remarks>
public static class HubFixtureGuard
{
    /// <summary>Default bound for establishing a hub connection in a test fixture.</summary>
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default bound for tearing a hub connection down in a test fixture.</summary>
    public static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runs <paramref name="operation"/> under a hard bound. Throws
    /// <see cref="HubFixtureTimeoutException"/> naming the fixture and test if the bound elapses,
    /// whether or not the operation honours the cancellation token it is given.
    /// </summary>
    public static async Task RunGuardedAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        string fixtureName,
        string testName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        var operationTask = operation(linked.Token);
        using var timerCts = new CancellationTokenSource();
        var timerTask = Task.Delay(timeout, timerCts.Token);

        var winner = await Task.WhenAny(operationTask, timerTask).ConfigureAwait(false);
        var completed = ReferenceEquals(winner, operationTask);

        if (!completed)
        {
            // Deliberately do not await operationTask: it is hung by definition. Observe its
            // eventual fault so it cannot surface as an unobserved TaskException and crash the
            // test host after the run (which is the orphan-process fingerprint in #2628).
            _ = operationTask.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            throw new HubFixtureTimeoutException(fixtureName, testName, operationName, timeout);
        }

        await timerCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HubFixtureTimeoutException(fixtureName, testName, operationName, timeout);
        }
    }

    /// <summary>
    /// Starts <paramref name="connection"/> under <see cref="DefaultStartTimeout"/> (or
    /// <paramref name="timeout"/>), failing the test diagnosably instead of hanging the job.
    /// </summary>
    public static Task StartGuardedAsync(
        HubConnection connection,
        string fixtureName,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(connection);
        return RunGuardedAsync(
            ct => connection.StartAsync(ct),
            nameof(HubConnection.StartAsync),
            fixtureName,
            testName,
            timeout ?? DefaultStartTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Disposes <paramref name="connection"/> under a hard bound so a fixture cannot keep the test
    /// host alive after the run completes (issue #2628, AC2).
    /// </summary>
    public static Task DisposeGuardedAsync(
        HubConnection connection,
        string fixtureName,
        TimeSpan? timeout = null,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(connection);
        return RunGuardedAsync(
            _ => connection.DisposeAsync().AsTask(),
            nameof(HubConnection.DisposeAsync),
            fixtureName,
            testName,
            timeout ?? DefaultDisposeTimeout,
            CancellationToken.None);
    }

    /// <summary>Stopwatch helper used by the guard's own tests to assert the bound is honoured.</summary>
    internal static Stopwatch StartStopwatch() => Stopwatch.StartNew();
}
