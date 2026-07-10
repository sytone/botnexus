using Microsoft.AspNetCore.SignalR.Client;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for <see cref="MobileReconnectRetryPolicy"/> (#1840). The policy adapts the pure
/// <see cref="MobileReconnectBackoff"/> schedule to SignalR's <see cref="IRetryPolicy"/> so the
/// client's automatic-reconnect budget is widened well beyond the default ~5x3s and never gives
/// up, letting a returning backgrounded PWA self-heal.
/// </summary>
public sealed class MobileReconnectRetryPolicyTests
{
    private static RetryContext Ctx(long previousRetryCount) => new()
    {
        PreviousRetryCount = previousRetryCount,
        ElapsedTime = TimeSpan.Zero,
        RetryReason = null,
    };

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    public void Delay_matches_backoff_schedule(long attempt, int expectedSeconds)
    {
        var policy = new MobileReconnectRetryPolicy();

        var delay = policy.NextRetryDelay(Ctx(attempt));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void Delay_is_capped_at_thirty_seconds()
    {
        var policy = new MobileReconnectRetryPolicy();

        Assert.Equal(MobileReconnectBackoff.MaxDelay, policy.NextRetryDelay(Ctx(10)));
    }

    [Fact]
    public void Never_returns_null_so_reconnect_continues_indefinitely()
    {
        var policy = new MobileReconnectRetryPolicy();

        // A null delay would tell SignalR to stop retrying; the mobile policy must never do that.
        Assert.NotNull(policy.NextRetryDelay(Ctx(0)));
        Assert.NotNull(policy.NextRetryDelay(Ctx(1000)));
    }

    [Fact]
    public void Schedule_is_widened_beyond_the_default_five_by_three_second_budget()
    {
        var policy = new MobileReconnectRetryPolicy();

        var cumulative = TimeSpan.Zero;
        for (long i = 0; i < 8; i++)
            cumulative += policy.NextRetryDelay(Ctx(i))!.Value;

        Assert.True(
            cumulative > TimeSpan.FromSeconds(60),
            $"Expected first 8 attempts to span >60s, but was {cumulative.TotalSeconds}s.");
    }
}
