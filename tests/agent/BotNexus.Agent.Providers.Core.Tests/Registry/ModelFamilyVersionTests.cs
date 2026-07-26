using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

/// <summary>
/// Covers the shared family+version parser that replaced the four duplicated substring lists
/// (issue #2374). The critical properties are: a NEW generation (Opus 5) classifies without any
/// code change, the existing 4.x classifications do not regress, malformed input degrades to false
/// instead of throwing, and version comparison is numeric so <c>4.50</c> outranks <c>4.6</c>.
/// </summary>
public class ModelFamilyVersionTests
{
    [Theory]
    [InlineData("claude-opus-4.5", 4, 5)]
    [InlineData("claude-opus-4.6", 4, 6)]
    [InlineData("claude-opus-4.8", 4, 8)]
    [InlineData("claude-opus-5", 5, 0)]
    [InlineData("claude-opus-5.1", 5, 1)]
    [InlineData("opus-4-6", 4, 6)]
    [InlineData("claude-opus-4-5-20250929", 4, 5)]
    [InlineData("copilot/claude-opus-5", 5, 0)]
    [InlineData("CLAUDE-OPUS-4.6", 4, 6)]
    public void TryParse_ReadsOpusVersion(string modelId, int major, int minor)
    {
        Assert.True(ModelFamilyVersion.TryParse(modelId, "opus", out var version));
        Assert.Equal(new ModelVersion(major, minor), version);
    }

