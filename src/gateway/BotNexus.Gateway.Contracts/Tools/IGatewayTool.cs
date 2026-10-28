using System.Text.Json;

namespace BotNexus.Gateway.Contracts.Tools;

/// <summary>A gateway-owned tool boundary, independent of the agent loop and provider models.</summary>
/// <remarks>
/// Prepare runs before execute (sequentially even before parallel execution). Implementations must
/// be thread-safe when executed in parallel. Exceptions become error results at the execution
/// boundary; cancellation must flow through preparation, execution, and partial updates.
/// Optional resource ownership uses the BCL IDisposable/IAsyncDisposable contracts; adapters must
/// preserve those capabilities and prefer asynchronous disposal when both are implemented.
/// </remarks>
public interface IGatewayTool
{
    /// <summary>Model-visible routing name, matching Definition.Name; lookup is ordinal case-insensitive.</summary>
    string Name { get; }

    /// <summary>Human-readable diagnostic and UX label.</summary>
    string Label { get; }

    /// <summary>Model-visible definition; adapters must preserve the schema without rewriting it.</summary>
    GatewayToolDefinition Definition { get; }

    /// <summary>Validates, coerces, or enriches arguments before execution; failures are exceptions.</summary>
    /// <remarks>
    /// JSON null is a JsonElement with Null kind; absent dictionary keys remain absent. Adapters
    /// must preserve nested values, numeric fidelity, and argument spelling, not stringify them.
    /// Elements crossing document lifetimes must be cloned by the boundary that owns the document.
    /// </remarks>
    Task<IReadOnlyDictionary<string, JsonElement>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>Executes prepared arguments with the original correlation ID and optional synchronous updates.</summary>
    Task<GatewayToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken = default,
        GatewayToolUpdateCallback? onUpdate = null);

    /// <summary>Optional timeout floor: executors use max(configured safety cap, this hint); null defers to configuration.</summary>
    TimeSpan? DefaultTimeout => null;

    /// <summary>Explicit caller-timeout argument and unit; null opts out of all argument inspection.</summary>
    ToolTimeoutArgument? TimeoutArgument => null;

    /// <summary>Origin of returned bytes, not capability/risk. Unclassified tools taint by default.</summary>
    string ContentSource => ToolContentSource.Unknown;

    /// <summary>Optional one-line system-prompt tool-listing guidance; null contributes nothing.</summary>
    string? GetPromptSnippet() => null;

    /// <summary>Additional system-prompt guidelines, in declared order; empty contributes nothing.</summary>
    IReadOnlyList<string> GetPromptGuidelines() => [];
}
