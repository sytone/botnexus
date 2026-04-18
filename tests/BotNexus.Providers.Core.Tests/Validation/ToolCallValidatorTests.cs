using System.Text.Json;
using BotNexus.Agent.Providers.Core.Validation;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Validation;

public class ToolCallValidatorTests
{
    [Fact]
    public void Validate_WhenRequiredPropertyMissing_ReturnsError()
    {
        var arguments = JsonDocument.Parse("""{ "query": "hello" }""").RootElement.Clone();
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": { "type": "string" },
                "limit": { "type": "integer" }
              },
              "required": ["query", "limit"]
            }
            """).RootElement.Clone();

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.Contains("Missing required property 'limit'"));
    }

    [Fact]
    public void Validate_WhenTypeMismatches_ReturnsError()
    {
        var arguments = JsonDocument.Parse("""{ "timeout": "fast" }""").RootElement.Clone();
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "timeout": { "type": "integer" }
              }
            }
            """).RootElement.Clone();

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.Contains("Property 'timeout' must be of type integer"));
    }

    [Fact]
    public void Validate_WhenEnumValueInvalid_ReturnsError()
    {
        var arguments = JsonDocument.Parse("""{ "mode": "slow" }""").RootElement.Clone();
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "mode": {
                  "type": "string",
                  "enum": ["fast", "balanced"]
                }
              }
            }
            """).RootElement.Clone();

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.Contains("Property 'mode' must be one of the allowed enum values"));
    }

    [Fact]
    public void Validate_WhenValidArguments_ReturnsSuccess()
    {
        var arguments = JsonDocument.Parse("""{ "query": "hello", "count": 3, "extra": true }""").RootElement.Clone();
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": { "type": "string" },
                "count": { "type": "integer" }
              },
              "required": ["query"]
            }
            """).RootElement.Clone();

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenSchemaIsUndefined_ReturnsSuccess()
    {
        var arguments = JsonDocument.Parse("""{ "anything": "goes" }""").RootElement.Clone();
        var schema = default(JsonElement);

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenSchemaHasNoRules_ReturnsSuccess()
    {
        var arguments = JsonDocument.Parse("""{ "foo": 1, "bar": "text" }""").RootElement.Clone();
        var schema = JsonDocument.Parse("""{ }""").RootElement.Clone();

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenAdditionalPropertiesPresent_DoesNotReject()
    {
        var arguments = JsonDocument.Parse("""{ "required": "ok", "unexpected": 42 }""").RootElement.Clone();
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "required": { "type": "string" }
              },
              "required": ["required"]
            }
            """).RootElement.Clone();

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }
}
