using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;

namespace BotNexus.Domain.Tests;

public sealed class HeartbeatAgentConfigTests
{
    [Fact]
    public void HeartbeatAgentConfig_Defaults_AreExpected()
    {
        var config = new HeartbeatAgentConfig();

        config.IntervalMinutes.Should().Be(30);
        config.QuietHours.Should().BeNull();
        config.Prompt.Should().BeNull();
        config.Enabled.Should().BeFalse();
    }

    [Fact]
    public void QuietHoursConfig_Defaults_AreExpected()
    {
        var config = new QuietHoursConfig();

        config.Start.Should().Be("23:00");
        config.End.Should().Be("07:00");
        config.Enabled.Should().BeFalse();
    }

    [Fact]
    public void HeartbeatAgentConfig_JsonRoundTrip_PreservesValues()
    {
        var original = new HeartbeatAgentConfig
        {
            Enabled = true,
            IntervalMinutes = 15,
            Prompt = "Check heartbeat",
            QuietHours = new QuietHoursConfig
            {
                Enabled = true,
                Start = "22:30",
                End = "06:45",
                Timezone = "UTC"
            }
        };

        var json = JsonSerializer.Serialize(original);
        var roundTrip = JsonSerializer.Deserialize<HeartbeatAgentConfig>(json);

        roundTrip.Should().NotBeNull();
        roundTrip!.Enabled.Should().BeTrue();
        roundTrip.IntervalMinutes.Should().Be(15);
        roundTrip.Prompt.Should().Be("Check heartbeat");
        roundTrip.QuietHours.Should().NotBeNull();
        roundTrip.QuietHours!.Enabled.Should().BeTrue();
        roundTrip.QuietHours.Start.Should().Be("22:30");
        roundTrip.QuietHours.End.Should().Be("06:45");
        roundTrip.QuietHours.Timezone.Should().Be("UTC");
    }
}
