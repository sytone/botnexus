using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Tests.Models;

public class ContentBlockTests
{
    [Fact]
    public void TextContent_Creation_SetsProperties()
    {
        var block = new TextContent("Hello world");

        block.Text.ShouldBe("Hello world");
        block.TextSignature.ShouldBeNull();
    }

    [Fact]
    public void TextContent_WithSignature_SetsSignature()
    {
        var block = new TextContent("Hello", "sig123");

        block.TextSignature.ShouldBe("sig123");
    }

    [Fact]
    public void ThinkingContent_Creation_SetsProperties()
    {
        var block = new ThinkingContent("reasoning here");

        block.Thinking.ShouldBe("reasoning here");
        block.ThinkingSignature.ShouldBeNull();
        block.Redacted.ShouldBeNull();
    }

    [Fact]
    public void ThinkingContent_WithSignature_SetsSignature()
    {
        var block = new ThinkingContent("reasoning", "sig456");

        block.ThinkingSignature.ShouldBe("sig456");
    }

    [Fact]
    public void ThinkingContent_WithRedaction_SetsRedacted()
    {
        var block = new ThinkingContent("redacted data", Redacted: true);

        block.Redacted.ShouldBe(true);
    }

    [Fact]
    public void ImageContent_Creation_SetsProperties()
    {
        var block = new ImageContent("base64data==", "image/png");

        block.Data.ShouldBe("base64data==");
        block.MimeType.ShouldBe("image/png");
    }

    [Fact]
    public void ToolCallContent_Creation_SetsProperties()
    {
        var args = new Dictionary<string, object?> { ["path"] = "/tmp" };
        var block = new ToolCallContent("tc-1", "read_file", args);

        block.Id.ShouldBe("tc-1");
        block.Name.ShouldBe("read_file");
        block.Arguments.ShouldContainKey("path");
        block.ThoughtSignature.ShouldBeNull();
    }

    [Fact]
    public void ContentBlock_Serialization_RoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        ContentBlock original = new TextContent("hello");

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ContentBlock>(json, options);

        deserialized.ShouldBeOfType<TextContent>();
        ((TextContent)deserialized!).Text.ShouldBe("hello");
    }

    [Fact]
    public void ContentBlock_ThinkingDeserialization_RoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        ContentBlock original = new ThinkingContent("deep thought", "sig");

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ContentBlock>(json, options);

        deserialized.ShouldBeOfType<ThinkingContent>();
        var thinking = (ThinkingContent)deserialized!;
        thinking.Thinking.ShouldBe("deep thought");
        thinking.ThinkingSignature.ShouldBe("sig");
    }

    [Fact]
    public void ContentBlock_ImageDeserialization_RoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        ContentBlock original = new ImageContent("abc123", "image/jpeg");

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ContentBlock>(json, options);

        deserialized.ShouldBeOfType<ImageContent>();
        var image = (ImageContent)deserialized!;
        image.Data.ShouldBe("abc123");
        image.MimeType.ShouldBe("image/jpeg");
    }

    [Fact]
    public void ContentBlock_ToolCallDeserialization_RoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var args = new Dictionary<string, object?> { ["key"] = "value" };
        ContentBlock original = new ToolCallContent("id1", "my_tool", args);

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ContentBlock>(json, options);

        deserialized.ShouldBeOfType<ToolCallContent>();
        ((ToolCallContent)deserialized!).Name.ShouldBe("my_tool");
    }
}
