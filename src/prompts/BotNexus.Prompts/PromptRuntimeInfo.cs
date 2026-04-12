namespace BotNexus.Prompts;

public sealed record PromptRuntimeInfo
{
    public string? AgentId { get; init; }
    public string? Host { get; init; }
    public string? Os { get; init; }
    public string? Arch { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? DefaultModel { get; init; }
    public string? Shell { get; init; }
    public string? Channel { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
}