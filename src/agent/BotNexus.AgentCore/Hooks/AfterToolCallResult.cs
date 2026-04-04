using BotNexus.AgentCore.Types;

namespace BotNexus.AgentCore.Hooks;

/// <summary>
/// Defines the override result returned from post-tool-call interception.
/// </summary>
/// <param name="Content">Optional replacement content blocks.</param>
/// <param name="Details">Optional replacement metadata.</param>
/// <param name="IsError">Optional override for the error flag.</param>
public record AfterToolCallResult(
    IReadOnlyList<AgentToolContent>? Content = null,
    object? Details = null,
    bool? IsError = null);
