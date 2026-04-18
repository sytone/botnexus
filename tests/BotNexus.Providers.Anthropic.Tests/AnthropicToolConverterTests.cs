using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.Anthropic.Tests;

public class AnthropicToolConverterTests
{
    [Fact]
    public void Tool_HasNameDescriptionInputSchema()
    {
        var parameters = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "command": { "type": "string" }
                },
                "required": ["command"]
            }
            """).RootElement;

        var tool = new Tool("execute", "Execute a command", parameters);

        tool.Name.Should().Be("execute");
        tool.Description.Should().Be("Execute a command");
        tool.Parameters.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void Tool_WithComplexSchema_PreservesStructure()
    {
        var parameters = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "items": {
                        "type": "array",
                        "items": { "type": "string" }
                    },
                    "options": {
                        "type": "object",
                        "properties": {
                            "recursive": { "type": "boolean" }
                        }
                    }
                }
            }
            """).RootElement;

        var tool = new Tool("complex_tool", "A complex tool", parameters);

        tool.Parameters.GetProperty("properties")
            .GetProperty("items")
            .GetProperty("type")
            .GetString()
            .Should().Be("array");
    }
}
