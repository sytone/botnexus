using BotNexus.Gateway.Abstractions.Media;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests.Media;

public sealed class MediaContractTests
{
    [Fact]
    public void MediaProcessingContext_Constructor_WithRequiredProperties_ShouldPreserveValues()
    {
        var context = new MediaProcessingContext
        {
            SessionId = "session-1",
            ChannelType = "signalr"
        };

        context.SessionId.ShouldBe("session-1");
        context.ChannelType.ShouldBe("signalr");
        context.CancellationToken.ShouldBe(CancellationToken.None);
    }

    [Fact]
    public void MediaProcessingResult_Constructor_WithRequiredProperties_ShouldPreserveValuesAndDefaults()
    {
        var processedPart = new TextContentPart
        {
            MimeType = "text/plain",
            Text = "processed"
        };
        var result = new MediaProcessingResult
        {
            ProcessedPart = processedPart
        };

        result.ProcessedPart.ShouldBeSameAs(processedPart);
        result.WasTransformed.ShouldBeFalse();
        result.Metadata.ShouldBeNull();
    }
}
