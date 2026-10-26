using System.Text.Json;

namespace BotNexus.Gateway.Contracts.Tools;

/// <summary>Model-facing tool definition, carrying the original JSON Schema without provider dependencies.</summary>
/// <param name="Name">Routing name matching the owning tool.</param>
/// <param name="Description">Model-facing description.</param>
/// <param name="Parameters">Unmodified schema; clone before disposing the source JsonDocument.</param>
public sealed record GatewayToolDefinition(string Name, string Description, JsonElement Parameters);

/// <summary>Wire-compatible text/image result discriminators.</summary>
public enum GatewayToolContentType
{
    /// <summary>Plain text sent back to the model.</summary>
    Text,
    /// <summary>Image data URI or base64 payload, preserved verbatim.</summary>
    Image
}

/// <summary>One ordered model-visible content block; do not re-encode image or text values in adapters.</summary>
/// <param name="Type">Text or image discriminator.</param>
/// <param name="Value">Verbatim text, image data URI, or base64 payload.</param>
public sealed record GatewayToolContent(GatewayToolContentType Type, string Value);

/// <summary>Normalized tool output, with model-visible content separated from application metadata.</summary>
/// <param name="Content">Ordered text/image blocks sent to the model.</param>
/// <param name="Details">Optional JSON-native tool metadata, for hooks/logging only, not model content.</param>
/// <remarks>
/// Adapters must preserve the existing metadata's serialized property names, nested values, nulls,
/// numeric fidelity, and omission semantics using the originating boundary's serializer options.
/// Do not convert metadata into text content or wrap opaque CLR/provider objects. Typed legacy
/// metadata consumers require explicit reconstruction at the adapter boundary, not a cast to JSON.
/// A null nullable element means absent metadata; a present JSON null can be retained distinctly
/// in memory (the ordinary JSON serializer writes both as null). Clone document-backed elements
/// before their source document is disposed. IsError remains an execution-envelope concern.
/// </remarks>
public sealed record GatewayToolResult(IReadOnlyList<GatewayToolContent> Content, JsonElement? Details = null)
{
    /// <summary>Transport metadata, independent of tool Details and never injected into model content.</summary>
    public JsonElement? DeliveryDetails { get; init; }

    /// <summary>True for interruption payloads rather than an observed final tool result.</summary>
    public bool IsIncomplete { get; init; }
}

/// <summary>Synchronous partial-result notification, preserving the same payload semantics as the final result.</summary>
public delegate void GatewayToolUpdateCallback(GatewayToolResult partialResult);
