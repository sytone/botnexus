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

    [Fact]
    public void TryParse_TreatsAnthropicDateStampAsNotAMinorVersion()
    {
        // claude-opus-4-5-20250929: the 20250929 is a release date, NOT minor version 20.
        Assert.True(ModelFamilyVersion.TryParse("claude-opus-4-5-20250929", "opus", out var version));
        Assert.Equal(5, version.Minor);
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

    [Fact]
    public void TryParse_RejectsBlankFamily()
    {
        Assert.Throws<ArgumentException>(() => ModelFamilyVersion.TryParse("claude-opus-5", "  ", out _));
    }
}
