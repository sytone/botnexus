using System;
using System.Linq;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the mobile reconnect backoff schedule (#1839). The schedule must widen
/// beyond Blazor's default 5x3s (15s) budget so a backgrounded PWA that returns after a
/// long suspension keeps auto-retrying rather than surfacing a dead-end error bar.
/// </summary>
public sealed class MobileReconnectBackoffTests
{
    [Fact]
    public void First_attempt_uses_base_delay()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), MobileReconnectBackoff.GetDelay(0));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    public void Delay_grows_exponentially_until_capped(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), MobileReconnectBackoff.GetDelay(attempt));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(50)]
    public void Delay_is_capped_at_max(int attempt)
    {
        Assert.Equal(MobileReconnectBackoff.MaxDelay, MobileReconnectBackoff.GetDelay(attempt));
    }

    [Fact]
    public void Max_delay_is_thirty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), MobileReconnectBackoff.MaxDelay);
    }

    [Fact]
    public void Delay_never_exceeds_max()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.True(MobileReconnectBackoff.GetDelay(i) <= MobileReconnectBackoff.MaxDelay);
        }
    }

    [Fact]
    public void Negative_attempt_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MobileReconnectBackoff.GetDelay(-1));
    }

    [Fact]
    public void Schedule_is_widened_well_beyond_the_default_five_by_three_second_budget()
    {
        // Blazor's default reconnect budget is 5 retries x 3s = ~15s. The mobile schedule must
        // cover realistic background-return latency, so the cumulative window across the early
        // attempts must be substantially longer than that default.
        var cumulativeFirstEight = Enumerable.Range(0, 8)
            .Select(MobileReconnectBackoff.GetDelay)
            .Aggregate(TimeSpan.Zero, (acc, d) => acc + d);

        Assert.True(
            cumulativeFirstEight > TimeSpan.FromSeconds(60),
            $"Expected the first 8 attempts to span more than 60s, but was {cumulativeFirstEight.TotalSeconds}s.");
    }

    [Fact]
    public void Schedule_keeps_retrying_indefinitely_so_a_returning_app_self_heals()
    {
        // There is no terminal "give up" attempt: every attempt yields a positive delay so the
        // overlay never becomes a dead-end. A returning backgrounded PWA keeps trying until the
        // hub is reachable again.
        Assert.True(MobileReconnectBackoff.GetDelay(1000) > TimeSpan.Zero);
    }
}
