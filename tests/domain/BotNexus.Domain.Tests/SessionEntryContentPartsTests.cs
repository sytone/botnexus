using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

public sealed class SessionEntryContentPartsTests
{
    [Fact]
    public void SessionEntry_Constructor_WithoutContentPartLists_ShouldDefaultToNull()
    {
        var entry = CreateEntry();

        entry.OriginalContentParts.ShouldBeNull();
        entry.ProcessedContentParts.ShouldBeNull();
    }

    [Fact]
    public void SessionEntry_Constructor_WithBothContentPartLists_ShouldPreserveValues()
    {
        var original = new MessageContentPart[]
        {
            new BinaryContentPart { MimeType = "audio/wav", Data = [1, 2, 3] }
        };
        var processed = new MessageContentPart[]
        {
            new TextContentPart { MimeType = "text/plain", Text = "transcribed text" }
        };
        var entry = CreateEntry() with
        {
            OriginalContentParts = original,
            ProcessedContentParts = processed
        };

        entry.OriginalContentParts.ShouldBe(original);
        entry.ProcessedContentParts.ShouldBe(processed);
    }

    [Fact]
    public void SessionEntry_Constructor_WithOriginalContentPartsOnly_ShouldSupportPreProcessingState()
    {
        var original = new MessageContentPart[]
        {
            new ReferenceContentPart { MimeType = "audio/mpeg", Uri = "https://example.invalid/audio.mp3" }
        };
        var entry = CreateEntry() with { OriginalContentParts = original };

        entry.OriginalContentParts.ShouldBe(original);
        entry.ProcessedContentParts.ShouldBeNull();
    }

    [Fact]
    public void SessionEntry_ContentPartLists_ShouldRemainIndependent()
    {
        var original = new MessageContentPart[]
        {
            new BinaryContentPart { MimeType = "audio/wav", Data = [1, 2, 3], FileName = "input.wav" }
        };
        var processed = new MessageContentPart[]
        {
            new TextContentPart { MimeType = "text/plain", Text = "hello world" }
        };
        var entry = CreateEntry() with
        {
            OriginalContentParts = original,
            ProcessedContentParts = processed
        };

        entry.OriginalContentParts.Count().ShouldBe(1);
        entry.ProcessedContentParts.Count().ShouldBe(1);
        entry.OriginalContentParts![0].ShouldNotBeSameAs(entry.ProcessedContentParts![0]);
        entry.OriginalContentParts[0].ShouldBeOfType<BinaryContentPart>();
        entry.ProcessedContentParts[0].ShouldBeOfType<TextContentPart>();
    }

    private static SessionEntry CreateEntry() => new()
    {
        Role = MessageRole.User,
        Content = "hello"
    };
}
