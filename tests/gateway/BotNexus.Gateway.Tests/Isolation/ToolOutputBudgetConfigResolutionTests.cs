using BotNexus.Agent.Core.Loop;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Isolation;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// AC5 of #3162: the central tool-output budget is configurable, defaults to the documented value,
/// and a non-positive value disables it.
/// </summary>
public sealed class ToolOutputBudgetConfigResolutionTests
{
    /// <summary>
    /// An absent <c>gateway:toolOutputBudget</c> section must resolve to the documented default,
    /// NOT to unbounded. An unbounded tool result reaching the context window is the condition the
    /// backstop exists to prevent, so "not configured" must never mean "unprotected".
    /// </summary>
    [Fact]
    public void ResolveMaxToolOutputBytes_AbsentSection_UsesDocumentedDefault()
    {
        InProcessIsolationStrategy.ResolveMaxToolOutputBytes(null)
            .ShouldBe(ToolOutputBudget.DefaultMaxBytes);
    }

    /// <summary>The class default must match the documented 256 KiB in <c>docs/configuration.md</c>.</summary>
    [Fact]
    public void ToolOutputBudgetConfig_DefaultMaxBytes_MatchesDocumentedValue()
    {
        var config = new ToolOutputBudgetConfig();
        config.Enabled.ShouldBeTrue();
        config.MaxBytes.ShouldBe(262_144);
        config.MaxBytes.ShouldBe(ToolOutputBudget.DefaultMaxBytes);
    }

    [Fact]
    public void ResolveMaxToolOutputBytes_ExplicitValue_IsHonoured()
    {
        InProcessIsolationStrategy.ResolveMaxToolOutputBytes(
            new ToolOutputBudgetConfig { Enabled = true, MaxBytes = 4096 }).ShouldBe(4096);
    }

    /// <summary>
    /// Both disabling routes collapse to zero, which <c>ToolOutputBudget.Apply</c> treats as a
    /// no-op. This is the same convention <c>toolResultPersistence</c> already uses, so an operator
    /// does not have to learn a second one.
    /// </summary>
    [Theory]
    [InlineData(false, 4096)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public void ResolveMaxToolOutputBytes_DisabledOrNonPositive_ReturnsZero(bool enabled, int maxBytes)
    {
        InProcessIsolationStrategy.ResolveMaxToolOutputBytes(
            new ToolOutputBudgetConfig { Enabled = enabled, MaxBytes = maxBytes }).ShouldBe(0);
    }
}
