using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BotNexus.Architecture.Tests;

/// <summary>Proves the child startup environment independently of stochastic cache corruption.</summary>
public sealed class PowerShellStartupIsolationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task RunLintAt_StderrBeyondPipeCapacity_RetainsBothOutputs()
    {
        const int pipeSaturatingBytes = 131072;
        await ExerciseLintBoundaryAsync(
            $"[Console]::Error.Write(('E' * {pipeSaturatingBytes})); [Console]::Out.Write('stdout-complete'); exit 0",
            cancelAfterStart: false,
            expectedStdErrBytes: pipeSaturatingBytes);
    }

    [Fact]
    public async Task RunLintAt_NeverExitingChild_CancellationTerminatesOwnedProcess()
    {
        await ExerciseLintBoundaryAsync(
            "[Console]::Out.Write('ready-never-exit'); [Threading.Tasks.Task]::Delay(-1).GetAwaiter().GetResult()",
            cancelAfterStart: true);
    }

    [Fact]
    public async Task RunLintAt_DeadlineBeginsAfterReadinessBoundary()
    {
        var fixture = Directory.CreateTempSubdirectory("lint-readiness-").FullName;
        var script = Path.Combine(fixture, "child.ps1");
        var readyFile = Path.Combine(fixture, "ready");
        File.WriteAllText(script,
            $"param($RepoRoot, $Rule)\n[Console]::Out.Write('ready-never-exit'); [Console]::Out.Flush(); [IO.File]::WriteAllText('{readyFile.Replace("'", "''")}', 'ready'); [Threading.Tasks.Task]::Delay(-1).GetAwaiter().GetResult()");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReadiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(fixture, Path.GetFileName(readyFile))
        {
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, _) => ready.TrySetResult();
        Process? observed = null;
        try
        {
            var run = DocsLintScriptTests.RunLintAtAsync(fixture, script, "literal-drift", false,
                onStarted: async (child, _, token) =>
                {
                    observed = Process.GetProcessById(child.Id);
                    if (File.Exists(readyFile))
                    {
                        ready.TrySetResult();
                    }
                    await ready.Task.WaitAsync(token).WaitAsync(TimeSpan.FromSeconds(10));
                    await releaseReadiness.Task.WaitAsync(token);
                }, timeout: TimeSpan.FromMilliseconds(100));

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            run.IsCompleted.ShouldBeFalse("the execution deadline must not consume child startup time");
            releaseReadiness.TrySetResult();

            var failure = await Should.ThrowAsync<TimeoutException>(async () =>
                await run.WaitAsync(TimeSpan.FromSeconds(10)));
            failure.Message.ShouldContain("ready-never-exit");
            observed.ShouldNotBeNull();
            observed.HasExited.ShouldBeTrue();
        }
        finally
        {
            releaseReadiness.TrySetResult();
            if (observed is not null)
            {
                if (!observed.HasExited)
                {
                    observed.Kill(entireProcessTree: true);
                }
                await observed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                observed.Dispose();
            }
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static async Task ExerciseLintBoundaryAsync(
        string body,
        bool cancelAfterStart,
        int expectedStdErrBytes = 0)
    {
        var fixture = Directory.CreateTempSubdirectory("lint-boundary-").FullName;
        using var unrelated = new DocsLintScriptTests.PowerShellStartupState();
        var sentinel = Path.Combine(unrelated.Root, "untouched");
        File.WriteAllText(sentinel, "preserve");
        var script = Path.Combine(fixture, "child.ps1");
        File.WriteAllText(script, "param($RepoRoot, $Rule)\n" + body);
        using var cancellation = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? observed = null;
        string? cache = null;
        Task<DocsLintScriptTests.LintRun>? run = null;
        try
        {
            run = DocsLintScriptTests.RunLintAtAsync(fixture, script, "literal-drift", false,
                cancellation.Token, (child, root, _) =>
                {
                    observed = Process.GetProcessById(child.Id);
                    cache = root;
                    Directory.Exists(root).ShouldBeTrue("cache must remain owned while the child is live");
                    return Task.CompletedTask;
                }, timeout: TimeSpan.FromSeconds(10));
            if (cancelAfterStart)
            {
                var failure = await Should.ThrowAsync<TimeoutException>(async () =>
                    await run.WaitAsync(guard.Token));
                failure.Message.ShouldContain("ready-never-exit");
                failure.Message.ShouldContain("cache");
                // The outer guard is only emergency containment of the RED mutation.
                guard.IsCancellationRequested.ShouldBeFalse("the actual helper must enforce its deadline");
            }
            else
            {
                DocsLintScriptTests.LintRun? result = null;
                var failure = await Record.ExceptionAsync(async () => result = await run.WaitAsync(guard.Token));
                failure.ShouldBeNull("concurrent drains must complete the stderr flood without the emergency guard");
                result.ShouldNotBeNull();
                result.ExitCode.ShouldBe(0, result.StartupDiagnostics);
                result.StdOut.ShouldBe("stdout-complete");
                result.StdErr.ShouldBe(new string('E', expectedStdErrBytes));
            }

            observed.ShouldNotBeNull();
            observed.HasExited.ShouldBeTrue("helper completion must prove owned termination");
            cache.ShouldNotBeNull();
            Directory.Exists(cache).ShouldBeFalse();
            File.ReadAllText(sentinel).ShouldBe("preserve");
        }
        finally
        {
            // This independent guard safely kills a deliberately broken launcher in RED.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (observed is not null)
            {
                if (!observed.HasExited)
                {
                    observed.Kill(entireProcessTree: true);
                }
                await observed.WaitForExitAsync(cleanup.Token);
                observed.HasExited.ShouldBeTrue();
                observed.Dispose();
            }
            if (run is not null)
            {
                try { await run.WaitAsync(cleanup.Token); }
                catch (OperationCanceledException) when (!cleanup.IsCancellationRequested) { }
                catch (TimeoutException) when (cancelAfterStart && !cleanup.IsCancellationRequested)
                {
                    // The main assertion already checks this expected helper failure and diagnostics.
                }
            }
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Fact]
    public void Configure_ReplacesInheritedCacheInputsWithoutChangingParent()
    {
        var parentCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var parentModuleCache = Environment.GetEnvironmentVariable("PSModuleAnalysisCachePath");
        var parentMinimumCpuCount = Environment.GetEnvironmentVariable("DOTNET_MultiCoreJitMinNumCpus");
        using var state = new DocsLintScriptTests.PowerShellStartupState();
        var start = new ProcessStartInfo("pwsh");
        start.Environment["XDG_CACHE_HOME"] = "inherited-cache";
        start.Environment["PSModuleAnalysisCachePath"] = "inherited-module-cache";
        start.Environment["DOTNET_MultiCoreJitMinNumCpus"] = "2";
        start.Environment["UNRELATED_SENTINEL"] = "preserve";

        state.Configure(start);

        start.Environment["XDG_CACHE_HOME"].ShouldBe(
            OperatingSystem.IsWindows() ? "inherited-cache" : state.Root);
        start.Environment["PSModuleAnalysisCachePath"].ShouldBe(Path.Combine(state.Root, "ModuleAnalysisCache"));
        start.Environment["DOTNET_MultiCoreJitMinNumCpus"].ShouldBe(int.MaxValue.ToString());
        start.Environment["UNRELATED_SENTINEL"].ShouldBe("preserve");
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME").ShouldBe(parentCache);
        Environment.GetEnvironmentVariable("PSModuleAnalysisCachePath").ShouldBe(parentModuleCache);
        Environment.GetEnvironmentVariable("DOTNET_MultiCoreJitMinNumCpus").ShouldBe(parentMinimumCpuCount);
    }

    [Fact]
    public void Configure_TwoLaunchesHaveIndependentExistingCachePaths()
    {
        using var first = new DocsLintScriptTests.PowerShellStartupState();
        using var second = new DocsLintScriptTests.PowerShellStartupState();
        var a = new ProcessStartInfo("pwsh");
        var b = new ProcessStartInfo("pwsh");
        first.Configure(a);
        second.Configure(b);

        first.Root.ShouldNotBe(second.Root);
        Path.IsPathFullyQualified(first.Root).ShouldBeTrue();
        Directory.Exists(first.Root).ShouldBeTrue();
        Directory.Exists(second.Root).ShouldBeTrue();
        a.Environment.ShouldContainKey("PSModuleAnalysisCachePath");
        b.Environment.ShouldContainKey("PSModuleAnalysisCachePath");
        a.Environment["PSModuleAnalysisCachePath"].ShouldNotBe(b.Environment["PSModuleAnalysisCachePath"]);
        a.Environment["DOTNET_MultiCoreJitMinNumCpus"].ShouldBe(int.MaxValue.ToString());
        b.Environment["DOTNET_MultiCoreJitMinNumCpus"].ShouldBe(int.MaxValue.ToString());
        if (!OperatingSystem.IsWindows())
        {
            a.Environment.ShouldContainKey("XDG_CACHE_HOME");
            b.Environment.ShouldContainKey("XDG_CACHE_HOME");
            a.Environment["XDG_CACHE_HOME"].ShouldNotBe(b.Environment["XDG_CACHE_HOME"]);
        }
    }

    [Fact]
    public void Dispose_RemovesOnlyOwnedState()
    {
        using var sibling = new DocsLintScriptTests.PowerShellStartupState();
        var owned = new DocsLintScriptTests.PowerShellStartupState();
        File.WriteAllText(Path.Combine(owned.Root, "owned-cache"), "fixture");
        owned.Dispose();
        owned.Dispose();
        Directory.Exists(owned.Root).ShouldBeFalse();
        Directory.Exists(sibling.Root).ShouldBeTrue();
    }

    [Fact]
    public async Task ProbeCleanup_RetainsOnlyCacheWhoseTerminationFailed()
    {
        var first = new DocsLintScriptTests.PowerShellStartupState();
        var second = new DocsLintScriptTests.PowerShellStartupState();
        using var a = new Process();
        using var b = new Process();
        var attempted = new System.Collections.Concurrent.ConcurrentBag<Process>();
        var bothAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cleanup = CleanupProbesAsync(
            [(a, true, first), (b, true, second)],
            async (process, _, token) =>
            {
                attempted.Add(process);
                if (attempted.Count == 2)
                {
                    bothAttempted.TrySetResult();
                }
                await release.Task.WaitAsync(token);
                if (process == b)
                {
                    throw new InvalidOperationException("fixture termination failure");
                }
            });

        await bothAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cleanup.IsCompleted.ShouldBeFalse("both termination attempts must begin before either is awaited");
        release.TrySetResult();
        var failure = await Should.ThrowAsync<AggregateException>(async () => await cleanup);

        attempted.ShouldBe([a, b], ignoreOrder: true);
        Directory.Exists(first.Root).ShouldBeFalse("confirmed termination permits owned cache cleanup");
        Directory.Exists(second.Root).ShouldBeTrue("failed termination must retain owned cache state");
        failure.Message.ShouldContain(second.Root);
        second.Dispose();
    }

    [Fact]
    public async Task ConcurrentChildren_ReportActualPowerShellCacheRoots()
    {
        var first = new DocsLintScriptTests.PowerShellStartupState();
        var second = new DocsLintScriptTests.PowerShellStartupState();
        using var a = CreateProbe(first);
        using var b = CreateProbe(second);
        // Safety deadline only: success depends on child observations, never elapsed time.
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = safety.Token;
        var startedA = false;
        var startedB = false;
        try
        {
            startedA = a.Start();
            startedA.ShouldBeTrue();
            startedB = b.Start();
            startedB.ShouldBeTrue();
            var errorA = a.StandardError.ReadToEndAsync(token);
            var errorB = b.StandardError.ReadToEndAsync(token);
            // Both children wait on stdin after reporting actual startup state. No sleep or retry oracle.
            var lines = await Task.WhenAll(
                a.StandardOutput.ReadLineAsync(token).AsTask(),
                b.StandardOutput.ReadLineAsync(token).AsTask()).WaitAsync(token);
            await a.StandardInput.WriteLineAsync("release".AsMemory(), token).WaitAsync(token);
            await b.StandardInput.WriteLineAsync("release".AsMemory(), token).WaitAsync(token);
            await Task.WhenAll(a.WaitForExitAsync(token), b.WaitForExitAsync(token)).WaitAsync(token);
            var errors = await Task.WhenAll(errorA, errorB).WaitAsync(token);
            a.ExitCode.ShouldBe(0, errors[0]);
            b.ExitCode.ShouldBe(0, errors[1]);
            AssertProbe(lines[0], first);
            AssertProbe(lines[1], second);
        }
        finally
        {
            // A cancelled protocol token must not cancel cleanup. Start both cleanup attempts
            // before awaiting either, so one failure cannot strand the other child.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await CleanupProbesAsync(
                [(a, startedA, first), (b, startedB, second)],
                StopIfRunningAsync, cleanup.Token).WaitAsync(cleanup.Token);
        }
    }

    private static async Task CleanupProbesAsync(
        IReadOnlyList<(Process Process, bool Started, DocsLintScriptTests.PowerShellStartupState State)> probes,
        Func<Process, bool, CancellationToken, Task> stop,
        CancellationToken token = default)
    {
        var outcomes = await Task.WhenAll(probes.Select(async probe =>
        {
            try
            {
                await stop(probe.Process, probe.Started, token);
                probe.State.Dispose();
                return (probe.State.Root, Error: (Exception?)null);
            }
            catch (Exception error)
            {
                return (probe.State.Root, Error: error);
            }
        }));
        var failures = outcomes
            .Where(outcome => outcome.Error is not null)
            .Select(outcome => new InvalidOperationException(
                $"PowerShell probe cleanup failed; cache retained: {outcome.Root}", outcome.Error))
            .ToArray();
        if (failures.Length > 0)
        {
            throw new AggregateException(failures);
        }
    }

    private static Process CreateProbe(DocsLintScriptTests.PowerShellStartupState state)
    {
        const string command = "[ordered]@{ cache = [System.Management.Automation.PSObject].Assembly.GetType('System.Management.Automation.Platform').GetField('CacheDirectory', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null); moduleCache = $env:PSModuleAnalysisCachePath; minimumCpuCount = $env:DOTNET_MultiCoreJitMinNumCpus; version = $PSVersionTable.PSVersion.ToString(); runtime = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription; executable = [Environment]::ProcessPath } | ConvertTo-Json -Compress; [Console]::ReadLine() | Out-Null";
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        state.Configure(start);
        return new Process { StartInfo = start };
    }

    private void AssertProbe(string? line, DocsLintScriptTests.PowerShellStartupState state)
    {
        line.ShouldNotBeNullOrWhiteSpace();
        using var document = JsonDocument.Parse(line);
        output.WriteLine("PowerShell startup probe: " + line);
        var root = document.RootElement;
        root.GetProperty("moduleCache").GetString().ShouldBe(Path.Combine(state.Root, "ModuleAnalysisCache"));
        root.GetProperty("minimumCpuCount").GetString().ShouldBe(int.MaxValue.ToString());
        root.GetProperty("version").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("runtime").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("executable").GetString().ShouldNotBeNullOrWhiteSpace();
        if (!OperatingSystem.IsWindows())
        {
            root.GetProperty("cache").GetString().ShouldBe(Path.Combine(state.Root, "powershell"));
        }
    }

    private static async Task StopIfRunningAsync(Process process, bool started, CancellationToken token)
    {
        if (!started)
        {
            return;
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        // Failure to terminate is a test failure, not an unbounded wait or a swallowed error.
        await process.WaitForExitAsync(token).WaitAsync(token);
    }
}
