using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

public sealed class TimingSafeTests
{
    [Fact]
    public void Equals_WithIdenticalStrings_ReturnsTrue()
        => TimingSafe.Equals("super-secret-key", "super-secret-key").ShouldBeTrue();

    [Fact]
    public void Equals_WithDifferentStrings_ReturnsFalse()
        => TimingSafe.Equals("super-secret-key", "super-secret-kez").ShouldBeFalse();

    [Fact]
    public void Equals_WithDifferentLengths_ReturnsFalse()
        => TimingSafe.Equals("short", "shorter").ShouldBeFalse();

    [Fact]
    public void Equals_WithNullFirst_ReturnsFalse()
        => TimingSafe.Equals(null, "value").ShouldBeFalse();

    [Fact]
    public void Equals_WithNullSecond_ReturnsFalse()
        => TimingSafe.Equals("value", null).ShouldBeFalse();

    [Fact]
    public void Equals_WithBothNull_ReturnsFalse()
        => TimingSafe.Equals(null, null).ShouldBeFalse();

    [Fact]
    public void Equals_WithMultiByteUtf8_ComparesByBytes()
    {
        TimingSafe.Equals("k\u00e9y", "k\u00e9y").ShouldBeTrue();
        TimingSafe.Equals("k\u00e9y", "k\u00f6y").ShouldBeFalse();
    }
}
