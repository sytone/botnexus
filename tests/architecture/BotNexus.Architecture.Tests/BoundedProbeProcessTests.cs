using System.Diagnostics;
using System.IO.Pipes;

namespace BotNexus.Architecture.Tests;

public sealed class BoundedProbeProcessTests
{
    [Fact]
    public async Task NormalOutput_CompletesRootAndBothDrains()
    {
        var result = await BoundedProbeProcess.RunAsync(AppContext.BaseDirectory,
            [Probe(), "process-lifetime", "normal"], TimeSpan.FromSeconds(15));
        result.Exit.ShouldBe(0);
        result.Output.ShouldContain("NORMAL_STDOUT");
        result.Error.ShouldContain("NORMAL_STDERR");
    }

    [Fact]
    public async Task RunningRoot_DeadlinePreservesPrimaryFailureAndBoundsCleanup()
    {
        var name = "probe-root-" + Guid.NewGuid().ToString("N");
        using var ready = Pipe(name);
        var nonce = Guid.NewGuid().ToString("N");
        var error = await Should.ThrowAsync<BoundedProbeProcessException>(() => BoundedProbeProcess.RunAsync(
            AppContext.BaseDirectory, [Probe(), "process-lifetime", "timeout", name, nonce], TimeSpan.FromSeconds(2),
            readiness: async (root, token) =>
            {
                await ready.WaitForConnectionAsync(token);
                var identity = await ReadIdentityAsync(ready, nonce, token);
                if (identity != root.Id) throw new InvalidOperationException("Root readiness nonce/PID mismatch.");
                await ready.WriteAsync(new byte[] { 1 }, token);
                await ready.FlushAsync(token);
            }));
        Console.WriteLine("SYNTHETIC_PRIMARY " + error);
        RequireOperationPhase(error);
        error.InnerException.ShouldBeAssignableTo<OperationCanceledException>(error.ToString());
        error.RootExited.ShouldBeFalse();
        error.OutputComplete.ShouldBeFalse();
        error.Message.ShouldContain("TIMEOUT_READY");
    }

    [Fact]
    public Task ExitedParent_WithChildHoldingPipe_DeadlineRemainsReachableAndReportsUncertainty() => HeldPipeScenario("normal");

    [Theory]
    [InlineData("assertion")]
    [InlineData("signal")]
    public Task HeldPipeOwnership_SurvivesAssertionOrSignalFailure(string fault) => HeldPipeScenario(fault);

