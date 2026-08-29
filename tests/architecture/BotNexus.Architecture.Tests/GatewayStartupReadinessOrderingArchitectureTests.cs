using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fence for issue #3660, second defect: no log statement may assert startup completion or
/// readiness before Kestrel is listening.
/// </summary>
/// <remarks>
/// <para>
/// <c>Program.cs</c> previously called <c>LogGatewayStartup</c> inline, seven lines and one
/// blocking hosted-services pass before <c>app.Run()</c>. During a slow start the log therefore
/// read "Gateway startup complete" while the port was still closed, which is why a 3.5-minute
/// startup stall was diagnosed as a crash and triggered a break-glass recovery session.
/// </para>
/// <para>
/// The fix registers the banner on <c>ApplicationStarted</c>, which fires after the server has
/// bound its addresses. This fence pins that ordering: the readiness banner must be emitted from
/// inside an <c>ApplicationStarted</c> registration, never from top-level startup code.
/// </para>
/// </remarks>
public sealed class GatewayStartupReadinessOrderingArchitectureTests : ArchitectureTest
{
    private const string ReadinessMessage = "Gateway startup complete";

    /// <summary>
    /// #3660 acceptance criterion 8: the readiness banner is emitted only after the application
    /// has started, so the log cannot claim readiness the gateway does not yet have.
    /// </summary>
    [Fact]
    public void ReadinessBanner_IsEmitted_OnlyAfterApplicationStarted()
    {
        var programPath = Repository.Path("src", "gateway", "BotNexus.Gateway.Api", "Program.cs");
        File.Exists(programPath).ShouldBeTrue(
            $"non-vacuity: the fence must point at a real file, but {programPath} is missing");

        var source = File.ReadAllText(programPath);

        // Non-vacuity: the readiness message must still exist, or this fence is guarding nothing.
        source.Contains(ReadinessMessage, StringComparison.Ordinal).ShouldBeTrue(
            "non-vacuity: the readiness banner text must still be present for this fence to mean anything");

        source.Contains("app.Lifetime.ApplicationStarted.Register", StringComparison.Ordinal).ShouldBeTrue(
            "#3660: the gateway startup banner must be registered on ApplicationStarted so it is " +
            "emitted after Kestrel has bound its addresses.");

        var registrationIndex = source.IndexOf("app.Lifetime.ApplicationStarted.Register", StringComparison.Ordinal);
        var runIndex = source.IndexOf("app.Run();", StringComparison.Ordinal);
        runIndex.ShouldBeGreaterThan(-1, "non-vacuity: app.Run() must be present in Program.cs");

        var bannerCallIndex = source.IndexOf("LogGatewayStartup(app", StringComparison.Ordinal);
        bannerCallIndex.ShouldBeGreaterThan(registrationIndex,
            "#3660: LogGatewayStartup must be invoked from inside the ApplicationStarted registration, " +
            "not from top-level startup code that runs before the server binds.");
        bannerCallIndex.ShouldBeLessThan(runIndex,
            "the ApplicationStarted registration must be installed before app.Run() or it will never fire");
    }

    /// <summary>
    /// #3660 acceptance criterion 8: no <em>other</em> readiness or completion claim sneaks back
    /// into the pre-bind section of startup. The fence scans everything above the
    /// <c>ApplicationStarted</c> registration for completion-flavoured log text.
    /// </summary>
    [Fact]
    public void NoCompletionClaim_IsLogged_BeforeTheApplicationStartedRegistration()
    {
        var programPath = Repository.Path("src", "gateway", "BotNexus.Gateway.Api", "Program.cs");
        var source = File.ReadAllText(programPath);
        var registrationIndex = source.IndexOf("app.Lifetime.ApplicationStarted.Register", StringComparison.Ordinal);
        registrationIndex.ShouldBeGreaterThan(-1);

        var preBind = source[..registrationIndex];

        // Comments are not log statements. The #3660 fix deliberately documents the old broken
        // behaviour by quoting the banner text, so scanning raw source would flag the explanation
        // of the fix as the defect. Strip comments first and assert against real code only.
        var preBindCode = StripComments(preBind);

        // Non-vacuity: the pre-bind region must actually contain logging, otherwise an empty
        // violation set would be trivially satisfied by there being nothing to scan.
        preBindCode.Contains("Log", StringComparison.Ordinal).ShouldBeTrue(
            "non-vacuity: the pre-bind startup region is expected to contain logging calls");

        var violations = CompletionClaimPattern.Matches(preBindCode)
            .Select(m => m.Value)
            .ToArray();

        violations.ShouldBeEmpty(
            "#3660: startup code that runs before Kestrel binds must not log a readiness or " +
            "completion claim — a slow start then reads as a crash for humans and for any monitor " +
            "grepping the log. Move the statement after the bind or rename it. Violations: " +
            string.Join(", ", violations));
    }

    /// <summary>
    /// Matches log message literals that assert the gateway has finished starting or is ready.
    /// "Gateway starting on ..." is intentionally not matched: it announces an intention, not a
    /// completed bind, and is useful precisely because it precedes the bind.
    /// </summary>
    private static readonly Regex CompletionClaimPattern = new(
        @"""[^""]*(?:startup complete|Gateway ready|ready to accept|now listening|is ready)[^""]*""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes line and block comments so the fence judges executable statements rather than the
    /// prose that documents them. Intentionally simple: <c>Program.cs</c> is top-level statements,
    /// and a comment marker appearing inside a string literal here would only ever cause the fence
    /// to inspect <em>more</em> text, never less, so it cannot open a hole.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
    }
}
