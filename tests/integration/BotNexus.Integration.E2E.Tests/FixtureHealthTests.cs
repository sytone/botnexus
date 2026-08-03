using System.Text;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Guards the E2E harness itself (issue #2739).
///
/// Every other class in this collection begins with
/// <c>Skip.IfNot(_fx.Succeeded, ...)</c>, which means an infrastructure fault -
/// most notably the solution prebuild losing a race against a concurrently
/// running test host and dying with "Solution prebuild exit 1" - used to turn
/// the ENTIRE suite into ~265 silent skips while the runner still printed
/// "Passed!" and exited 0. A vacuous green is worse than a red: it hides the
/// fact that nothing was verified.
///
/// The tests below therefore ASSERT rather than skip. If fixture
/// initialization fails for any reason, these fail loudly and by name.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class FixtureHealthTests
{
    private readonly NewUserExperienceFixture _fx;

    public FixtureHealthTests(NewUserExperienceFixture fx) => _fx = fx;

    /// <summary>
    /// The load-bearing regression assertion. This must NEVER be converted to a
    /// SkippableFact: skipping is exactly the failure mode being prevented.
    /// Reverting the serialised prebuild in
    /// <c>NewUserExperienceFixture.EnsureSolutionBuiltAsync</c> reddens THIS test.
    /// </summary>
    [Fact]
    public void FixtureInitializationSucceeded()
    {
        if (_fx.Succeeded)
            return;

        var detail = new StringBuilder();
        detail.AppendLine(
            "E2E collection fixture initialization FAILED, so every other test in this " +
            "collection would have silently skipped (issue #2739). This is an assertion, " +
            "not a skip, precisely so an infrastructure fault cannot masquerade as a pass.");
        detail.AppendLine();
        detail.AppendLine($"Error: {_fx.Error ?? "(none recorded)"}");
        detail.AppendLine();
        detail.AppendLine("Fixture log:");
        foreach (var line in _fx.Log)
            detail.AppendLine($"  {line}");

        throw new Xunit.Sdk.XunitException(detail.ToString());
    }

    /// <summary>
    /// The prebuild race manifested specifically as "Solution prebuild exit 1"
    /// (CS2012 / MSB3883 file-lock contention between concurrent test hosts over
    /// the shared bin/Release tree). Name that reason explicitly so a regression
    /// reports the actual cause rather than a generic fixture failure.
    /// </summary>
    [Fact]
    public void FixtureDidNotFailOnConcurrentSolutionPrebuild()
    {
        var error = _fx.Error ?? string.Empty;
        if (!error.Contains("Solution prebuild exit", StringComparison.Ordinal))
            return;

        throw new Xunit.Sdk.XunitException(
            "The serialised solution prebuild (issue #2739) failed: concurrent E2E test " +
            "hosts raced the shared bin/Release outputs again, so this collection would " +
            "have degraded into skips.\nFixture error:\n" + error);
    }

    /// <summary>
    /// The gateway subprocess is the substrate for every Playwright test in the
    /// collection. Assert the provisioned sandbox is genuinely there so "no browser"
    /// and "no gateway" cannot be confused with each other.
    /// </summary>
    [Fact]
    public void FixtureExposedAReachableGateway()
    {
        FixtureInitializationSucceeded();
        _fx.GatewayPort.ShouldBeGreaterThan(0, "fixture never picked a gateway port");
        File.Exists(Path.Combine(_fx.Home, "config.json"))
            .ShouldBeTrue($"provisioned config.json missing under '{_fx.Home}'");
    }
}