    private static async Task HeldPipeScenario(string fault)
    {
        var name = "probe-pipe-" + Guid.NewGuid().ToString("N");
        using var release = Pipe(name);
        using var parentReady = Pipe(name + "-parent");
        var owner = Guid.NewGuid().ToString("N");
        Process? child = null;
        Exception? primary = null;
        var cleanupErrors = new List<Exception>();
        var joined = false;
        try
        {
            var error = await Should.ThrowAsync<BoundedProbeProcessException>(() => BoundedProbeProcess.RunAsync(
                AppContext.BaseDirectory, [Probe(), "process-lifetime", "parent", name, owner], TimeSpan.FromSeconds(3),
                readiness: async (root, token) =>
                {
                    await release.WaitForConnectionAsync(token);
                    var childPid = await ReadIdentityAsync(release, owner, token);
                    await parentReady.WaitForConnectionAsync(token);
                    var parentOwnedPid = await ReadIdentityAsync(parentReady, owner, token);
                    if (childPid != parentOwnedPid) throw new InvalidOperationException("Parent-owned child and nonce channel disagree.");
                    // Parent still owns its live Process handle until acknowledgement below.
                    child = Process.GetProcessById(parentOwnedPid);
                    await parentReady.WriteAsync(new byte[] { 1 }, token);
                    await parentReady.FlushAsync(token);
                    await root.WaitForExitAsync(token); // short deadline starts only after parent exit
                }));
            Console.WriteLine("HELD_PIPE_PRIMARY " + error);
            // Every intended scenario condition is established before fault injection.
            RequireHeldPipeOperation(error);
            if (fault == "assertion") throw new InvalidOperationException("Forced assertion failure after ownership.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            // Signaling cannot bypass the owned kill/join fallback, even if assertions failed.
            try
            {
                using var signalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (fault == "signal") throw new IOException("Forced release signaling failure.");
                if (release.IsConnected)
                {
                    await release.WriteAsync(new byte[] { 1 }, signalDeadline.Token);
                    await release.FlushAsync(signalDeadline.Token);
                }
            }
            catch (Exception error) { cleanupErrors.Add(error); }
            finally
            {
                if (child is not null)
                {
                    using (child)
                    {
                        try
                        {
                            // Explicitly owned synthetic process only, never arbitrary/global kill.
                            if (!child.HasExited) child.Kill(entireProcessTree: true);
                        }
                        catch (Exception error) { cleanupErrors.Add(error); }
                        finally
                        {
                            try
                            {
                                using var joinDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                                await child.WaitForExitAsync(joinDeadline.Token);
                                joined = true;
                            }
                            catch (Exception error) { cleanupErrors.Add(error); }
                        }
                    }
                }
            }
        }
        if (!joined)
            throw new AggregateException("Owned child termination unconfirmed; primary and cleanup retained.",
                new[] { primary }.OfType<Exception>().Concat(cleanupErrors));
        if (fault == "assertion")
        {
            primary.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("Forced assertion");
            cleanupErrors.ShouldBeEmpty();
        }
        else if (fault == "signal")
        {
            primary.ShouldBeNull();
            cleanupErrors.ShouldHaveSingleItem().ShouldBeOfType<IOException>().Message.ShouldContain("Forced release");
        }
        else
        {
            if (primary is not null || cleanupErrors.Count > 0)
                throw new AggregateException("Primary and cleanup failures retained.", new[] { primary }.OfType<Exception>().Concat(cleanupErrors));
        }
    }

    [Fact]
    public async Task ReadinessCancellation_CannotPassOperationOrInjectedFaultScenario()
    {
        using var forcedCancellation = new CancellationTokenSource();
        forcedCancellation.Cancel();
        var error = await Should.ThrowAsync<BoundedProbeProcessException>(() => BoundedProbeProcess.RunAsync(
            AppContext.BaseDirectory, [Probe(), "process-lifetime", "normal"], TimeSpan.FromSeconds(15),
            readiness: (_, _) => Task.FromCanceled(forcedCancellation.Token)));
        Console.WriteLine("FORCED_READINESS_PRIMARY " + error);
        error.Phase.ShouldBe(ProbeProcessPhase.Readiness);
        error.ReadinessCompleted.ShouldBeFalse();
        error.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        var operationRejected = Should.Throw<InvalidOperationException>(() => RequireOperationPhase(error));
        operationRejected.InnerException.ShouldBeSameAs(error);
        var injected = false;
        var heldRejected = Should.Throw<InvalidOperationException>(() =>
        {
            RequireHeldPipeOperation(error);
            injected = true;
            throw new InvalidOperationException("Forced assertion failure after ownership.");
        });
        heldRejected.InnerException.ShouldBeSameAs(error);
        injected.ShouldBeFalse("Wrong-phase cancellation must stop before any expected fault can be injected.");
    }

    private static void RequireOperationPhase(BoundedProbeProcessException error)
    {
        if (error.Phase != ProbeProcessPhase.Operation || !error.ReadinessCompleted)
            throw new InvalidOperationException("Expected completed readiness and operation-phase failure: " + error, error);
    }

    private static void RequireHeldPipeOperation(BoundedProbeProcessException error)
    {
        RequireOperationPhase(error);
        error.InnerException.ShouldBeAssignableTo<OperationCanceledException>(error.ToString());
        error.RootExited.ShouldBeTrue(error.ToString());
        error.OutputComplete.ShouldBeFalse(error.ToString());
        error.CleanupUncertain.ShouldBeTrue(error.ToString());
        error.Message.ShouldContain("PIPE_CHILD");
    }

    private static async Task<int> ReadIdentityAsync(Stream pipe, string nonce, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        var terminated = false;
        while (bytes.Count < 100)
        {
            if (await pipe.ReadAsync(one, token) != 1) throw new IOException("Readiness identity missing.");
            if (one[0] == (byte)'\n') { terminated = true; break; }
            bytes.Add(one[0]);
        }
        var parts = System.Text.Encoding.UTF8.GetString(bytes.ToArray()).Split(':');
        if (!terminated || parts.Length != 2 || parts[1] != nonce
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var processId))
            throw new InvalidOperationException("Invalid bounded readiness nonce/identity response.");
        return processId;
    }

    private static NamedPipeServerStream Pipe(string name) => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static string Probe()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "process-probe", "BotNexus.ExtensionDependencyProbe.dll");
        File.Exists(path).ShouldBeTrue("Build Architecture.Tests to copy the framework-only probe/runtimeconfig.");
        File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")).ShouldBeTrue();
        return path;
    }
}
