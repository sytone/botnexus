using BotNexus.Satellite.Client.Core;

namespace BotNexus.Satellite.Client.Core.Tests;

public sealed class RetryTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(1000, 30)]
    public void GetDelay_ExponentialBackoff_IsBoundedWithoutOverflow(int attempt, int seconds)
    {
        new RetryPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
            .GetDelay(attempt).ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void GetDelay_NegativeAttempt_Rejects()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new RetryPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)).GetDelay(-1));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    public void GetDelay_InvalidBounds_Rejects(int initial, int maximum)
    {
        Should.Throw<ArgumentException>(() =>
            new RetryPolicy(TimeSpan.FromSeconds(initial), TimeSpan.FromSeconds(maximum)).GetDelay(0));
    }
}
