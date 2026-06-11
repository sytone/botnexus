using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Contracts.Memory;

namespace BotNexus.Memory.Tools;

/// <summary>
/// Appends markdown notes to an agent's memory via the <see cref="IAgentMemory"/> abstraction.
/// Supports both the default daily note target and explicit file paths.
/// </summary>
public sealed class MemorySaveTool : IAgentTool
{
    private readonly IAgentMemory _agentMemory;
    private readonly string _agentId;

    public MemorySaveTool(IAgentMemory agentMemory, string agentId)
    {
        _agentMemory = agentMemory ?? throw new ArgumentNullException(nameof(agentMemory));
        _agentId = string.IsNullOrWhiteSpace(agentId)
            ? throw new ArgumentException("Agent ID is required.", nameof(agentId))
            : agentId;
    }

    public string Name => "memory_save";

    public string Label => "Memory Save";

    public Tool Definition => new(
        Name,
        "Append markdown memory notes. Use content only for today's daily note, or provide file_path for a specific note under memory root.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "content": {
                  "type": "string",
                  "description": "Content to append to the memory note"
                },
                "file_path": {
                  "type": "string",
                  "description": "Optional relative note path under memory root"
                }
              },
              "required": ["content"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetValue("content", out var contentValue) || string.IsNullOrWhiteSpace(ToStringValue(contentValue)))
            throw new ArgumentException("Missing required argument: content.");

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["content"] = ToStringValue(contentValue)!
        };

        if (arguments.TryGetValue("file_path", out var filePathValue) && !string.IsNullOrWhiteSpace(ToStringValue(filePathValue)))
            prepared["file_path"] = ToStringValue(filePathValue);

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = ToStringValue(arguments["content"])!;
        var filePath = arguments.TryGetValue("file_path", out var filePathValue)
            ? ToStringValue(filePathValue)
            : null;

        if (!string.IsNullOrWhiteSpace(filePath) && _agentMemory is MarkdownAgentMemory markdownMemory)
        {
            // File-path saves use the extended method on MarkdownAgentMemory
            await markdownMemory.SaveToFileAsync(content, filePath, cancellationToken);
        }
        else
        {
            // Default save via the abstraction
            var request = new AgentMemorySaveRequest(
                AgentId: _agentId,
                Content: content,
                SourceType: "tool");
            await _agentMemory.SaveAsync(request, cancellationToken);
        }

        var targetDescription = string.IsNullOrWhiteSpace(filePath)
            ? "default memory target"
            : filePath;
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Appended memory note to {targetDescription}.")]);
    }

    private static string? ToStringValue(object? value)
        => value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
}
