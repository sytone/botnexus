using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BotNexus.Architecture.Tests;

/// <summary>Proves the child startup environment independently of stochastic cache corruption.</summary>
public sealed class PowerShellStartupIsolationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Configure_ReplacesInheritedCacheInputsWithoutChangingParent()
    {
        var parentCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var parentModuleCache = Environment.GetEnvironmentVariable("PSModuleAnalysisCachePath");
        using var state = new DocsLintScriptTests.PowerShellStartupState();
        var start = new ProcessStartInfo("pwsh");
        start.Environment["XDG_CACHE_HOME"] = "inherited-cache";
        start.Environment["PSModuleAnalysisCachePath"] = "inherited-module-cache";
        start.Environment["UNRELATED_SENTINEL"] = "preserve";

        state.Configure(start);

        start.Environment["XDG_CACHE_HOME"].ShouldBe(
            OperatingSystem.IsWindows() ? "inherited-cache" : state.Root);
        start.Environment["PSModuleAnalysisCachePath"].ShouldBe(Path.Combine(state.Root, "ModuleAnalysisCache"));
        start.Environment["UNRELATED_SENTINEL"].ShouldBe("preserve");
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME").ShouldBe(parentCache);
        Environment.GetEnvironmentVariable("PSModuleAnalysisCachePath").ShouldBe(parentModuleCache);
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
    public async Task ConcurrentChildren_ReportActualPowerShellCacheRoots()
    {
        using var first = new DocsLintScriptTests.PowerShellStartupState();
        using var second = new DocsLintScriptTests.PowerShellStartupState();
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
            await Task.WhenAll(
                StopIfRunningAsync(a, startedA, cleanup.Token),
                StopIfRunningAsync(b, startedB, cleanup.Token)).WaitAsync(cleanup.Token);
        }
    }

    private static Process CreateProbe(DocsLintScriptTests.PowerShellStartupState state)
    {
        const string command = "[ordered]@{ cache = [System.Management.Automation.PSObject].Assembly.GetType('System.Management.Automation.Platform').GetField('CacheDirectory', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null); moduleCache = $env:PSModuleAnalysisCachePath; version = $PSVersionTable.PSVersion.ToString(); runtime = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription; executable = [Environment]::ProcessPath } | ConvertTo-Json -Compress; [Console]::ReadLine() | Out-Null";
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
