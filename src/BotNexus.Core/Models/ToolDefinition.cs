namespace BotNexus.Core.Models;

/// <summary>A JSON schema property definition for a tool parameter.</summary>
public record ToolParameterSchema(
    string Type,
    string Description,
    bool Required = false,
    IReadOnlyList<string>? EnumValues = null);

/// <summary>A tool that can be called by the LLM.</summary>
public record ToolDefinition(
    string Name,
    string Description,
    IReadOnlyDictionary<string, ToolParameterSchema> Parameters);
