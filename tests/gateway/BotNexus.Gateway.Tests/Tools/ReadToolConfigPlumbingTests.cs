using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Pins that the operator-facing <c>gateway.readTool</c> config section actually reaches the read
/// tool (#2689 AC2). Without this, the setting would be documented but inert.
/// </summary>
public sealed class ReadToolConfigPlumbingTests
{
    [Fact]
    public void BuildReadToolOptions_WhenSectionAbsent_UsesDocumentedDefaults()
    {
        var options = ToolServiceCollectionExtensions.BuildReadToolOptions(new PlatformConfig());

        options.LargeReadThresholdBytes.ShouldBe(ReadToolConfig.DefaultLargeReadThresholdBytes);
        options.LargeReadThresholdBytes.ShouldBe(20 * 1024);
        options.ElideUnchangedRereads.ShouldBeTrue();
    }

    [Fact]
    public void BuildReadToolOptions_WhenConfigNull_UsesDocumentedDefaults()
    {
        var options = ToolServiceCollectionExtensions.BuildReadToolOptions(null);

        options.LargeReadThresholdBytes.ShouldBe(ReadToolConfig.DefaultLargeReadThresholdBytes);
        options.ElideUnchangedRereads.ShouldBeTrue();
    }

    [Fact]
    public void BuildReadToolOptions_WhenSectionPresent_CarriesOperatorValuesThrough()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ReadTool = new ReadToolConfig
                {
                    LargeReadThresholdBytes = 4096,
                    ElideUnchangedRereads = false,
                },
            },
        };

        var options = ToolServiceCollectionExtensions.BuildReadToolOptions(config);

        options.LargeReadThresholdBytes.ShouldBe(4096);
        options.ElideUnchangedRereads.ShouldBeFalse();
    }

    [Fact]
    public void BuildReadToolOptions_WhenThresholdZero_PropagatesTheDisabledValue()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ReadTool = new ReadToolConfig { LargeReadThresholdBytes = 0 },
            },
        };

        ToolServiceCollectionExtensions.BuildReadToolOptions(config).LargeReadThresholdBytes.ShouldBe(0);
    }
}
