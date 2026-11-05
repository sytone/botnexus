using System.Diagnostics;
using System.Text;

namespace BotNexus.Architecture.Tests;

/// <summary>One deadline covers process exit and inherited output pipes; cleanup uncertainty is explicit.</summary>
internal enum ProbeProcessPhase { Readiness, Operation }

internal static class BoundedProbeProcess
{
    internal sealed record Result(int Exit, string Output, string Error);

    internal static async Task<Result> RunAsync(string directory, string[] arguments, TimeSpan timeout,
        CancellationToken fixtureToken = default, Func<Process, CancellationToken, Task>? readiness = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "HOME", "USERPROFILE", "TMP", "TEMP", "TMPDIR", "SystemRoot", "WINDIR", "COMSPEC",
            "PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_HOST_PATH",
            "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "DOTNET_CLI_HOME", "MSBuildSDKsPath", "APPDATA", "LOCALAPPDATA",
        };
        foreach (var key in process.StartInfo.Environment.Keys.ToArray())
            if (!allowed.Contains(key)) process.StartInfo.Environment.Remove(key);
        if (OperatingSystem.IsWindows())
        {
            if (!process.StartInfo.Environment.TryGetValue("PROCESSOR_ARCHITECTURE", out var architecture) || string.IsNullOrEmpty(architecture))
                process.StartInfo.Environment["PROCESSOR_ARCHITECTURE"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X64 => "AMD64",
                    System.Runtime.InteropServices.Architecture.X86 => "x86",
                    System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
                    _ => throw new InvalidOperationException("Unsupported Windows process architecture."),
                };
            foreach (var (key, folder) in new[] { ("ProgramFiles", Environment.SpecialFolder.ProgramFiles), ("ProgramFiles(x86)", Environment.SpecialFolder.ProgramFilesX86) })
                if (!process.StartInfo.Environment.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
                    process.StartInfo.Environment[key] = Environment.GetFolderPath(folder);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(fixtureToken);

        using var drains = new CancellationTokenSource();
        process.Start().ShouldBeTrue();
        var output = new StringBuilder();
        var error = new StringBuilder();
        var stdout = DrainAsync(process.StandardOutput, output, drains.Token);
        var stderr = DrainAsync(process.StandardError, error, drains.Token);
        var exit = process.WaitForExitAsync();
        var phase = readiness is null ? ProbeProcessPhase.Operation : ProbeProcessPhase.Readiness;
        var readinessCompleted = readiness is null;
        try
        {
            if (readiness is not null)
            {
                using var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(fixtureToken);
                readyDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                await readiness(process, readyDeadline.Token);
            }
            readinessCompleted = true;
            phase = ProbeProcessPhase.Operation;
            deadline.CancelAfter(timeout);
            // Root exit alone is insufficient: descendants can retain inherited output handles.
            await Task.WhenAll(exit, stdout, stderr).WaitAsync(deadline.Token);
            return new(process.ExitCode, output.ToString(), error.ToString());
        }
        catch (Exception primary)
        {
            var rootExited = process.HasExited;
            var outputComplete = stdout.IsCompletedSuccessfully && stderr.IsCompletedSuccessfully;
            var cleanupErrors = new List<string>();
            var uncertain = rootExited && !outputComplete;
            try
            {
                if (!rootExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception cleanupError) when (cleanupError is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                uncertain = true;
                cleanupErrors.Add("kill: " + cleanupError.Message);
            }
            // Cancellation closes our readers; it does NOT prove a descendant stopped.
            drains.Cancel();
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await Task.WhenAll(exit, ObserveDrainAsync(stdout), ObserveDrainAsync(stderr)).WaitAsync(cleanup.Token);
            }
            catch (Exception cleanupError)
            {
                uncertain = true;
                cleanupErrors.Add("bounded cleanup: " + cleanupError.Message);
            }
            throw new BoundedProbeProcessException(primary, rootExited, outputComplete, uncertain,
                output.ToString(), error.ToString(), cleanupErrors, phase, readinessCompleted);
        }
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder destination, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line) destination.AppendLine(line);
    }

    private static async Task ObserveDrainAsync(Task drain)
    {
        try { await drain; }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Retains the primary failure separately from bounded cleanup and descendant uncertainty.</summary>
internal sealed class BoundedProbeProcessException(Exception primary, bool rootExited, bool outputComplete,
    bool cleanupUncertain, string output, string error, IReadOnlyList<string> cleanupErrors, ProbeProcessPhase phase, bool readinessCompleted)
    : Exception($"Child {phase} failed: {primary}; rootExited={rootExited}; outputComplete={outputComplete}; " +
        $"cleanupUncertain={cleanupUncertain}; cleanup={string.Join("; ", cleanupErrors)}\nstdout:{output}\nstderr:{error}", primary)
{
    internal ProbeProcessPhase Phase { get; } = phase;
    internal bool ReadinessCompleted { get; } = readinessCompleted;
    internal string Output { get; } = output;
    internal bool RootExited { get; } = rootExited;
    internal bool OutputComplete { get; } = outputComplete;
    internal bool CleanupUncertain { get; } = cleanupUncertain;
}
