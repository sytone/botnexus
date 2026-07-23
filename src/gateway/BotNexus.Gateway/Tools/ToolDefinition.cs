using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// A user-defined portal tool: a named, ordered launcher for an external URL that roams
/// with the user across browsers and devices because it is persisted server-side.
/// </summary>
/// <remarks>
/// This is the foundation model for the portal Tools feature (#2232, slice 1 of #2231).
/// It is intentionally minimal - later slices layer UI and richer behaviour on top of this
/// stable persisted shape.
/// </remarks>
public sealed record ToolDefinition
{
    /// <summary>Stable identifier for the tool. Supplied by the caller (client-generated).</summary>
    public required ToolId Id { get; init; }

    /// <summary>Human-readable display name shown in the portal.</summary>
    public required string Name { get; init; }

    /// <summary>Target URL the tool launches.</summary>
    public required string Url { get; init; }

    /// <summary>
    /// Icon shown alongside the tool. Typically a single emoji or character string; may be
    /// empty when no icon is chosen.
    /// </summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Sort order within the tool list (ascending).</summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether the tool opens inside a sandboxed frame. Defaults to <c>true</c> so newly
    /// created tools are isolated unless the user explicitly opts out.
    /// </summary>
    public bool SandboxEnabled { get; init; } = true;

    /// <summary>When the tool was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
