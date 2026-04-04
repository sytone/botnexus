using System.Text.Json;
using BotNexus.Providers.Core.Compatibility;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.OpenAI.Tests;

public class ToolConverterTests
{
    [Fact]
    public void Tool_HasCorrectStructure()
    {
        var parameters = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "File path" }
                },
                "required": ["path"]
            }
            """).RootElement;

        var tool = new Tool("read_file", "Read a file from disk", parameters);

        tool.Name.Should().Be("read_file");
        tool.Description.Should().Be("Read a file from disk");
        tool.Parameters.GetProperty("type").GetString().Should().Be("object");
        tool.Parameters.GetProperty("required").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void StrictMode_WhenSupported_IsTrue()
    {
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = true };

        compat.SupportsStrictMode.Should().BeTrue();
    }

    [Fact]
    public void StrictMode_WhenNotSupported_IsFalse()
    {
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = false };

        compat.SupportsStrictMode.Should().BeFalse();
    }
}
