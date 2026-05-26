namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Optional per-spawn overrides applied on top of an <see cref="Embody"/> sub-agent's
/// role defaults. Each field, when null, falls back to a sensible default derived
/// from the role or the parent agent's descriptor. Use <see cref="Default"/> for
/// "no overrides" rather than constructing an empty record by hand.
///
/// <para>By design this type is strictly pass-through — no field changes the
/// child descriptor's effective configuration today; they are surfaced via
/// <c>SubAgentInfo</c> (display name, metadata) or applied during background-turn
/// dispatch. Role-derived defaults (per-role tool sets, role-specific system
/// prompts) are scoped to GitHub issue #467 and intentionally out of scope here.</para>
/// </summary>
public sealed record EmbodyCustomizations
{
    /// <summary>
    /// Shared "no overrides" instance. Use this instead of constructing a new
    /// instance with all-null fields so equality checks and JSON round-trips
    /// remain trivial.
    /// </summary>
    public static readonly EmbodyCustomizations Default = new();

    /// <summary>
    /// Optional friendly display name surfaced on <c>SubAgentInfo.Name</c>.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Optional system-prompt override. Currently captured but not applied to
    /// the child descriptor (the parent descriptor's prompt is used). Reserved
    /// for future role-prompt work tracked in #467.
    /// </summary>
    public string? SystemPromptOverride { get; init; }

    /// <summary>
    /// Optional model override surfaced on <c>SubAgentInfo.Model</c>. Today this
    /// is metadata-only and does not change the model the child descriptor
    /// invokes; that wiring is tracked separately.
    /// </summary>
    public string? ModelOverride { get; init; }

    /// <summary>
    /// Optional API-provider override. Currently captured but not applied to
    /// the child descriptor.
    /// </summary>
    public string? ApiProviderOverride { get; init; }

    /// <summary>
    /// Optional explicit tool allowlist. When non-empty, each entry is validated
    /// against the parent agent's effective deny-list before the spawn proceeds —
    /// any tool denied to the parent is also denied to the child (privilege-
    /// escalation prevention).
    /// </summary>
    public IReadOnlyList<string>? ToolIds { get; init; }
}
