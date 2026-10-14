using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>
/// Defines the override result returned from tool result transformation.
/// </summary>
/// <param name="Content">Optional replacement content blocks (overrides Result.Content if set).</param>
/// <param name="Details">Optional replacement metadata (overrides Result.Details if set).</param>
/// <param name="IsError">Optional override for the error flag (changes ToolResultAgentMessage.IsError).</param>
/// <remarks>
/// Return from ToolResultTransformer to transform the tool result.
/// Only non-null fields are applied — null fields leave the original value unchanged.
/// Use to redact sensitive data, inject additional content, or convert errors to warnings.
/// </remarks>
public record ToolResultTransformResult(
    IReadOnlyList<AgentToolContent>? Content = null,
    object? Details = null,
    bool? IsError = null);