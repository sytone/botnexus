using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2441: the density preset must always normalise to a legal value so an unknown or corrupted
/// preference can never emit an unrecognised <c>data-density</c> attribute into the DOM.
/// </summary>
public sealed class PortalDensityPreferenceTests
{
    [Fact]
    public void Default_preference_is_compact() =>
        Assert.Equal(PortalDensity.Compact, new PortalPreferences().Density);

    [Theory]
    [InlineData("comfortable")]
    [InlineData("Comfortable")]
    [InlineData("  COMFORTABLE  ")]
    public void Normalize_accepts_comfortable_case_insensitively(string input) =>
        Assert.Equal(PortalDensity.Comfortable, PortalDensity.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("compact")]
    [InlineData("spacious")]
    [InlineData("<script>")]
    public void Normalize_falls_back_to_compact_for_anything_else(string? input) =>
        Assert.Equal(PortalDensity.Compact, PortalDensity.Normalize(input));
}
