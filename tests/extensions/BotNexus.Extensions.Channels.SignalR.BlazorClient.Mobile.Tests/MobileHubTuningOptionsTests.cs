using System;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the mobile-scoped SignalR keep-alive / server-timeout tuning (#1840). These values
/// must be configurable via the mobile client's appsettings while carrying mobile defaults tuned
/// against the netbird tunnel idle window, and must never let the server timeout fall below twice
/// the keep-alive interval (SignalR's recommended minimum ratio).
/// </summary>
public sealed class MobileHubTuningOptionsTests
{
    [Fact]
    public void Defaults_are_mobile_tuned()
    {
        var options = new MobileHubTuningOptions();

        Assert.Equal(15, options.KeepAliveIntervalSeconds);
        Assert.Equal(60, options.ServerTimeoutSeconds);
    }

    [Fact]
    public void Default_server_timeout_is_at_least_twice_keep_alive()
    {
        // SignalR guidance: the server timeout should be at least twice the keep-alive interval so a
        // single dropped ping does not trip a client-side timeout.
        Assert.True(
            MobileHubTuningOptions.DefaultServerTimeoutSeconds
                >= MobileHubTuningOptions.DefaultKeepAliveIntervalSeconds * 2);
    }

    [Fact]
    public void ToTuning_uses_configured_values()
    {
        var options = new MobileHubTuningOptions
        {
            KeepAliveIntervalSeconds = 20,
            ServerTimeoutSeconds = 90,
        };

        var tuning = options.ToTuning();

        Assert.Equal(TimeSpan.FromSeconds(20), tuning.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(90), tuning.ServerTimeout);
    }

    [Fact]
    public void ToTuning_supplies_the_widened_reconnect_policy()
    {
        var tuning = new MobileHubTuningOptions().ToTuning();

        Assert.IsType<MobileReconnectRetryPolicy>(tuning.ReconnectRetryPolicy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ToTuning_falls_back_to_default_keep_alive_for_non_positive(int invalid)
    {
        var tuning = new MobileHubTuningOptions { KeepAliveIntervalSeconds = invalid }.ToTuning();

        Assert.Equal(
            TimeSpan.FromSeconds(MobileHubTuningOptions.DefaultKeepAliveIntervalSeconds),
            tuning.KeepAliveInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ToTuning_falls_back_to_default_server_timeout_for_non_positive(int invalid)
    {
        var tuning = new MobileHubTuningOptions { ServerTimeoutSeconds = invalid }.ToTuning();

        Assert.Equal(
            TimeSpan.FromSeconds(MobileHubTuningOptions.DefaultServerTimeoutSeconds),
            tuning.ServerTimeout);
    }

    [Fact]
    public void ToTuning_coerces_server_timeout_to_at_least_twice_keep_alive()
    {
        // A misconfig where the timeout is below 2x the keep-alive would make an idle connection
        // flap; ToTuning must coerce it up rather than honour the unsafe pair.
        var tuning = new MobileHubTuningOptions
        {
            KeepAliveIntervalSeconds = 20,
            ServerTimeoutSeconds = 25, // below 2x20 = 40
        }.ToTuning();

        Assert.Equal(TimeSpan.FromSeconds(40), tuning.ServerTimeout);
    }

    [Fact]
    public void Binds_from_configuration_signalr_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SignalR:KeepAliveIntervalSeconds"] = "18",
                ["SignalR:ServerTimeoutSeconds"] = "72",
            })
            .Build();

        var options = new MobileHubTuningOptions();
        config.GetSection("SignalR").Bind(options);

        Assert.Equal(18, options.KeepAliveIntervalSeconds);
        Assert.Equal(72, options.ServerTimeoutSeconds);
    }
}
