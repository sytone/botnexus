using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Pins how an offering's version is compared with the installed one.
/// </summary>
/// <remarks>
/// The case this exists for is a catalog pinned BEHIND what is running. Presenting that as an
/// update would send someone to install a downgrade, so a direction is only ever claimed when both
/// versions can actually be ordered.
/// </remarks>
public sealed class MarketplaceOfferingComparisonTests
{
    private static MarketplaceOfferingState Resolve(string? installed, string? offered, bool isInstalled = true) =>
        MarketplaceOfferingComparison.Resolve(installed, offered, isInstalled);

    [Fact]
    public void Not_installed_is_installable()
    {
        Assert.Equal(
            MarketplaceOfferingState.NotInstalled,
            Resolve(installed: null, offered: "1.0.0", isInstalled: false));
    }

    [Fact]
    public void Same_version_is_just_installed()
    {
        Assert.Equal(MarketplaceOfferingState.Installed, Resolve("1.2.1", "1.2.1"));
    }

    [Fact]
    public void A_newer_offering_is_an_update()
    {
        Assert.Equal(MarketplaceOfferingState.UpdateAvailable, Resolve("1.2.0", "1.2.1"));
    }

    /// <summary>The live case: a catalog still pinning v1.2.0 while v1.2.1 is installed.</summary>
    [Fact]
    public void An_older_offering_is_reported_as_the_source_being_behind_not_an_update()
    {
        Assert.Equal(MarketplaceOfferingState.SourceIsBehind, Resolve("1.2.1", "1.2.0"));
    }

    [Theory]
    [InlineData("1.2.0", "2.0.0")]
    [InlineData("1.9.0", "1.10.0")]   // numeric, not lexical: 10 > 9
    [InlineData("1.2.0", "1.2.1")]
    [InlineData("0.9.9", "1.0.0")]
    public void Ordering_is_numeric_per_segment(string installed, string offered)
    {
        Assert.Equal(MarketplaceOfferingState.UpdateAvailable, Resolve(installed, offered));
    }

    [Theory]
    [InlineData("v1.2.0", "v1.2.1")]
    [InlineData("1.2.0", "v1.2.1")]
    [InlineData("V1.2.0", "1.2.1")]
    public void A_leading_v_is_tolerated_because_tags_carry_one(string installed, string offered)
    {
        Assert.Equal(MarketplaceOfferingState.UpdateAvailable, Resolve(installed, offered));
    }

    [Theory]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.0", "1.2")]
    [InlineData("1", "1.0.0")]
    public void A_missing_trailing_segment_counts_as_zero(string installed, string offered)
    {
        Assert.Equal(MarketplaceOfferingState.Installed, Resolve(installed, offered));
    }

    /// <summary>
    /// Anything not dotted-numeric is unorderable. Saying "differs" is honest; guessing a direction
    /// is how a downgrade gets presented as an upgrade.
    /// </summary>
    [Theory]
    [InlineData("1.2.0", "1.2.1-beta")]
    [InlineData("2026-08-31", "1.2.0")]
    [InlineData("1.2.0", "abc123")]
    [InlineData("nightly", "1.2.0")]
    public void An_unorderable_pair_reports_only_that_it_differs(string installed, string offered)
    {
        Assert.Equal(MarketplaceOfferingState.VersionDiffers, Resolve(installed, offered));
    }

    /// <summary>
    /// An unversioned plugin on either side would otherwise be flagged as differing forever.
    /// </summary>
    [Theory]
    [InlineData(null, "1.2.0")]
    [InlineData("1.2.0", null)]
    [InlineData("", "1.2.0")]
    [InlineData("   ", "1.2.0")]
    public void A_missing_version_on_either_side_is_just_installed(string? installed, string? offered)
    {
        Assert.Equal(MarketplaceOfferingState.Installed, Resolve(installed, offered));
    }

    [Fact]
    public void Comparison_is_never_claimed_for_a_plugin_that_is_not_installed()
    {
        // Even with wildly different versions, absence wins - there is nothing to compare against.
        Assert.Equal(
            MarketplaceOfferingState.NotInstalled,
            Resolve("9.9.9", "1.0.0", isInstalled: false));
    }
}
