using BotNexus.Agent.Core.Tools;

namespace BotNexus.Agent.Core.Tests.Tools;

/// <summary>
/// Covers the closed content-source vocabulary and its fail-closed normalisation (#2519).
/// </summary>
public sealed class ToolContentSourceTests
{
    [Theory]
    [InlineData("local", ToolContentSource.Local)]
    [InlineData("network", ToolContentSource.Network)]
    [InlineData("untrusted", ToolContentSource.Untrusted)]
    [InlineData("unknown", ToolContentSource.Unknown)]
    public void Normalize_RecognisedValue_ReturnsCanonicalMember(string input, string expected)
        => ToolContentSource.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("LOCAL")]
    [InlineData("  Local  ")]
    [InlineData("NeTwOrK")]
    public void Normalize_IsCaseAndWhitespaceInsensitive(string input)
        => ToolContentSource.Normalize(input).ShouldNotBe(ToolContentSource.Unknown);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("trusted")]
    [InlineData("locel")]
    [InlineData("local; drop table")]
    public void Normalize_UnrecognisedOrMissingValue_FailsClosedToUnknown(string? input)
        => ToolContentSource.Normalize(input).ShouldBe(ToolContentSource.Unknown);

    [Fact]
    public void IsTainting_Local_IsTheOnlyUntaintingSource()
    {
        ToolContentSource.IsTainting(ToolContentSource.Local).ShouldBeFalse();

        ToolContentSource.IsTainting(ToolContentSource.Network).ShouldBeTrue();
        ToolContentSource.IsTainting(ToolContentSource.Untrusted).ShouldBeTrue();
        ToolContentSource.IsTainting(ToolContentSource.Unknown).ShouldBeTrue();
    }

    /// <summary>
    /// The core fail-closed guarantee: a value nobody recognises must taint, not pass. A
    /// near-miss typo is the realistic form of this - it must NOT be charitably read as local.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("locel")]
    [InlineData("definitely-safe")]
    public void IsTainting_UnknownOrMalformedSource_Taints(string? input)
        => ToolContentSource.IsTainting(input).ShouldBeTrue();

    [Fact]
    public void All_ContainsEveryMemberNormalizeCanReturn()
        => ToolContentSource.All.ShouldBe(
            [ToolContentSource.Local, ToolContentSource.Network, ToolContentSource.Untrusted, ToolContentSource.Unknown],
            ignoreOrder: true);
}
