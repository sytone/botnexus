using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function: platform code emits metrics through the
/// <c>IMetrics</c> facade / the canonical <c>BotNexusMeters</c> scope, never by
/// newing up an ad-hoc <see cref="System.Diagnostics.Metrics.Meter"/>. A private
/// <c>new Meter("...")</c> creates an instrumentation scope that no exporter
/// subscribes to, so its measurements are silently dropped. Centralising on the
/// single <c>"BotNexus"</c> meter keeps every instrument observable.
/// </summary>
/// <remarks>
/// The fence scans tracked C# source under <c>src/</c> for <c>new Meter(</c>. The
/// only sanctioned construction sites live inside the telemetry project
/// (<c>BotNexusMeters</c> owns the canonical meter; <c>BotNexusMetrics</c> accepts an
/// injected meter for the facade), so those files are allowlisted by basename.
/// Test code is out of scope - tests legitimately create private meters plus
/// <c>MeterListener</c>s to observe instruments in isolation.
/// </remarks>
public sealed class MeterCentralizationArchitectureTests
{
    // Sanctioned meter construction sites, all inside BotNexus.Gateway.Telemetry.
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "BotNexusMeters.cs",   // owns the single canonical "BotNexus" meter
        "BotNexusMetrics.cs",  // facade; accepts an injected meter (default = canonical)
    };

    // Matches "new Meter(" with optional whitespace, catching ad-hoc scope creation.
    private static readonly Regex NewMeter = new(
        @"new\s+Meter\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void NoPlatformSource_ConstructsAdHocMeter()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var relative in EnumerateTrackedFiles(repoRoot))
        {
            var normalised = relative.Replace('\\', '/');
            if (!normalised.StartsWith("src/", StringComparison.Ordinal))
            {
                continue;
            }
            if (!normalised.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (AllowedFiles.Contains(Path.GetFileName(normalised)))
            {
                continue;
            }

            var absolute = Path.Combine(repoRoot, relative);
            if (!File.Exists(absolute))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(absolute);
            }
            catch (IOException)
            {
                continue;
            }

            if (NewMeter.IsMatch(content))
            {
                offenders.Add($"{normalised}: constructs an ad-hoc Meter - resolve IMetrics or use BotNexusMeters instead");
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        offenders.ShouldBeEmpty(
            "Platform source constructs an ad-hoc System.Diagnostics.Metrics.Meter. " +
            "Instruments on a private meter are dropped by exporters that only subscribe " +
            "to the canonical \"BotNexus\" scope. Resolve IMetrics from DI or use " +
            "BotNexusMeters. Only the telemetry project may construct a Meter.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static IEnumerable<string> EnumerateTrackedFiles(string repoRoot)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", "ls-files")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        string? line;
        while ((line = process.StandardOutput.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, "git ls-files failed: " + process.StandardError.ReadToEnd());
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return current.FullName;
    }
}
