using System.Text.Json;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.OpenAI.Tests;

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

        tool.Name.ShouldBe("read_file");
        tool.Description.ShouldBe("Read a file from disk");
        tool.Parameters.GetProperty("type").GetString().ShouldBe("object");
        tool.Parameters.GetProperty("required").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public void StrictMode_WhenSupported_IsTrue()
    {
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = true };

        compat.SupportsStrictMode.ShouldBeTrue();
    }

    [Fact]
    public void StrictMode_WhenNotSupported_IsFalse()
    {
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = false };

        compat.SupportsStrictMode.ShouldBeFalse();
    }
}
