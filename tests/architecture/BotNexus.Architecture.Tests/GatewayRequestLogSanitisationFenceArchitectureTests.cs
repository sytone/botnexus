using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for clause 5 of #3260: inside
/// <c>src/gateway/BotNexus.Gateway.Api</c>, a request-derived value (<c>Request.Path</c> or
/// <c>Request.Method</c>) may only reach a logging or telemetry call through
/// <c>RequestLogText</c>.
/// </summary>
/// <remarks>
/// <para><b>Why a fence and not just a sweep.</b> The sweep removes today's five instances. It
/// does nothing about the next middleware, whose author writes
/// <c>_logger.LogWarning("... {Path}", context.Request.Path)</c> because that is what the rest
/// of the file used to look like, and ships a log-forgery path by omission. This is the exact
/// failure recorded in #3124 and #3151 - a guard applied at one site and absent at its siblings
/// - and it is why CodeQL flagged two NEW lines in PR #3259 while an identically-shaped
/// pre-existing line three lines above went unmentioned. A property that must hold at every
/// logging site cannot be maintained by independent decisions at each one.</para>
///
/// <para><b>The legitimate remedy is always the same:</b> wrap the value -
/// <c>RequestLogText.SafePath(context.Request.Path.Value)</c> for a path,
/// <c>RequestLogText.Safe(context.Request.Method)</c> for a method. Both escape control
/// characters into printable form and leave every legitimate value byte-for-byte identical, so
/// the remedy is never a behaviour regression and never loses evidence.</para>
///
/// <para><b>Scope is deliberately logging and telemetry, not every use.</b>
/// <c>GatewayAuthMiddleware</c> passes the raw path and method into <c>GatewayAuthContext</c>,
/// and <c>ShouldSkipAuth</c>/<c>RateLimitingMiddleware</c> match on the raw <c>PathString</c>.
/// Those are INPUTS to a decision, not output to a sink: escaping them would change what is
/// authenticated or rate-limited. A fence that forced sanitisation there would be demanding a
/// correctness defect, so it matches only calls whose target is a logger, an activity tag, or a
/// metric.</para>
///
/// <para>Source-text based, like <see cref="CliSafeDisplayFenceArchitectureTests"/>: "did this
/// call site wrap its argument" is a property of the source, and the compiled assembly retains
/// no trace of it - both spellings end up as a string argument.</para>
/// </remarks>
public sealed class GatewayRequestLogSanitisationFenceArchitectureTests : ArchitectureTest
{
    /// <summary>Root of the gateway API project this fence governs.</summary>
    private const string ApiRoot = "src/gateway/BotNexus.Gateway.Api";

    /// <summary>The one file permitted to spell out the neutralisation itself.</summary>
    private const string HelperSource = ApiRoot + "/RequestLogText.cs";

    /// <summary>
    /// A logging, activity-tag, or metric call - i.e. a sink that renders its arguments into
    /// the audit trail. Spans to the closing parenthesis of the statement so multi-line logger
    /// calls (the common formatting in this project) are inspected in full.
    /// </summary>
    private static readonly Regex SinkCall = new(
        @"(?:\b_?[Ll]ogger\s*\.\s*Log[A-Za-z]*|\bLogger\s*\.\s*Log[A-Za-z]*|\.\s*SetTag|\.\s*Add\s*\(\s*1\s*,)" +
        @"[\s\S]{0,900}?\);",
        RegexOptions.Compiled);

    /// <summary>
    /// A raw request-derived value: <c>context.Request.Path</c>, <c>request.Path.Value</c>,
    /// <c>context.Request.Method</c> and friends, in any spacing.
    /// </summary>
    private static readonly Regex RawRequestValue = new(
        @"\bRequest\s*\.\s*(?:Path|Method)\b",
        RegexOptions.Compiled);

    /// <summary>A use routed through the seam.</summary>
    private static readonly Regex SanitisedUse = new(
        @"\bRequestLogText\s*\.\s*Safe(?:Path)?\s*\(",
        RegexOptions.Compiled);