    [Theory]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("gpt-5.2")]
    [InlineData("claude-opus")]
    [InlineData("opus")]
    [InlineData("octopus-5")]
    [InlineData("claude-opus-x")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_DegradesSafelyOnNonOpusOrMalformedIds(string? modelId)
    {
        Assert.False(ModelFamilyVersion.TryParse(modelId, "opus", out var version));
        Assert.Equal(default, version);
    }

    /// <summary>
    /// Version-first id ordering (issue #2374 follow-up). SAP AI Core and several gateway vendors
    /// spell the same model as <c>claude-4.7-opus</c> rather than <c>claude-opus-4.7</c>. Both
    /// orderings must yield the same parsed version, otherwise the identical model gets two
    /// different capability classifications depending on which broker served it.
    /// </summary>
    [Theory]
    [InlineData("claude-4.7-opus", 4, 7)]
    [InlineData("claude-4-7-opus", 4, 7)]
    [InlineData("claude-5-opus", 5, 0)]
    [InlineData("claude-4.50-opus", 4, 50)]
    [InlineData("CLAUDE-4.7-OPUS", 4, 7)]
    [InlineData("sapaicore/claude-4.7-opus", 4, 7)]
    public void TryParse_ReadsVersionPrecedingTheFamilyToken(string modelId, int major, int minor)
    {
        Assert.True(ModelFamilyVersion.TryParse(modelId, "opus", out var version));
        Assert.Equal(new ModelVersion(major, minor), version);
    }

    [Theory]
    [InlineData("claude-x-opus")]
    [InlineData("claude-octopus")]
    [InlineData("octopus5")]
    [InlineData("4.7-octopus")]
    [InlineData("claude-opus-")]
    public void TryParse_RejectsNonTokenAndVersionlessMatchesInBothOrderings(string modelId)
    {
        Assert.False(ModelFamilyVersion.TryParse(modelId, "opus", out var version));
        Assert.Equal(default, version);
    }

    [Fact]
    public void TryParse_TreatsAnthropicDateStampAsNotAMinorVersion()
    {
        // claude-opus-4-5-20250929: the 20250929 is a release date, NOT minor version 20.
        Assert.True(ModelFamilyVersion.TryParse("claude-opus-4-5-20250929", "opus", out var version));
        Assert.Equal(5, version.Minor);
    }

    /// <summary>
    /// The minor component is capped at two digits so a bare date-stamped id parses as the major
    /// version with NO minor, rather than reading the release date as the minor (issue #2374).
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4-20250514")]
    [InlineData("claude-sonnet-4-20250514")]
    public void TryParse_DoesNotReadABareReleaseDateAsTheMinor(string modelId)
    {
        var family = modelId.Contains("opus", StringComparison.Ordinal) ? "opus" : "sonnet";
        Assert.True(ModelFamilyVersion.TryParse(modelId, family, out var version));
        Assert.Equal(4, version.Major);
        // Must be 4.0 -- neither 4.20250514 nor the truncated 4.20.
        Assert.Equal(0, version.Minor);
        Assert.Equal("4.0", version.ToString());
    }

    /// <summary>
    /// The date-stamp guard must apply to the version-FIRST ordering too, and a leading
    /// <c>claude-3-5-</c> style version must win over a trailing release date.
    /// </summary>
    [Fact]
    public void TryParse_PrefersALeadingVersionOverATrailingReleaseDate()
    {
        Assert.True(ModelFamilyVersion.TryParse("claude-3-5-haiku-20241022", "haiku", out var version));
        Assert.Equal(new ModelVersion(3, 5), version);

        Assert.True(ModelFamilyVersion.TryParse("claude-3-7-sonnet-20250219", "sonnet", out var sonnet));
        Assert.Equal(new ModelVersion(3, 7), sonnet);
    }

    [Fact]
    public void Compare_IsNumericNotLexicographic()
    {
        var v450 = new ModelVersion(4, 50);
        var v46 = new ModelVersion(4, 6);

        // Substring/character ordering would call 4.50 < 4.6 because '5' < '6'. It must not.
        Assert.True(v450.CompareTo(v46) > 0);
        Assert.True(v450.AtLeast(v46));
        Assert.False(v46.AtLeast(v450));
        Assert.True(new ModelVersion(5, 0).AtLeast(new ModelVersion(4, 8)));
        Assert.True(new ModelVersion(4, 6).AtLeast(new ModelVersion(4, 6)));
    }

    [Fact]
    public void IsAtLeast_ParsesThenComparesNumerically()
    {
        Assert.True(ModelFamilyVersion.IsAtLeast("claude-opus-4.50", "opus", 4, 6));
        Assert.True(ModelFamilyVersion.IsAtLeast("claude-opus-5", "opus", 4, 6));
        Assert.False(ModelFamilyVersion.IsAtLeast("claude-opus-4.5", "opus", 4, 6));
        Assert.False(ModelFamilyVersion.IsAtLeast(null, "opus", 4, 6));
        Assert.False(ModelFamilyVersion.IsAtLeast("gpt-5.4", "opus", 4, 6));
    }

    [Fact]
    public void IsAtLeast_HandlesTheGptFamilyToo()
    {
        Assert.True(ModelFamilyVersion.IsAtLeast("gpt-5.2", "gpt", 5, 2));
        Assert.True(ModelFamilyVersion.IsAtLeast("gpt-5.10", "gpt", 5, 2));
        Assert.False(ModelFamilyVersion.IsAtLeast("gpt-5.1", "gpt", 5, 2));
        Assert.False(ModelFamilyVersion.IsAtLeast("gpt-4o", "gpt", 5, 2));
    }

    /// <summary>
    /// The token-boundary test that backs the fail-open heuristic: it must recognise a family
    /// token WITHOUT requiring a version, and must not fire inside an unrelated word.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-next", "opus", true)]
    [InlineData("claude-opus-next", "claude", true)]
    [InlineData("claude-4.7-opus", "opus", true)]
    [InlineData("opus", "opus", true)]
    [InlineData("octopus5", "opus", false)]
    [InlineData("octopus-5", "opus", false)]
    [InlineData("gpt-5.4", "opus", false)]
    [InlineData("clauded-out", "claude", false)]
    [InlineData("", "opus", false)]
    [InlineData(null, "opus", false)]
    public void ContainsFamilyToken_RequiresATokenBoundaryOnBothSides(string? modelId, string family, bool expected) =>
        Assert.Equal(expected, ModelFamilyVersion.ContainsFamilyToken(modelId, family));

    [Fact]
    public void TryParse_RejectsBlankFamily()
    {
        Assert.Throws<ArgumentException>(() => ModelFamilyVersion.TryParse("claude-opus-5", "  ", out _));
        Assert.Throws<ArgumentException>(() => ModelFamilyVersion.ContainsFamilyToken("claude-opus-5", "  "));
    }
}
