using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Models;

public class ContentBlockTests
{
    [Fact]
    public void TextContent_Creation_SetsProperties()
    {
        var block = new TextContent("Hello world");

        block.Text.Should().Be("Hello world");
        block.TextSignature.Should().BeNull();
    }

    [Fact]
    public void TextContent_WithSignature_SetsSignature()
    {
        var block = new TextContent("Hello", "sig123");

        block.TextSignature.Should().Be("sig123");
    }

    [Fact]
    public void ThinkingContent_Creation_SetsProperties()
    {
        var block = new ThinkingContent("reasoning here");

        block.Thinking.Should().Be("reasoning here");
        block.ThinkingSignature.Should().BeNull();
        block.Redacted.Should().BeNull();
    }

    [Fact]
    public void ThinkingContent_WithSignature_SetsSignature()
    {
        var block = new ThinkingContent("reasoning", "sig456");

        block.ThinkingSignature.Should().Be("sig456");
    }

    [Fact]
    public void ThinkingContent_WithRedaction_SetsRedacted()
    {
        var block = new ThinkingContent("redacted data", Redacted: true);

        block.Redacted.Should().BeTrue();
    }

    [Fact]
    public void ImageContent_Creation_SetsProperties()
    {
        var block = new ImageContent("base64data==", "image/png");

        block.Data.Should().Be("base64data==");
        block.MimeType.Should().Be("image/png");
    }

    [Fact]
    public void ToolCallContent_Creation_SetsProperties()
    {
        var args = new Dictionary<string, object?> { ["path"] = "/tmp" };
        var block = new ToolCallContent("tc-1", "read_file", args);

        block.Id.Should().Be("tc-1");
        block.Name.Should().Be("read_file");
        block.Arguments.Should().ContainKey("path");
        block.ThoughtSignature.Should().BeNull();
    }

    [Fact]
    public void ContentBlock_Serialization_RoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        ContentBlock original = new TextContent("hello");

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ContentBlock>(json, options);

        deserialized.Should().BeOfType<TextContent>();
        ((TextContent)deserialized!).Text.Should().Be("hello");
    }

    [Fact]
    public void ContentBlock_ThinkingDeserialization_RoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        ContentBlock original = new ThinkingContent("deep thought", "sig");

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ContentBlock>(json, options);

        deserialized.Should().BeOfType<ThinkingContent>();
        var thinking = (ThinkingContent)deserialized!;
        thinking.Thinking.Should().Be("deep thought");
        thinking.ThinkingSignature.Should().Be("sig");
    }

    [Fact]
    public void ContentBlock_ImageDeserialization_RoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        ContentBlock original = new ImageContent("abc123", "image/jpeg");

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ContentBlock>(json, options);

        deserialized.Should().BeOfType<ImageContent>();
        var image = (ImageContent)deserialized!;
        image.Data.Should().Be("abc123");
        image.MimeType.Should().Be("image/jpeg");
    }

    [Fact]
    public void ContentBlock_ToolCallDeserialization_RoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var args = new Dictionary<string, object?> { ["key"] = "value" };
        ContentBlock original = new ToolCallContent("id1", "my_tool", args);

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ContentBlock>(json, options);

        deserialized.Should().BeOfType<ToolCallContent>();
        ((ToolCallContent)deserialized!).Name.Should().Be("my_tool");
    }
}
