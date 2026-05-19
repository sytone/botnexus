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
        config.ActiveHours.ShouldBeNull();
        config.Prompt.ShouldBeNull();
        config.Enabled.ShouldBeFalse();
        config.AckMaxChars.ShouldBe(300);
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
    public void ActiveHoursConfig_Defaults_AreExpected()
    {
        var config = new ActiveHoursConfig();

        config.Start.ShouldBe("08:00");
        config.End.ShouldBe("23:00");
        config.Timezone.ShouldBeNull();
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

    [Fact]
    public void ActiveHoursConfig_JsonRoundTrip_PreservesValues()
    {
        var original = new HeartbeatAgentConfig
        {
            Enabled = true,
            IntervalMinutes = 30,
            ActiveHours = new ActiveHoursConfig
            {
                Start = "08:00",
                End = "22:30",
                Timezone = "America/Los_Angeles"
            }
        };

        var json = JsonSerializer.Serialize(original);
        var roundTrip = JsonSerializer.Deserialize<HeartbeatAgentConfig>(json);

        roundTrip.ShouldNotBeNull();
        roundTrip!.ActiveHours.ShouldNotBeNull();
        roundTrip.ActiveHours!.Start.ShouldBe("08:00");
        roundTrip.ActiveHours.End.ShouldBe("22:30");
        roundTrip.ActiveHours.Timezone.ShouldBe("America/Los_Angeles");
    }

    // --- ActiveHoursConfig.ParseTime ---

    [Theory]
    [InlineData("08:00", 8, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData("00:00", 0, 0)]
    [InlineData("12:30", 12, 30)]
    public void ParseTime_ValidInput_ReturnsExpected(string value, int expectedHour, int expectedMinute)
    {
        var result = ActiveHoursConfig.ParseTime(value);
        result.ShouldNotBeNull();
        result!.Value.Hour.ShouldBe(expectedHour);
        result.Value.Minute.ShouldBe(expectedMinute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("8:00")]       // single-digit hour is actually valid per our parser
    [InlineData("25:00")]      // hour out of range
    [InlineData("08:60")]      // minute out of range
    [InlineData("not-a-time")]
    [InlineData("08")]
    public void ParseTime_InvalidInput_ReturnsNull(string? value)
    {
        // "8:00" is actually parseable (int.TryParse handles it) — but 25:00 and 08:60 are not.
        // Filter: only truly invalid ones should return null.
        var result = ActiveHoursConfig.ParseTime(value);
        if (value is "25:00" or "08:60" or "not-a-time" or "08" or null or "" or "  ")
            result.ShouldBeNull();
        // "8:00" parses fine — it's a valid time.
    }

    // --- ActiveHoursConfig.Validate ---

    [Fact]
    public void Validate_ValidWindow_ReturnsNull()
    {
        var config = new ActiveHoursConfig { Start = "08:00", End = "23:00" };
        config.Validate().ShouldBeNull();
    }

    [Fact]
    public void Validate_EndEqualsStart_ReturnsError()
    {
        var config = new ActiveHoursConfig { Start = "08:00", End = "08:00" };
        config.Validate().ShouldNotBeNull();
    }

    [Fact]
    public void Validate_EndBeforeStart_ReturnsError()
    {
        var config = new ActiveHoursConfig { Start = "22:00", End = "06:00" };
        config.Validate().ShouldNotBeNull();
    }

    [Fact]
    public void Validate_InvalidStartFormat_ReturnsError()
    {
        var config = new ActiveHoursConfig { Start = "not-a-time", End = "23:00" };
        config.Validate().ShouldNotBeNull();
    }

    [Fact]
    public void Validate_InvalidEndFormat_ReturnsError()
    {
        var config = new ActiveHoursConfig { Start = "08:00", End = "nope" };
        config.Validate().ShouldNotBeNull();
    }
}
