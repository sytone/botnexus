using System.Diagnostics;
using System.IO.Pipes;
using BotNexus.Cli.Commands;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

[Collection("AnsiConsole")]
public sealed class BuildOutputStreamerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    public static TheoryData<string, string> Errors => new()
    {
        { "error : Value cannot be null. (Parameter 'path1')", "" },
        { @"C:\Program Files\dotnet\sdk\NuGet.targets(782,5): error Value cannot be null. (Parameter 'path1')", "" },
        { "/repo with spaces/file.cs(12): error CS1001: missing name", "CS1001" },
        { "relative path/file.cs(12,4): error CS1002: missing semicolon", "CS1002" },
        { "CSC : error CS2001: missing input", "CS2001" },
        { "/repo/file.proj : error : restore failed", "" }
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void ProcessLine_DiagnosticForms_AreVisibleWithoutVerbose(string line, string code)
    {
        foreach (var stderr in new[] { false, true })
        {
            var state = new BuildOutputStreamer.BuildState();
            var output = Capture(() => BuildOutputStreamer.ProcessLine(line, state, false, stderr));
            state.ErrorCount.ShouldBe(1);
            state.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            output.ShouldContain(code.Length == 0 ? "error" : code);
            if (line.Contains("Value cannot", StringComparison.Ordinal))
                state.Diagnostics[0].Message.ShouldBe("Value cannot be null. (Parameter 'path1')");
        }
    }

    [Theory]
    [InlineData("0 Error(s)")]
    [InlineData("Build FAILED.")]
    [InlineData("This prose mentions error CS1001: not a diagnostic")]
    [InlineData("Restored /repo/error project.csproj")]
    [InlineData("All projects are up-to-date for restore.")]
    public void ProcessLine_NegativeForms_DoNotInventErrors(string line)
    {
        var state = new BuildOutputStreamer.BuildState();
        Capture(() => BuildOutputStreamer.ProcessLine(line, state, false, false));
        state.ErrorCount.ShouldBe(0);
    }

    [Fact]
    public void RenderSummary_UnknownFailure_RetainsBoundedContextFromBothStreams()
    {
        var state = new BuildOutputStreamer.BuildState();
        Capture(() =>
        {
            for (var index = 0; index < 100; index++)
                foreach (var stderr in new[] { false, true })
                    BuildOutputStreamer.ProcessLine($"tail-{index} [red] " + new string('x', 10000), state, false, stderr);
        });
        state.RecentOutput.Count.ShouldBeLessThanOrEqualTo(24);
        state.RecentOutput.ShouldAllBe(line => line.Length <= 530);
        var output = Capture(() => BuildOutputStreamer.RenderSummary(state, 1, false));
        output.ShouldContain("exit code 1");
        output.ShouldContain("No recognized error diagnostic");
        output.ShouldContain("stdout");
        output.ShouldContain("stderr");
        output.ShouldContain("tail-99 [red]");
        output.ShouldNotContain("0 error(s)");
        output.Length.ShouldBeLessThan(15000);
    }

    [Theory]
    [InlineData(false, 511)]
    [InlineData(false, 510)]
    [InlineData(true, 511)]
    [InlineData(true, 510)]
    public void ProcessLine_NonBmpAtRetentionBoundary_PreservesCompleteSurrogates(bool stderr, int prefixLength)
    {
        var state = new BuildOutputStreamer.BuildState();
        var prefix = new string('x', prefixLength);
        Capture(() => BuildOutputStreamer.ProcessLine(prefix + "\U0001F680tail", state, false, stderr));
        var expected = prefix + (prefixLength + 2 <= BuildOutputStreamer.MaxLineCharacters ? "\U0001F680" : string.Empty);
        state.RecentOutput.ShouldHaveSingleItem().ShouldBe((stderr ? "stderr: " : "stdout: ") + expected);
    }

    [Theory]
    [InlineData(false, false, 511)]
    [InlineData(false, true, 511)]
    [InlineData(true, false, 511)]
    [InlineData(true, true, 511)]
    [InlineData(false, false, 510)]
    [InlineData(false, true, 510)]
    [InlineData(true, false, 510)]
    [InlineData(true, true, 510)]
    public async Task ReadStreamAsync_NonBmpAtRetentionBoundary_PreservesCompleteSurrogates(bool stderr, bool newline, int prefixLength)
    {
        var state = new BuildOutputStreamer.BuildState();
        var prefix = new string('x', prefixLength);
        var content = prefix + "\U0001F680" + new string('y', 2048) + (newline ? "\r\n" : string.Empty);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        using var reader = new StreamReader(stream);
        await BuildOutputStreamer.ReadStreamAsync(reader, state, false, stderr, CancellationToken.None);
        var expected = prefix + (prefixLength + 2 <= BuildOutputStreamer.MaxLineCharacters ? "\U0001F680" : string.Empty);
        state.RecentOutput.ShouldHaveSingleItem().ShouldBe((stderr ? "stderr: " : "stdout: ") + expected);
    }

    [Fact]
    public void RenderSummary_SilentFailure_ExplainsMissingOutputAndSuccessStaysCompact()
    {
        var state = new BuildOutputStreamer.BuildState();
        Capture(() => BuildOutputStreamer.RenderSummary(state, 42, false)).ShouldContain("No output was captured");
        Capture(() => BuildOutputStreamer.ProcessLine("ordinary hidden output", state, false, true));
        var success = Capture(() => BuildOutputStreamer.RenderSummary(state, 0, false));
        success.ShouldContain("Build succeeded");
        success.ShouldNotContain("ordinary hidden output");
    }

    [Fact]
    public void ProcessLine_ConcurrentProducers_CountsAndNoticesAreConsistentAndBounded()
    {
        var state = new BuildOutputStreamer.BuildState();
        var output = Capture(() => Parallel.For(0, 1000, index =>
        {
            BuildOutputStreamer.ProcessLine("Determining projects to restore...", state, false, index % 2 == 0);
            BuildOutputStreamer.ProcessLine("a.cs(1,2): warning CS1000: warning [red]", state, false, false);
            BuildOutputStreamer.ProcessLine("a.cs(1): error CS2000: failure [red]", state, false, true);
            BuildOutputStreamer.ProcessLine("  Project -> output.dll", state, false, false);
        }));
        state.ErrorCount.ShouldBe(1000);
        state.WarningCount.ShouldBe(1000);
        state.ProjectsBuilt.ShouldBe(1000);
        state.Diagnostics.Count.ShouldBeLessThanOrEqualTo(100);
        output.Split("Restoring packages...").Length.ShouldBe(2);
        output.Split("Further warnings suppressed").Length.ShouldBe(2);
        output.ShouldContain("failure [red]");
    }

    [Fact]
    public async Task RunWithRetryAsync_LocksRetryExactlyOnceAndCancellationNeverRetries()
    {
        var attempts = new List<bool>();
        var result = await BuildOutputStreamer.RunWithRetryAsync((isolated, final) =>
        {
            attempts.Add(isolated);
            return Task.FromResult(BuildOutputStreamer.LockedFilesExitCode);
        }, CancellationToken.None);
        result.ShouldBe(1);
        attempts.ShouldBe(new[] { false, true });
        using var cancelled = new CancellationTokenSource();
        attempts.Clear();
        await Should.ThrowAsync<OperationCanceledException>(() => BuildOutputStreamer.RunWithRetryAsync((isolated, final) =>
        {
            attempts.Add(isolated);
            cancelled.Cancel();
            return Task.FromResult(75);
        }, cancelled.Token));
        attempts.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("silent-failure")]
    [InlineData("unknown-failure")]
    [InlineData("huge-line")]
    [InlineData("locked")]
    public async Task RunProcessAsync_FailedChild_PreservesBoundedEvidenceAndLockSentinel(string mode)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ControlledReadinessChild", "ControlledReadinessChild.dll"));
        info.ArgumentList.Add("build-output-" + mode);
        FillChildEnvironment(info);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var original = AnsiConsole.Console;
        using var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Interactive = InteractionSupport.No });
        try
        {
            var code = await BuildOutputStreamer.RunProcessAsync(info, false, deadline.Token, monitorLocks: true);
            code.ShouldBe(mode == "locked" ? 75 : 17);
            var output = writer.ToString();
            if (mode == "silent-failure")
                output.ShouldContain("No output was captured");
            if (mode == "unknown-failure")
            {
                output.ShouldContain("stdout: opaque stdout [red]");
                output.ShouldContain("stderr: opaque stderr [red]");
            }
            if (mode == "huge-line")
                output.Length.ShouldBeLessThan(15000);
            output.ShouldNotContain("0 error(s)");
        }
        finally { AnsiConsole.Console = original; }
    }

    [Fact]
    public async Task RunProcessAsync_PreCancelled_DoesNotSpawn()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => BuildOutputStreamer.RunProcessAsync(
            new ProcessStartInfo("must-not-be-started-5055"), false, cancelled.Token));
    }

    [Theory]
    [InlineData("silent")]
    [InlineData("continuous")]
    [InlineData("one-pipe")]
    [InlineData("exit-race")]
    [InlineData("descendant")]
    public async Task RunProcessAsync_Cancellation_ReapsOwnedRootAndSettlesReaders(string mode)
    {
        var pipeName = "bn-build-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancelled = new CancellationTokenSource();
        using var writerOutput = new StringWriter();
        var original = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writerOutput), Interactive = InteractionSupport.No });
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ControlledReadinessChild", "ControlledReadinessChild.dll"));
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("build-" + mode);
        FillChildEnvironment(info);
        int? rootPid = null;
        int? childPid = null;
        using var evidence = new OwnedProcessEvidence(mode);
        var run = BuildOutputStreamer.RunProcessAsync(info, false, cancelled.Token, pid =>
        {
            rootPid = pid;
            evidence.Observe("root", pid);
        });
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var ready = await reader.ReadLineAsync(deadline.Token);
            ready.ShouldNotBeNull();
            ready.ShouldStartWith("ready:");
            if (mode == "descendant")
            {
                var descendant = await reader.ReadLineAsync(deadline.Token);
                descendant.ShouldNotBeNull();
                childPid = int.Parse(descendant["child:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                evidence.Observe("child", childPid.Value);
            }
            evidence.Snapshot("ready; before caller cancellation", rootPid, childPid);
            evidence.Record("caller Cancel begin (production kill/wait is not instrumented)");
            cancelled.Cancel();
            evidence.Record("caller Cancel returned");
            if (mode == "exit-race")
            {
                try { await writer.WriteLineAsync("exit:0"); }
                catch (IOException) { } // Cancellation may already have reaped the child.
            }
            await Should.ThrowAsync<OperationCanceledException>(() => run.WaitAsync(deadline.Token));
            evidence.Snapshot("RunProcessAsync cancellation observed; after production cleanup", rootPid, childPid);
            rootPid.ShouldNotBeNull();
            HasExited(rootPid.Value).ShouldBeTrue(evidence.Describe("root assertion", rootPid, childPid));
            if (childPid is int ownedChild)
                HasExited(ownedChild).ShouldBeTrue(evidence.Describe("child assertion", rootPid, childPid));
            writerOutput.ToString().ShouldNotContain("Build succeeded");
        }
        finally
        {
            cancelled.Cancel();
            try
            {
                foreach (var pid in new[] { childPid, rootPid })
                {
                    if (pid is not int owned)
                        continue;
                    try
                    {
                        using var process = Process.GetProcessById(owned);
                        evidence.Snapshot($"teardown before kill/wait pid={owned}", rootPid, childPid);
                        if (!process.HasExited)
                        {
                            evidence.Record($"teardown Kill(tree) begin pid={owned}");
                            process.Kill(entireProcessTree: true);
                            evidence.Record($"teardown Kill(tree) returned pid={owned}; not proof of reaping");
                        }
                        evidence.Record($"teardown WaitForExitAsync begin pid={owned}");
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                        evidence.Snapshot($"teardown WaitForExitAsync completed pid={owned}", rootPid, childPid);
                        process.HasExited.ShouldBeTrue(evidence.Describe("teardown assertion", rootPid, childPid));
                    }
                    catch (ArgumentException) { evidence.Record($"teardown owned pid={owned} no longer found"); }
                }
                try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
            finally
            {
                AnsiConsole.Console = original;
                output.WriteLine(evidence.Describe("teardown finally", rootPid, childPid));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunProcessAsync_Cancellation_AwaitsDescendantConfirmationAfterRootAndPumpsSettle(bool confirmationFails)
    {
        var pipeName = "bn-build-wait-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancelled = new CancellationTokenSource();
        var rootExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpsSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descendantExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settledPumps = 0;
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ControlledReadinessChild", "ControlledReadinessChild.dll"));
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("build-silent");
        FillChildEnvironment(info);
        var run = BuildOutputStreamer.RunProcessAsync(info, false, cancelled.Token,
            readStream: async (reader, state, verbose, stderr, token) =>
            {
                try { await BuildOutputStreamer.ReadStreamAsync(reader, state, verbose, stderr, token); }
                finally
                {
                    if (Interlocked.Increment(ref settledPumps) == 2)
                        pumpsSettled.SetResult();
                }
            },
            terminateTree: async (root, token) =>
            {
                root.Kill(entireProcessTree: true);
                await root.WaitForExitAsync(token);
                rootExited.SetResult();
                // Model the evidenced ordering without a timing race: the root and readers
                // finish first, but the retained descendant exit signal is still pending.
                await descendantExited.Task.WaitAsync(token);
            });
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            (await reader.ReadLineAsync(deadline.Token)).ShouldStartWith("ready:");
            cancelled.Cancel();
            await Task.WhenAll(rootExited.Task, pumpsSettled.Task).WaitAsync(deadline.Token);
            run.IsCompleted.ShouldBeFalse("Root exit and settled pumps cannot stand in for descendant exit confirmation.");
            if (confirmationFails)
            {
                descendantExited.SetException(new IOException("Injected unresolved descendant exit."));
                var error = await Should.ThrowAsync<IOException>(() => run.WaitAsync(deadline.Token));
                error.Message.ShouldContain("descendant cleanup could not be confirmed");
            }
            else
            {
                descendantExited.SetResult();
                await Should.ThrowAsync<OperationCanceledException>(() => run.WaitAsync(deadline.Token));
            }
        }
        finally
        {
            cancelled.Cancel();
            descendantExited.TrySetResult();
            try { await run.WaitAsync(deadline.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunProcessAsync_PumpFaultWithLiveChild_EntersCleanupWithoutCallerCancellation(bool faultStderr)
    {
        var pipeName = "bn-build-fault-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cleanup = new CancellationTokenSource();
        var releaseFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new IOException("Injected build output pump failure.");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ControlledReadinessChild", "ControlledReadinessChild.dll"));
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("build-silent");
        FillChildEnvironment(info);
        Process? root = null;
        var run = BuildOutputStreamer.RunProcessAsync(info, false, cleanup.Token,
            pid => root = Process.GetProcessById(pid), readStream: async (reader, state, verbose, stderr, token) =>
            {
                if (stderr != faultStderr)
                {
                    await BuildOutputStreamer.ReadStreamAsync(reader, state, verbose, stderr, token);
                    return;
                }
                await releaseFault.Task.WaitAsync(token);
                throw expected;
            });
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            (await reader.ReadLineAsync(deadline.Token)).ShouldStartWith("ready:");
            root.ShouldNotBeNull();
            root.HasExited.ShouldBeFalse();
            releaseFault.SetResult();
            var actual = await Should.ThrowAsync<IOException>(() => run.WaitAsync(deadline.Token));
            actual.ShouldBeSameAs(expected);
            cleanup.IsCancellationRequested.ShouldBeFalse();
            await root.WaitForExitAsync(deadline.Token);
            root.HasExited.ShouldBeTrue();
        }
        finally
        {
            cleanup.Cancel();
            try
            {
                if (root is not null)
                {
                    if (!root.HasExited)
                        root.Kill(entireProcessTree: true);
                    await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    root.HasExited.ShouldBeTrue();
                }
                try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
            finally { root?.Dispose(); }
        }
    }

    [Fact]
    public async Task RunProcessAsync_RootExitsBeforeCancellationWithLiveDescendant_FailsCleanupClosed()
    {
        var pipeName = "bn-build-exited-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var descendantPipe = new NamedPipeServerStream(pipeName + "-descendant", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancelled = new CancellationTokenSource();
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ControlledReadinessChild", "ControlledReadinessChild.dll"));
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("build-root-exits-first");
        FillChildEnvironment(info);
        Process? root = null;
        Process? descendant = null;
        var run = BuildOutputStreamer.RunProcessAsync(info, false, cancelled.Token,
            pid => root = Process.GetProcessById(pid));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            (await reader.ReadLineAsync(deadline.Token)).ShouldStartWith("ready:");
            await descendantPipe.WaitForConnectionAsync(deadline.Token);
            using var descendantReader = new StreamReader(descendantPipe, leaveOpen: true);
            var ready = await descendantReader.ReadLineAsync(deadline.Token);
            ready.ShouldNotBeNull();
            ready.ShouldStartWith("ready:");
            descendant = Process.GetProcessById(int.Parse(ready["ready:".Length..], System.Globalization.CultureInfo.InvariantCulture));
            await writer.WriteLineAsync("exit:0");
            root.ShouldNotBeNull();
            await root.WaitForExitAsync(deadline.Token);
            root.HasExited.ShouldBeTrue(); // Establish exit FIRST, not a cancellation/exit race.
            descendant.HasExited.ShouldBeFalse();
            run.IsCompleted.ShouldBeFalse(); // Inherited stdout/stderr are still held open.
            cancelled.Cancel();
            var error = await Should.ThrowAsync<IOException>(() => run.WaitAsync(deadline.Token));
            error.Message.ShouldContain("descendant cleanup could not be confirmed");
            descendant.HasExited.ShouldBeFalse(); // The fixture is deliberately noncooperative with root exit.
        }
        finally
        {
            cancelled.Cancel();
            try
            {
                // A kill request is not teardown evidence. Confirm exit for every owned handle.
                foreach (var process in new[] { descendant, root })
                {
                    if (process is null)
                        continue;
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    process.HasExited.ShouldBeTrue();
                }
                try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
            finally
            {
                descendant?.Dispose();
                root?.Dispose();
            }
        }
    }

    // Test-only observations: event order is observer notification order, not kernel exit
    // order. In particular, neither a Z state nor a returned Kill proves a PID was reaped.
    private sealed class OwnedProcessEvidence(string mode) : IDisposable
    {
        private readonly object _gate = new();
        private readonly System.Text.StringBuilder _events = new();
        private readonly List<Process> _observers = [];
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _sequence;

        public void Record(string message)
        {
            lock (_gate)
                _events.AppendLine($"{++_sequence:D3} +{Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F3}ms {message}");
        }

        public void Observe(string role, int pid)
        {
            Record($"{role} owned pid={pid} reported");
            try
            {
                var process = Process.GetProcessById(pid);
                _observers.Add(process);
                process.Exited += (_, _) => Record($"{role} pid={pid} Exited notification (not proof of /proc removal)");
                process.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (IsObservationFailure(ex))
            {
                Record($"{role} pid={pid} exit observer unavailable: {ex.GetType().Name}: {ex.Message}");
            }
            SnapshotPid("identity at report", role, pid);
        }

        public void Snapshot(string phase, int? rootPid, int? childPid)
        {
            Record($"snapshot {phase}; mode={mode}; root={rootPid}; child={childPid}");
            if (rootPid is int root)
                SnapshotPid(phase, "root", root);
            if (childPid is int child)
                SnapshotPid(phase, "child", child);
        }

        public string Describe(string phase, int? rootPid, int? childPid)
        {
            Snapshot(phase, rootPid, childPid);
            lock (_gate)
                return "Owned-process evidence (observations are not atomic; compare start identities for PID reuse):\n" + _events;
        }

        private void SnapshotPid(string phase, string role, int pid)
        {
            var label = $"{phase}: {role} pid={pid}";
            if (OperatingSystem.IsLinux())
            {
                // Only these two bounded files for a PID supplied by this test's fixture.
                var stat = ReadProc(pid, "stat");
                Record($"{label} /proc/stat={stat.TrimEnd()}");
                var close = stat.LastIndexOf(')');
                if (close >= 0)
                {
                    var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length > 19)
                        Record($"{label} parsed state={fields[0]} ppid={fields[1]} starttime_ticks={fields[19]} (stat field 22)");
                }
                var status = ReadProc(pid, "status");
                var selected = status.Split('\n').Where(line =>
                    new[] { "Name:", "State:", "Tgid:", "Pid:", "PPid:", "NSpid:", "Threads:" }
                        .Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)));
                Record($"{label} /proc/status={string.Join("; ", selected)}; read-result={status.Length} chars");
                if (status.StartsWith("unavailable:", StringComparison.Ordinal))
                    Record($"{label} /proc/status {status}");
            }
            try
            {
                using var process = Process.GetProcessById(pid);
                var exited = process.HasExited;
                Record($"{label} Process.HasExited={exited}");
                Record($"{label} Process.StartTimeUtc={process.StartTime.ToUniversalTime():O}");
                if (exited)
                    Record($"{label} Process.ExitTimeUtc={process.ExitTime.ToUniversalTime():O} ExitCode={process.ExitCode}");
            }
            catch (Exception ex) when (IsObservationFailure(ex))
            {
                Record($"{label} Process observation unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string ReadProc(int pid, string file)
        {
            try
            {
                using var reader = new StreamReader($"/proc/{pid}/{file}");
                var buffer = new char[4096];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                return new string(buffer, 0, count) + (count == buffer.Length ? " [4096-character cap]" : string.Empty);
            }
            catch (Exception ex) when (IsObservationFailure(ex))
            {
                return $"unavailable: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static bool IsObservationFailure(Exception ex)
            => ex is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException;

        public void Dispose()
        {
            foreach (var process in _observers)
                process.Dispose();
        }
    }

    private static void FillChildEnvironment(ProcessStartInfo info)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!info.Environment.TryGetValue("PROCESSOR_ARCHITECTURE", out var architecture) || string.IsNullOrEmpty(architecture))
                info.Environment["PROCESSOR_ARCHITECTURE"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X64 => "AMD64",
                    System.Runtime.InteropServices.Architecture.X86 => "x86",
                    System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
                    _ => throw new InvalidOperationException("Unsupported process architecture.")
                };
            if (!info.Environment.TryGetValue("ProgramFiles", out var programFiles) || string.IsNullOrEmpty(programFiles))
                info.Environment["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!info.Environment.TryGetValue("ProgramFiles(x86)", out var programFilesX86) || string.IsNullOrEmpty(programFilesX86))
                info.Environment["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        }
    }

    private static bool HasExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static string Capture(Action action)
    {
        var original = AnsiConsole.Console;
        using var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Interactive = InteractionSupport.No });
        AnsiConsole.Profile.Width = 240;
        try { action(); return writer.ToString(); }
        finally { AnsiConsole.Console = original; }
    }
}
