using BotNexus.Cli.Commands.Doctor;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Doctor;

/// <summary>
/// Two independently-chosen settings that only make sense read together: the tool-output backstop
/// bounds one tool result, and the compaction bloat trigger declares a single entry to be dead
/// weight. A result landing between them is billed in full and then forces a compaction on its
/// own — paying for tokens, a summarisation call, and a reset prompt cache, to carry something the
/// next step throws away.
/// </summary>
public sealed class ToolOutputBudgetCompactionCoherenceCheckTests
{
    private static ConfigDocument Parse(string json) => ConfigDocument.Parse(json);

    private static ToolOutputBudgetCompactionCoherenceCheck Check() => new();

    [Fact]
    public void ApplicableOnTheShippedDefaults()
    {
        // 256 KiB backstop over a 64 KiB bloat trigger — the band exists out of the box, which is
        // the whole reason this check reports rather than assuming someone misconfigured it.
        Check().IsApplicable(ConfigDocument.Empty()).ShouldBeTrue();
    }

    [Fact]
    public void NotApplicableWhenTheTwoAlreadyAgree()
    {
        Check().IsApplicable(Parse("""
            {"gateway":{"toolOutputBudget":{"maxBytes":65536},"compaction":{"largestEntryBytesThreshold":65536}}}
            """)).ShouldBeFalse();
    }

    [Fact]
    public void NotApplicableWhenTheBloatTriggerIsTheHigherOfTheTwo()
    {
        Check().IsApplicable(Parse("""
            {"gateway":{"toolOutputBudget":{"maxBytes":65536},"compaction":{"largestEntryBytesThreshold":262144}}}
            """)).ShouldBeFalse();
    }

    [Fact]
    public void NotApplicableWhenTheBackstopIsSwitchedOff()
    {
        // An operator who turned the backstop off has opted out of bounding tool output at all.
        // Reporting a band inside a bound that is not enforced would be noise.
        Check().IsApplicable(Parse("""
            {"gateway":{"toolOutputBudget":{"enabled":false,"maxBytes":262144}}}
            """)).ShouldBeFalse();
    }

    [Fact]
    public void NotApplicableWhenTheBackstopIsDisabledByANonPositiveSize()
    {
        // Zero disables the backstop even with enabled:true, matching ToolOutputBudgetConfig.
        Check().IsApplicable(Parse("""
            {"gateway":{"toolOutputBudget":{"enabled":true,"maxBytes":0}}}
            """)).ShouldBeFalse();
    }

    [Fact]
    public void NotApplicableWhenTheByteBasedCompactionTriggerIsSwitchedOff()
    {
        Check().IsApplicable(Parse("""
            {"gateway":{"toolOutputBudget":{"maxBytes":262144},"compaction":{"largestEntryBytesThreshold":0}}}
            """)).ShouldBeFalse();
    }

    [Fact]
    public void FixAlignsTheBackstopDownToTheConfiguredBloatThreshold()
    {
        var config = Parse("""
            {"gateway":{"toolOutputBudget":{"maxBytes":262144},"compaction":{"largestEntryBytesThreshold":32768}}}
            """);

        Check().Apply(config);

        config.GetInt(ToolOutputBudgetCompactionCoherenceCheck.BackstopMaxBytesPath).ShouldBe(32768);
        Check().IsApplicable(config).ShouldBeFalse();
    }

    [Fact]
    public void FixOnDefaultsUsesTheDefaultBloatThreshold()
    {
        var config = ConfigDocument.Empty();

        Check().Apply(config);

        config.GetInt(ToolOutputBudgetCompactionCoherenceCheck.BackstopMaxBytesPath)
            .ShouldBe(ToolOutputBudgetCompactionCoherenceCheck.DefaultBloatThresholdBytes);
    }
}
