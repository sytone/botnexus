using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

public sealed class MessageContentPartTests
{
    [Fact]
    public void TextContentPart_Constructor_WithRequiredProperties_ShouldPreserveValues()
    {
        var part = new TextContentPart
        {
            MimeType = "text/plain",
            Text = "hello"
        };

        part.MimeType.ShouldBe("text/plain");
        part.Text.ShouldBe("hello");
    }

    [Fact]
    public void TextContentPart_RecordEquality_WithSameValues_ShouldBeEqual()
    {
        var left = new TextContentPart { MimeType = "text/plain", Text = "hello" };
        var right = new TextContentPart { MimeType = "text/plain", Text = "hello" };

        left.ShouldBe(right);
    }

    [Fact]
    public void TextContentPart_WithExpression_ShouldCreateModifiedCopy()
    {
        var original = new TextContentPart { MimeType = "text/plain", Text = "hello" };

        var updated = original with { Text = "updated" };

        updated.MimeType.ShouldBe("text/plain");
        updated.Text.ShouldBe("updated");
        original.Text.ShouldBe("hello");
    }

    [Fact]
    public void BinaryContentPart_Constructor_WithRequiredProperties_ShouldPreserveValues()
    {
        var data = new byte[] { 1, 2, 3 };
        var part = new BinaryContentPart
        {
            MimeType = "audio/wav",
            Data = data
        };

        part.MimeType.ShouldBe("audio/wav");
        part.Data.ShouldBeSameAs(data);
        part.FileName.ShouldBeNull();
    }

    [Fact]
    public void BinaryContentPart_RecordEquality_WithSameArrayReference_ShouldBeEqual()
    {
        var shared = new byte[] { 1, 2, 3 };
        var left = new BinaryContentPart { MimeType = "application/octet-stream", Data = shared };
        var right = new BinaryContentPart { MimeType = "application/octet-stream", Data = shared };

        left.ShouldBe(right);
    }

    [Fact]
    public void BinaryContentPart_RecordEquality_WithDifferentArrayInstancesSameValues_ShouldNotBeEqual()
    {
        var left = new BinaryContentPart { MimeType = "application/octet-stream", Data = [1, 2, 3] };
        var right = new BinaryContentPart { MimeType = "application/octet-stream", Data = [1, 2, 3] };

        left.ShouldNotBe(right);
    }

    [Fact]
    public void ReferenceContentPart_Constructor_WithRequiredProperties_ShouldPreserveValues()
    {
        var part = new ReferenceContentPart
        {
            MimeType = "image/png",
            Uri = "https://example.invalid/image.png"
        };

        part.MimeType.ShouldBe("image/png");
        part.Uri.ShouldBe("https://example.invalid/image.png");
        part.SizeBytes.ShouldBeNull();
        part.FileName.ShouldBeNull();
    }

    [Fact]
    public void ReferenceContentPart_RecordEquality_WithSameValues_ShouldBeEqual()
    {
        var left = new ReferenceContentPart
        {
            MimeType = "image/png",
            Uri = "https://example.invalid/image.png",
            SizeBytes = 123,
            FileName = "image.png"
        };
        var right = new ReferenceContentPart
        {
            MimeType = "image/png",
            Uri = "https://example.invalid/image.png",
            SizeBytes = 123,
            FileName = "image.png"
        };

        left.ShouldBe(right);
    }

    [Fact]
    public void MessageContentPart_Polymorphism_ShouldSupportAllDerivedTypesInSingleList()
    {
        MessageContentPart text = new TextContentPart { MimeType = "text/plain", Text = "hello" };
        MessageContentPart binary = new BinaryContentPart { MimeType = "application/octet-stream", Data = [1] };
        MessageContentPart reference = new ReferenceContentPart { MimeType = "image/png", Uri = "https://example.invalid/x.png" };

        IReadOnlyList<MessageContentPart> parts = [text, binary, reference];

        text.ShouldBeAssignableTo<MessageContentPart>();
        binary.ShouldBeAssignableTo<MessageContentPart>();
        reference.ShouldBeAssignableTo<MessageContentPart>();
        parts.Count().ShouldBe(3);
        parts[0].ShouldBeOfType<TextContentPart>();
        parts[1].ShouldBeOfType<BinaryContentPart>();
        parts[2].ShouldBeOfType<ReferenceContentPart>();
    }
}
