using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

public sealed class HeartbeatAgentConfigTests
{
    [Fact]
    public void HeartbeatAgentConfig_Defaults_AreExpected()
    {
        var config = new HeartbeatAgentConfig();

        config.IntervalMinutes.ShouldBe(30);
        config.QuietHours.ShouldBeNull();
        config.Prompt.ShouldBeNull();
        config.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void QuietHoursConfig_Defaults_AreExpected()
    {
        var config = new QuietHoursConfig();

        config.Start.ShouldBe("23:00");
        config.End.ShouldBe("07:00");
        config.Enabled.ShouldBeFalse();
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

        roundTrip.ShouldNotBeNull();
        roundTrip!.Enabled.ShouldBeTrue();
        roundTrip.IntervalMinutes.ShouldBe(15);
        roundTrip.Prompt.ShouldBe("Check heartbeat");
        roundTrip.QuietHours.ShouldNotBeNull();
        roundTrip.QuietHours!.Enabled.ShouldBeTrue();
        roundTrip.QuietHours.Start.ShouldBe("22:30");
        roundTrip.QuietHours.End.ShouldBe("06:45");
        roundTrip.QuietHours.Timezone.ShouldBe("UTC");
    }
}
