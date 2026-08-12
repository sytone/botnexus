using BotNexus.Agent.Providers.Core.Resilience;

namespace BotNexus.Agent.Providers.Core.Tests.Resilience;

/// <summary>
/// Tests for the shared bounded-jitter seam (#3035).
/// <para>
/// The point of these tests is the pair of <em>bounds</em>, not a range check. A range assertion
/// ("delay is between 250 and 320ms") passes just as happily when jitter is silently removed, because
/// the un-jittered value is inside the range. Pinning the injected source to its two extremes turns the
/// property into an exact equality at one end and a strict inequality at the other, so deleting the
/// jitter term reddens the max-pinned test and changing the backoff curve reddens the zero-pinned one.
/// </para>
/// </summary>
public class RetryJitterTests
{
    [Fact]
    public void ApplyMs_RandomPinnedToZero_ReturnsBaseDelayExactly()
    {
        // The deterministic bound: with no randomness the pre-existing schedule must be reproduced
        // byte-for-byte, which is what makes this change safe to land.
        RetryJitter.ApplyMs(500, random: 0).ShouldBe(500);
        RetryJitter.ApplyMs(1000, random: 0).ShouldBe(1000);
        RetryJitter.ApplyMs(2000, random: 0).ShouldBe(2000);
    }

    [Fact]
    public void ApplyMs_RandomPinnedToMax_StretchesByExactlyTheJitterFactor()
    {
        RetryJitter.ApplyMs(500, random: 1).ShouldBe(500 * (1 + RetryJitter.DefaultJitterFactor));
        RetryJitter.ApplyMs(1000, random: 1).ShouldBe(1000 * (1 + RetryJitter.DefaultJitterFactor));
    }

    [Theory]
    [InlineData(500d)]
    [InlineData(1000d)]
    [InlineData(2000d)]
    public void ApplyMs_RandomPinnedToMax_IsStrictlyGreaterThanBaseAndWithinFactor(double baseMs)
    {
        var jittered = RetryJitter.ApplyMs(baseMs, random: 1);

        jittered.ShouldBeGreaterThan(baseMs);
        jittered.ShouldBeLessThanOrEqualTo(baseMs * (1 + RetryJitter.DefaultJitterFactor));
    }

    [Fact]
    public void ApplyMs_JitterIsOneSided_NeverShortensTheDelay()
    {
        // Sad path for the herd: a two-sided jitter could return LESS than the caller asked for, which
        // would make a retry storm hotter rather than cooler. Sweep the whole source domain.
        for (var r = 0d; r <= 1d; r += 0.05)
        {
            RetryJitter.ApplyMs(1000, r).ShouldBeGreaterThanOrEqualTo(1000);
        }
    }

    [Theory]
    [InlineData(-5d)]
    [InlineData(1.5d)]
    [InlineData(double.PositiveInfinity)]
    public void ApplyMs_OutOfRangeRandomSource_IsClampedIntoTheBoundedWindow(double rogue)
    {
        // A misbehaving source must not be able to produce a negative or unbounded delay.
        var jittered = RetryJitter.ApplyMs(1000, rogue);

        jittered.ShouldBeGreaterThanOrEqualTo(1000);
        jittered.ShouldBeLessThanOrEqualTo(1000 * (1 + RetryJitter.DefaultJitterFactor));
    }

    [Fact]
    public void ApplyMs_NaNRandomSource_FallsBackToTheUnJitteredDelay()
    {
        RetryJitter.ApplyMs(1000, double.NaN).ShouldBe(1000);
    }

    [Fact]
    public void ApplyMs_NonPositiveBaseDelay_IsReturnedUnchanged()
    {
        RetryJitter.ApplyMs(0, random: 1).ShouldBe(0);
        RetryJitter.ApplyMs(-10, random: 1).ShouldBe(-10);
    }

    [Fact]
    public void ApplyMs_ZeroJitterFactor_DisablesJitter()
    {
        RetryJitter.ApplyMs(1000, random: 1, jitterFactor: 0).ShouldBe(1000);
    }

    [Fact]
    public void Apply_TimeSpanOverload_MatchesTheMillisecondOverload()
    {
        RetryJitter.Apply(TimeSpan.FromMilliseconds(250), random: 1)
            .TotalMilliseconds
            .ShouldBe(RetryJitter.ApplyMs(250, random: 1));
    }

    [Fact]
    public void DefaultRandomSource_StaysWithinTheUnitInterval()
    {
        for (var i = 0; i < 200; i++)
        {
            var value = RetryJitter.DefaultRandomSource();
            value.ShouldBeGreaterThanOrEqualTo(0);
            value.ShouldBeLessThan(1);
        }
    }
}
