using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class SatelliteKeyGeneratorTests
{
    [Fact]
    public void GenerateApiKey_StartsWithSatPrefix()
    {
        var key = SatelliteKeyGenerator.GenerateApiKey();

        key.ShouldStartWith("sat_");
    }

    [Fact]
    public void GenerateApiKey_HasExpectedLength()
    {
        var key = SatelliteKeyGenerator.GenerateApiKey();

        // "sat_" (4) + 43 base62 chars = 47 total
        key.Length.ShouldBe(47);
    }

    [Fact]
    public void GenerateApiKey_ContainsOnlyBase62Characters()
    {
        var key = SatelliteKeyGenerator.GenerateApiKey();
        var body = key["sat_".Length..];

        body.ShouldAllBe(c =>
            (c >= '0' && c <= '9') ||
            (c >= 'A' && c <= 'Z') ||
            (c >= 'a' && c <= 'z'));
    }

    [Fact]
    public void GenerateApiKey_ProducesUniqueKeys()
    {
        var keys = Enumerable.Range(0, 100)
            .Select(_ => SatelliteKeyGenerator.GenerateApiKey())
            .ToHashSet();

        // All 100 keys should be distinct (probability of collision is astronomically small)
        keys.Count.ShouldBe(100);
    }

    [Fact]
    public void GenerateApiKey_HasSufficientEntropy()
    {
        // 32 bytes = 256 bits of entropy. Base62-encoded to 43 chars.
        // Verify the key body is not all zeros or all same char.
        var key = SatelliteKeyGenerator.GenerateApiKey();
        var body = key["sat_".Length..];

        body.Distinct().Count().ShouldBeGreaterThan(5);
    }
}
