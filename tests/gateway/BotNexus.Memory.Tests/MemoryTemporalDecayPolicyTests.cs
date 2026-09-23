using BotNexus.Memory.Models;

namespace BotNexus.Memory.Tests;

public sealed class MemoryTemporalDecayPolicyTests
{
    [Fact]
    public void Default_UsesThirtyDayHalfLife()
    {
        MemoryTemporalDecayPolicy.Default.Enabled.ShouldBeTrue();
        MemoryTemporalDecayPolicy.Default.HalfLifeDays.ShouldBe(30d);
        MemoryTemporalDecayPolicy.Default.Lambda.ShouldBe(Math.Log(2d) / 30d);
    }

    [Fact]
    public void Disabled_UsesZeroLambdaSoAgeHasNoPenalty()
    {
        var policy = new MemoryTemporalDecayPolicy(enabled: false, halfLifeDays: 7d);

        policy.Lambda.ShouldBe(0d);
    }

    [Fact]
    public void ConfiguredHalfLife_ProducesExactHalfLifeBoundary()
    {
        var policy = new MemoryTemporalDecayPolicy(enabled: true, halfLifeDays: 14d);

        Math.Exp(-policy.Lambda * policy.HalfLifeDays).ShouldBe(0.5d, tolerance: 1e-12);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void InvalidHalfLife_IsRejected(double halfLifeDays)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MemoryTemporalDecayPolicy(enabled: true, halfLifeDays));
    }
}
