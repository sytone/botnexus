using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class SatelliteIdTests
{
    [Fact]
    public void From_ValidValue_CreatesInstance()
    {
        var id = SatelliteId.From("sat_desktop_home");

        id.Value.ShouldBe("sat_desktop_home");
    }

    [Fact]
    public void From_TrimsWhitespace()
    {
        var id = SatelliteId.From("  sat_test  ");

        id.Value.ShouldBe("sat_test");
    }

    [Fact]
    public void From_EmptyString_Throws()
    {
        Should.Throw<Exception>(() => SatelliteId.From(""));
    }

    [Fact]
    public void From_Whitespace_Throws()
    {
        Should.Throw<Exception>(() => SatelliteId.From("   "));
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var id1 = SatelliteId.From("sat_one");
        var id2 = SatelliteId.From("sat_one");

        id1.ShouldBe(id2);
    }

    [Fact]
    public void Equality_DifferentValue_AreNotEqual()
    {
        var id1 = SatelliteId.From("sat_one");
        var id2 = SatelliteId.From("sat_two");

        id1.ShouldNotBe(id2);
    }
}
