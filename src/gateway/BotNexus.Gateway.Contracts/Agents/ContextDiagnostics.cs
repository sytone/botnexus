namespace BotNexus.Gateway.Abstractions.Agents;

public sealed record ContextDiagnostics
{
    public int SystemPromptChars { get; init; }
    public int SystemPromptTokens { get; init; }
    public int ToolCount { get; init; }
    public int ToolDefinitionChars { get; init; }
    public int ToolDefinitionTokens { get; init; }
    public IReadOnlyList<ToolDiagInfo> Tools { get; init; } = [];
    public int HistoryEntryCount { get; init; }
    public int HistoryChars { get; init; }
    public int HistoryTokens { get; init; }
    public int TotalEstimatedTokens { get; init; }
    public string? SystemPrompt { get; init; }
}

public sealed record ToolDiagInfo(string Name, string? Description, int SchemaChars);
