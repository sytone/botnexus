using System.IO;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Issue #2810 clauses 4 and 5.
/// <para>
/// Clause 5 says the transition policy must be expressed in ONE place and that no site may
/// re-derive it. That is a property of the source tree, not of any behaviour: the seven
/// call sites this issue names all produced correct answers before the change, so no
/// behavioural test can observe an eighth site appearing with its own inline
/// <c>GetNextOccurrence</c> call. This scans instead, deriving the offender list rather than
/// maintaining one - a hand-maintained list of call sites IS the duplication being removed.
/// It is the same shape as <see cref="CronTimeZoneSingleDefinitionTests"/>, which pins the
/// #2748 single-resolver invariant.
/// </para>
/// </summary>
public sealed class CronNextRunSingleDefinitionTests
{
    private const string CalculatorFileName = "CronExpressionExtensions.cs";

    /// <summary>
    /// Clause 5. Only the extensions file may call Cronos' occurrence API directly.
    /// <para>
    /// The scan covers the gateway source tree, not just <c>BotNexus.Cron</c>, because the
    /// seventh call site found during the premise check lived in
    /// <c>BotNexus.Gateway.Api/Controllers/CronController.cs</c> - outside the project the
    /// #2748 fence scans, which is precisely why that controller was still carrying its own
    /// private copy of timezone resolution as well.
    /// </para>
    /// </summary>
    [Fact]
    public void CronosOccurrenceApi_IsCalledOnlyByTheCanonicalCalculator()
    {
        var gatewaySource = LocateGatewaySource();

        var offenders = Directory
            .EnumerateFiles(gatewaySource, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals(CalculatorFileName, StringComparison.Ordinal))
            .Where(path => File.ReadLines(path).Any(IsOccurrenceCall))
            .Select(path => Path.GetRelativePath(gatewaySource, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"Only {CalculatorFileName} may call Cronos' occurrence API (#2810 clause 5); every " +
            "other site must delegate so the DST-transition policy has exactly one definition. " +
            $"Offending files: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The positive half of clause 5, and the reason this is a pair rather than a single rule:
    /// a "nobody calls X" fence is trivially satisfied by a tree in which X is never called at
    /// all, including one where somebody deleted the calculator and hand-rolled arithmetic
    /// instead. This pins that the single permitted definition actually exists and actually
    /// calls the API it is supposed to own.
    /// </summary>
    [Fact]
    public void TheCanonicalCalculator_ActuallyCallsTheOccurrenceApi()
    {
        var calculator = Path.Combine(LocateGatewaySource(), "BotNexus.Cron", CalculatorFileName);

        File.Exists(calculator).ShouldBeTrue($"Expected the single next-run definition at {calculator}.");
        File.ReadLines(calculator).Count(IsOccurrenceCall).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Matches a DIRECT call to Cronos' occurrence API, not a doc comment naming it.
    /// <para>
    /// The comment filter is load-bearing rather than cosmetic: without it, the extensions' own
    /// explanation of the policy and this file's explanation of the fence would be
    /// indistinguishable from a violation.
    /// </para>
    /// <para>
    /// No exclusion is needed for compliant call sites, because the extensions deliberately use
    /// DIFFERENT verbs (<c>NextRun</c> / <c>NextRunUtc</c> / <c>RunsBetweenUtc</c>) from Cronos'
    /// <c>GetNextOccurrence</c>. Had they reused Cronos' name, a compliant
    /// <c>expression.GetNextOccurrence(now, tz)</c> would be textually identical to the raw call
    /// this fence forbids, and the fence would need an exclusion that any new violation could
    /// trivially adopt. Distinct verbs make the distinction structural instead.
    /// </para>
    /// </summary>
    private static bool IsOccurrenceCall(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("///", StringComparison.Ordinal) ||
            trimmed.StartsWith("*", StringComparison.Ordinal))
            return false;

        return trimmed.Contains(".GetNextOccurrence(", StringComparison.Ordinal)
            || trimmed.Contains(".GetOccurrences(", StringComparison.Ordinal);
    }

    private static string LocateGatewaySource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BotNexus.slnx")))
            dir = dir.Parent;

        dir.ShouldNotBeNull("Could not locate the repository root (BotNexus.slnx) from the test output directory.");

        var gatewaySource = Path.Combine(dir!.FullName, "src", "gateway");
        Directory.Exists(gatewaySource).ShouldBeTrue($"Expected the gateway source at {gatewaySource}.");
        return gatewaySource;
    }
}

/// <summary>
/// Issue #2810 clause 4: an unresolvable timezone id must produce a Warning naming BOTH the
/// job id and the id that failed.
/// <para>
/// The pre-existing warning (#2748) named only the timezone id. That tells an operator that
/// SOMETHING degraded to UTC but not WHICH job is now firing on the wrong hour, and this
/// instance runs ~20 jobs. The job id is the actionable half.
/// </para>
/// </summary>
public sealed class CronTimeZoneWarningJobIdTests
{
    [Fact]
    public void UnresolvableTimeZone_LogsAWarningNamingBothTheJobAndTheFailingId()
    {
        var logger = new CapturingLogger();

        var resolved = CronTimeZoneResolver.Resolve(
            "Not/AZone",
            _ => throw new TimeZoneNotFoundException(),
            logger,
            jobId: JobId.From("nightly-maintenance"));

        resolved.ShouldBe(TimeZoneInfo.Utc);

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.Contains("nightly-maintenance", StringComparison.Ordinal)
            .ShouldBeTrue($"The warning must name the job id so an operator knows which job degraded. Got: {warning.Message}");
        warning.Message.Contains("Not/AZone", StringComparison.Ordinal)
            .ShouldBeTrue($"The warning must name the failing timezone id (#2748). Got: {warning.Message}");
    }

    /// <summary>
    /// A call site that forgot to pass a job id must be visible as such, rather than rendering
    /// as an empty field that reads like a job with no id. Pinned so the placeholder is a
    /// deliberate contract and not an accident of the formatter.
    /// </summary>
    [Fact]
    public void UnresolvableTimeZone_WithoutAJobId_StillWarnsWithAnExplicitPlaceholder()
    {
        var logger = new CapturingLogger();

        CronTimeZoneResolver.Resolve("Not/AZone", _ => throw new TimeZoneNotFoundException(), logger);

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.Contains("(unspecified)", StringComparison.Ordinal).ShouldBeTrue(warning.Message);
    }

    /// <summary>
    /// Non-vacuity anchor: a resolvable id must not warn at all. Without this, a resolver that
    /// warned unconditionally would satisfy both assertions above for entirely the wrong reason.
    /// </summary>
    [Fact]
    public void ResolvableTimeZone_DoesNotWarn()
    {
        var logger = new CapturingLogger();
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fake/Zone", TimeSpan.FromHours(-8), "Fake", "Fake");

        CronTimeZoneResolver.Resolve("Fake/Zone", _ => zone, logger, jobId: JobId.From("nightly-maintenance"));

        logger.Entries.ShouldBeEmpty();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
