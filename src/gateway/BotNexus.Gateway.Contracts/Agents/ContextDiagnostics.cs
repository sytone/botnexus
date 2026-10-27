namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>Heuristic text accounting for the current live agent state, not provider usage.</summary>
/// <remarks>
/// Token estimates use script-weighted units and independently truncate each subtotal and total.
/// Below integer saturation, the total minus the sum of system prompt, tool definitions and history
/// is 0–2 tokens; history minus its userAssistant and toolResult children is 0–1 token. Using the
/// four disjoint leaves instead gives a 0–3 token tolerance. Do not add history to its children.
/// Skills embedded in prompt or history text remain in that enclosing category and are counted
/// once; this snapshot does not establish separate skill attribution.
/// </remarks>
public sealed record ContextDiagnostics
{
    public int SystemPromptChars { get; init; }
    public int SystemPromptTokens { get; init; }
    public int ToolCount { get; init; }
    /// <summary>Raw characters across tool names, descriptions and parameter schema JSON text.</summary>
    public int ToolDefinitionChars { get; init; }

    /// <summary>Script-aware estimate over the same complete tool-definition text as the total.</summary>
    public int ToolDefinitionTokens { get; init; }
    public IReadOnlyList<ToolDiagInfo> Tools { get; init; } = [];
    public int HistoryEntryCount { get; init; }
    public int HistoryChars { get; init; }
    public int HistoryTokens { get; init; }

    /// <summary>History text characters, including system and sub-agent completion messages, excluding tool results.</summary>
    public int UserAssistantChars { get; init; }

    /// <summary>Estimated history text tokens, including system and sub-agent completion messages, excluding tool results.</summary>
    public int UserAssistantTokens { get; init; }

    /// <summary>Chars contributed by tool result messages in history.</summary>
    public int ToolResultChars { get; init; }

    /// <summary>Estimated tokens for tool result messages in history.</summary>
    public int ToolResultTokens { get; init; }

    public int TotalEstimatedTokens { get; init; }
    public string? SystemPrompt { get; init; }
}

public sealed record ToolDiagInfo(string Name, string? Description, int SchemaChars);
