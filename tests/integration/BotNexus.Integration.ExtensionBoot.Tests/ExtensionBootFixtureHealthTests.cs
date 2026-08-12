namespace BotNexus.Integration.ExtensionBoot.Tests;

/// <summary>
/// Guards the extension-boot harness itself (issue #2491, acceptance criterion 4).
/// </summary>
/// <remarks>
/// <para>
/// Every test in <see cref="ExtensionBootSmokeTests"/> opens with
/// <c>Skip.If(ShouldSkip(), SkipReason())</c>, where <c>ShouldSkip()</c> is
/// <c>!_fx.Succeeded</c>. That is the same mass-vacuity shape that left the
/// <c>NewUserExperience</c> collection fully dark on <c>main</c> while CI reported green
/// (#2491/#2738, then again #2739/#2749): one provisioning fault flips one boolean and the
/// entire collection converts itself into silent skips.
/// </para>
/// <para>
/// The indirection through <c>ShouldSkip()</c> made it harder to see, not less dangerous - the
/// extension-boot gate exists specifically to catch assembly-load regressions (#2220), so a
/// gate that can quietly stop running is worth very little.
/// </para>
/// <para>
/// The test below therefore ASSERTS rather than skips. It is a plain <c>[Fact]</c> and must
/// never become a <c>[SkippableFact]</c>: skipping is exactly the failure mode being prevented.
/// </para>
/// </remarks>
[Collection(ExtensionBootCollection.Name)]
public sealed class ExtensionBootFixtureHealthTests
{
    private readonly ExtensionBootFixture _fx;

    public ExtensionBootFixtureHealthTests(ExtensionBootFixture fx) => _fx = fx;

    /// <summary>
    /// The load-bearing regression assertion for this collection. Reddens by name whenever the
    /// extension-boot fixture fails to provision, instead of allowing the smoke gate to report a
    /// vacuous pass.
    /// </summary>
    [Fact]
    public void FixtureInitializationSucceeded()
    {
        if (_fx.Succeeded)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException(
            "Extension-boot fixture initialization FAILED, so every test in " +
            "ExtensionBootSmokeTests would have silently skipped and the #2220 " +
            "assembly-load gate would have reported a vacuous pass (issue #2491). " +
            "This is an assertion, not a skip, precisely so an infrastructure fault " +
            "cannot masquerade as a green gate.\n\n" +
            $"Error: {_fx.Error ?? "(none recorded)"}\n\n" +
            "Fixture log:\n  " + string.Join("\n  ", _fx.Log) + "\n\n" +
            "Gateway output:\n" + _fx.GatewayOutput());
    }
}