    [Fact]
    public void Helper_Exists()
    {
        var path = ResolvePath(HelperSource);
        File.Exists(path).ShouldBeTrue(
            "RequestLogText - the single definition of 'safe to put in a gateway log message' " +
            $"that every request-derived logging site depends on (#3260) - is missing. Expected at: {path}");

        var source = File.ReadAllText(path);
        source.ShouldContain("public static string Safe(",
            Case.Sensitive,
            "RequestLogText must expose Safe(string?) - the seam every call site composes through.");
        source.ShouldContain("public static string SafePath(",
            Case.Sensitive,
            "RequestLogText must expose SafePath(string?) - the path overload that also owns the " +
            "'/' fallback the call sites used to each spell for themselves.");
    }

    /// <summary>
    /// The fence proper: no logging or telemetry call in the gateway API may name a raw
    /// request-derived value without routing it through the seam.
    /// </summary>
    [Fact]
    public void NoRawRequestValue_ReachesALoggingOrTelemetryCall()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateApiSources())
        {
            var relative = ToRepoRelative(file);
            if (string.Equals(relative, HelperSource, StringComparison.OrdinalIgnoreCase))
                continue;

            var source = File.ReadAllText(file);
            foreach (Match sink in SinkCall.Matches(source))
            {
                var text = sink.Value;
                if (!RawRequestValue.IsMatch(text))
                    continue;

                // A sink call that names a request value is compliant only if every such value
                // in it is wrapped. Requiring at least as many wraps as raw mentions catches the
                // half-converted call that sanitises the path and forgets the method.
                var rawCount = RawRequestValue.Matches(text).Count;
                var safeCount = SanitisedUse.Matches(text).Count;
                if (safeCount < rawCount)
                {
                    offenders.Add($"{relative}: {Condense(text)}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "These gateway logging/telemetry sites pass a raw request-derived value:\n  " +
            string.Join("\n  ", offenders) +
            "\nRequest.Path and Request.Method are entirely caller-controlled; a CR/LF in either " +
            "forges an additional record in any sink that renders structured properties to plain " +
            "text, corrupting the audit trail a security reviewer relies on. " +
            "REMEDY: wrap the argument - RequestLogText.SafePath(context.Request.Path.Value) for a " +
            "path, RequestLogText.Safe(context.Request.Method) for a method. Both leave legitimate " +
            "values byte-for-byte identical, so this is never a behaviour change for real traffic. " +
            "See #3260 clauses 1, 2 and 5.");
    }

    /// <summary>
    /// The positive half of the fence: the enumerated #3260 call sites must actually route
    /// through the seam, so a file that is emptied, reverted, or renamed fails rather than
    /// passing vacuously by simply containing no matching sink call.
    /// </summary>
    [Fact]
    public void EveryConvertedCallSite_RoutesThroughTheSeam()
    {
        string[] convertedCallSites =
        [
            ApiRoot + "/GatewayAuthMiddleware.cs",
            ApiRoot + "/CorrelationIdMiddleware.cs",
            ApiRoot + "/RequestCancellationMiddleware.cs",
        ];

        foreach (var relative in convertedCallSites)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Expected gateway middleware source not found: {path}. If it was renamed, update " +
                "this list - do not delete the entry without confirming the logging seam is gone.");

            SanitisedUse.IsMatch(File.ReadAllText(path)).ShouldBeTrue(
                $"'{relative}' logs request-derived values but never calls RequestLogText, so its " +
                "audit output is no longer sanitised. See #3260 clause 2.");
        }
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsARawSiteAndDoesNotFlagTheRemedy()
    {
        const string offendingSite = """
            public sealed class NextMiddleware
            {
                public Task InvokeAsync(HttpContext context)
                {
                    _logger.LogWarning(
                        "Rejected {Method} {Path}.",
                        context.Request.Method,
                        context.Request.Path.Value ?? "/");
                    return Task.CompletedTask;
                }
            }
            """;

        var offendingSink = SinkCall.Match(offendingSite);
        offendingSink.Success.ShouldBeTrue(
            "Vacuity guard: a multi-line logger call MUST be recognised as a sink. If this fails " +
            "the fence inspects nothing and the next middleware reintroduces #3260 unnoticed.");
        RawRequestValue.Matches(offendingSink.Value).Count.ShouldBe(
            2,
            "Vacuity guard: both the raw Method and the raw Path must be detected.");
        SanitisedUse.Matches(offendingSink.Value).Count.ShouldBe(
            0,
            "Vacuity guard: an unwrapped site must register zero sanitised uses.");

        const string compliantSite = """
            public sealed class CompliantMiddleware
            {
                public Task InvokeAsync(HttpContext context)
                {
                    _logger.LogWarning(
                        "Rejected {Method} {Path}.",
                        RequestLogText.Safe(context.Request.Method),
                        RequestLogText.SafePath(context.Request.Path.Value));
                    return Task.CompletedTask;
                }
            }
            """;

        var compliantSink = SinkCall.Match(compliantSite);
        compliantSink.Success.ShouldBeTrue("Positive pin: the remedy must still parse as a sink call.");
        SanitisedUse.Matches(compliantSink.Value).Count.ShouldBeGreaterThanOrEqualTo(
            RawRequestValue.Matches(compliantSink.Value).Count,
            "Positive pin: the sanctioned remedy must NOT be flagged, otherwise correct code " +
            "cannot go green and authors will route around the fence.");

        // The half-converted call is the interesting failure: it looks fixed at a glance.
        const string halfConvertedSite = """
            _logger.LogWarning(
                "Rejected {Method} {Path}.",
                context.Request.Method,
                RequestLogText.SafePath(context.Request.Path.Value));
            """;

        var halfSink = SinkCall.Match(halfConvertedSite);
        halfSink.Success.ShouldBeTrue("Vacuity guard: the half-converted call must parse as a sink.");
        SanitisedUse.Matches(halfSink.Value).Count.ShouldBeLessThan(
            RawRequestValue.Matches(halfSink.Value).Count,
            "Vacuity guard: a call that sanitises the path but forgets the method MUST be " +
            "flagged. Counting wraps against raw mentions is the only reason this is caught.");

        // Whitespace evasion.
        RawRequestValue.IsMatch("context . Request . Path").ShouldBeTrue(
            "Vacuity guard: whitespace must not defeat the raw-value detector.");
    }

    /// <summary>
    /// Guards the scan itself (#3260 clause 5): a sweep that silently matches nothing must fail,
    /// not pass. Both the file count and the number of sink calls actually inspected are pinned,
    /// because a fence can go vacuous either by losing the source tree or by regressing the sink
    /// pattern until it matches no call at all.
    /// </summary>
    [Fact]
    public void Fence_InspectsANonTrivialNumberOfSites()
    {
        var files = EnumerateApiSources().ToList();
        files.Count.ShouldBeGreaterThan(
            20,
            "Vacuity guard: the gateway API source tree scanned by this fence is missing or " +
            $"nearly empty, so every assertion above would pass without inspecting anything. " +
            $"Check that '{ApiRoot}' still exists relative to the repo root.");

        var sinkCalls = files.Sum(file => SinkCall.Matches(File.ReadAllText(file)).Count);
        sinkCalls.ShouldBeGreaterThan(
            50,
            "Vacuity guard: the sink-call pattern matched almost nothing across the gateway API, " +
            "so NoRawRequestValue_ReachesALoggingOrTelemetryCall would pass while enforcing " +
            $"nothing. Only {sinkCalls} logging/telemetry calls were recognised. If logging was " +
            "genuinely refactored to a new spelling, update SinkCall - do not lower this bound.");
    }

    private static string Condense(string text)
    {
        var single = Regex.Replace(text, @"\s+", " ").Trim();
        return single.Length <= 160 ? single : single[..160] + "...";
    }

    private IEnumerable<string> EnumerateApiSources()
    {
        var apiRoot = Path.Combine(Repository.Root, ApiRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(apiRoot).ShouldBeTrue($"Gateway API source root not found: {apiRoot}");
        return Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private string ToRepoRelative(string absolutePath) =>
        Path.GetRelativePath(Repository.Root, absolutePath).Replace('\\', '/');

    private string ResolvePath(string relative) =>
        Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));

}
