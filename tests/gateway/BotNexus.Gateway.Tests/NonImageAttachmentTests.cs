using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Regression tests for #2294: paperclip (non-image) file attachments were silently dropped by
/// the gateway. <see cref="GatewayHost.BuildUserMessage"/> only consumed image content parts via
/// <c>BuildImageContent</c>; text and non-image binary parts never reached the agent message.
/// <see cref="GatewayHost.AppendNonImageAttachments"/> folds those parts into the user message
/// text so they are delivered (parity with the working image-paste path, which stays on the
/// vision path and is intentionally NOT inlined here).
/// </summary>
public sealed class NonImageAttachmentTests
{
    [Fact]
    public void AppendNonImageAttachments_WhenNoParts_ReturnsContentUnchanged()
    {
        var result = GatewayHost.AppendNonImageAttachments("hello", contentParts: null);
        result.ShouldBe("hello");

        var empty = GatewayHost.AppendNonImageAttachments("hello", []);
        empty.ShouldBe("hello");
    }

    [Fact]
    public void AppendNonImageAttachments_WhenTextFileAttached_InlinesContentIntoMessage()
    {
        // A .log (text/plain) paperclip upload arrives as a TextContentPart.
        var parts = new MessageContentPart[]
        {
            new TextContentPart { MimeType = "text/plain", Text = "line one\nline two" },
        };

        var result = GatewayHost.AppendNonImageAttachments("see attached log", parts);

        result.ShouldContain("see attached log");
        result.ShouldContain("<attachment mimeType=\"text/plain\">");
        result.ShouldContain("line one\nline two");
        result.ShouldContain("</attachment>");
    }

    [Fact]
    public void AppendNonImageAttachments_WhenNonImageBinaryAttached_ReferencesFileMetadata()
    {
        var parts = new MessageContentPart[]
        {
            new BinaryContentPart
            {
                MimeType = "application/pdf",
                Data = new byte[] { 1, 2, 3, 4, 5 },
                FileName = "report.pdf",
            },
        };

        var result = GatewayHost.AppendNonImageAttachments("here", parts);

        result.ShouldContain("here");
        result.ShouldContain("fileName=\"report.pdf\"");
        result.ShouldContain("mimeType=\"application/pdf\"");
        result.ShouldContain("sizeBytes=\"5\"");
    }

    [Fact]
    public void AppendNonImageAttachments_WhenImageBinaryAttached_IsSkipped()
    {
        // Pasted images must stay on the vision path (BuildImageContent), not be inlined as text.
        var parts = new MessageContentPart[]
        {
            new BinaryContentPart
            {
                MimeType = "image/png",
                Data = new byte[] { 9, 9, 9 },
                FileName = "paste.png",
            },
        };

        var result = GatewayHost.AppendNonImageAttachments("look", parts);

        result.ShouldBe("look");
    }

    [Fact]
    public void AppendNonImageAttachments_MixedParts_InlinesOnlyNonImageParts()
    {
        var parts = new MessageContentPart[]
        {
            new TextContentPart { MimeType = "text/plain", Text = "log data" },
            new BinaryContentPart { MimeType = "image/png", Data = new byte[] { 1 }, FileName = "img.png" },
            new BinaryContentPart { MimeType = "application/octet-stream", Data = new byte[] { 1, 2 }, FileName = "blob.bin" },
        };

        var result = GatewayHost.AppendNonImageAttachments("msg", parts);

        result.ShouldContain("log data");
        result.ShouldContain("blob.bin");
        result.ShouldNotContain("img.png");
    }
}
