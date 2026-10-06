namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>
/// Transforms a completed tool result before it reaches the provider.
/// </summary>
/// <param name="context">The tool-result transformation context.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>Replacement fields, or null to retain the original result and error flag.</returns>
/// <remarks>
/// Only non-null Content, Details, and IsError fields replace their original values.
/// Non-cancellation exceptions retain the original result and error flag; cancellation propagates.
/// </remarks>
public delegate Task<ToolResultTransformResult?> ToolResultTransformer(
    ToolResultTransformContext context,
    CancellationToken cancellationToken);