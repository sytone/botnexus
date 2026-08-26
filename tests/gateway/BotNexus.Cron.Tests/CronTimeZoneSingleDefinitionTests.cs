using System.IO;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Issue #2748's defining invariant is not "the resolver is correct" - it is "there is only ONE
/// resolver". The behavioural tests in <see cref="CronTimeZoneResolverTests"/> pin what
/// <c>CronTimeZoneResolver</c> does, but they cannot observe a call site that quietly keeps its own
/// second spelling: re-inlining the old <c>FindSystemTimeZoneById</c> body into
/// <c>CronScheduler.ResolveTimeZone</c> leaves every behavioural test green while restoring the exact
/// defect (the next-run computation disagreeing with the action that runs the job).
/// <para>
/// So this asserts the property directly against the source tree, derived by scanning rather than by
/// a hand-maintained list of call sites - a hand-maintained list IS the duplication being removed.
/// </para>
/// </summary>
public sealed class CronTimeZoneSingleDefinitionTests
{
    private const string ResolverFileName = "CronTimeZoneResolver.cs";

    [Fact]
    public void HostTimeZoneLookup_IsReferencedOnlyByTheCanonicalResolver()
    {
        var cronSource = LocateCronProjectSource();

        var offenders = Directory
            .EnumerateFiles(cronSource, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals(ResolverFileName, StringComparison.Ordinal))
            .Where(path => File.ReadLines(path).Any(IsHostLookupCall))
            .Select(path => Path.GetRelativePath(cronSource, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"Only {ResolverFileName} may call the host timezone database directly (#2748). " +
            $"Offending files: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Matches a real call, not a doc comment mentioning the API - otherwise the resolver's own
    /// <c>&lt;see cref&gt;</c> explanation of the bug would be indistinguishable from the bug.
    /// </summary>
    private static bool IsHostLookupCall(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("///", StringComparison.Ordinal) ||
            trimmed.StartsWith("*", StringComparison.Ordinal))
            return false;

        return trimmed.Contains("TimeZoneInfo.FindSystemTimeZoneById", StringComparison.Ordinal)
            || trimmed.Contains("TimeZoneInfo.TryConvertWindowsIdToIanaId", StringComparison.Ordinal)
            || trimmed.Contains("TimeZoneInfo.TryConvertIanaIdToWindowsId", StringComparison.Ordinal);
    }

    private static string LocateCronProjectSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            dir = dir.Parent;

        dir.ShouldNotBeNull("Could not locate the repository root (Directory.Packages.props) from the test output directory.");

        var cronSource = Path.Combine(dir!.FullName, "src", "gateway", "BotNexus.Cron");
        Directory.Exists(cronSource).ShouldBeTrue($"Expected the cron project source at {cronSource}.");
        return cronSource;
    }
}
