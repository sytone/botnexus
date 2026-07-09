using BotNexus.Gateway.Telemetry;
using Shouldly;

namespace BotNexus.Gateway.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="BotNexusMeters"/> canonical identifiers and naming convention.
/// </summary>
public sealed class BotNexusMetersTests
{
    [Fact]
    public void Name_IsCanonicalScope()
    {
        BotNexusMeters.Name.ShouldBe("BotNexus");
    }

    [Fact]
    public void Meter_And_ActivitySource_UseCanonicalName()
    {
        BotNexusMeters.Meter.Name.ShouldBe("BotNexus");
        BotNexusMeters.ActivitySource.Name.ShouldBe("BotNexus");
    }

    [Fact]
    public void Version_IsNotBlank()
    {
        BotNexusMeters.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void InstrumentName_FollowsConvention()
    {
        BotNexusMeters.InstrumentName("host", "starts").ShouldBe("botnexus.host.starts");
    }

    [Theory]
    [InlineData(null, "starts")]
    [InlineData("host", null)]
    [InlineData("", "starts")]
    [InlineData("host", "  ")]
    public void InstrumentName_InvalidArgs_Throws(string? area, string? instrument)
    {
        Should.Throw<ArgumentException>(() => BotNexusMeters.InstrumentName(area!, instrument!));
    }
}
