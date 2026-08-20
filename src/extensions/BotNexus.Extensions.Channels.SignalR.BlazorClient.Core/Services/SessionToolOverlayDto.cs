using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Portal-side mirror of the gateway's <c>SessionToolOverride</c> wire shape (issue #2523), used by
/// the conversation tool-overlay control (issue #3271).
/// </summary>
/// <remarks>
/// <para>
/// This is a duplicate of the gateway record rather than a reference to it, for the same reason
/// <c>AgentDescriptorDto</c> is: the Blazor client cannot take a project reference on the gateway
/// assembly. The property names below are the CONTRACT - <c>enabledTools</c> / <c>disabledTools</c>
/// must match <c>SessionToolOverride</c> exactly or a portal write becomes unreadable by the
/// resolver.
/// </para>
/// <para>
/// <b>Narrowing only.</b> Nothing in this type can widen an agent's tool set:
/// <see cref="Project"/> intersects with the agent's configured tools and reports anything it had
/// to drop as REFUSED, matching <c>SessionToolOverrideResolver</c>. The portal deliberately has no
/// path that emits a name the agent does not have - granting a tool is an agent-configuration
/// change (<c>toolIds</c>) carrying different authority.
/// </para>
/// </remarks>
public sealed record SessionToolOverlayDto
{
    /// <summary>When non-empty, the session is narrowed to (at most) these tools.</summary>
    [JsonPropertyName("enabledTools")]
    public IReadOnlyList<string>? EnabledTools { get; init; }

    /// <summary>Tools dropped for this session. Applied after <see cref="EnabledTools"/>.</summary>
    [JsonPropertyName("disabledTools")]
    public IReadOnlyList<string>? DisabledTools { get; init; }

    /// <summary>Whether this overlay expresses any restriction at all.</summary>
    [JsonIgnore]
    public bool HasRestrictions =>
        EnabledTools is { Count: > 0 } || DisabledTools is { Count: > 0 };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serialises this overlay for the override endpoint.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Parses a persisted overlay, returning <see langword="null"/> for absent, blank, or CORRUPT
    /// JSON. This mirrors <c>SessionToolOverride.FromJson</c>, which fails OPEN: if the gateway will
    /// treat an unreadable overlay as "no restriction", the portal must render the same conclusion
    /// rather than display a restriction that is not actually in force.
    /// </summary>
    public static SessionToolOverlayDto? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SessionToolOverlayDto>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves this overlay against an agent's configured tool set, producing exactly what the
    /// portal renders. Kept as a pure function so the display and the write path cannot disagree.
    /// </summary>
    /// <param name="agentTools">The agent's configured tool names - the ceiling, never exceeded.</param>
    /// <param name="overlay">The persisted overlay, or <see langword="null"/> for none.</param>
    public static SessionToolOverlayProjection Project(
        IReadOnlyList<string> agentTools,
        SessionToolOverlayDto? overlay)
    {
        ArgumentNullException.ThrowIfNull(agentTools);

        var available = new HashSet<string>(agentTools, StringComparer.OrdinalIgnoreCase);

        if (overlay is null || !overlay.HasRestrictions)
            return new SessionToolOverlayProjection(agentTools, [], [], [], IsRestricted: false);

        var enabled = Normalize(overlay.EnabledTools);
        var disabled = Normalize(overlay.DisabledTools);

        // A requested ENABLE for a tool the agent does not have is a widening attempt. It is
        // REFUSED - reported so the operator can see it was rejected, never folded into the
        // resolved set where it would read as granted.
        var refused = overlay.EnabledTools is null
            ? []
            : overlay.EnabledTools
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Where(t => !available.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var resolved = new List<string>(agentTools.Count);
        foreach (var tool in agentTools)
        {
            if (enabled.Count > 0 && !enabled.Contains(tool))
                continue;
            if (disabled.Contains(tool))
                continue;
            resolved.Add(tool);
        }

        // The displayed lists are intersected with the agent set so the portal never shows a name
        // the agent does not have as though it were part of the effective configuration.
        var narrowedTo = enabled.Count > 0
            ? agentTools.Where(enabled.Contains).ToList()
            : (IReadOnlyList<string>)[];
        var dropped = disabled.Count > 0
            ? agentTools.Where(disabled.Contains).ToList()
            : (IReadOnlyList<string>)[];

        return new SessionToolOverlayProjection(resolved, narrowedTo, dropped, refused, IsRestricted: true);
    }

    /// <summary>
    /// Builds the overlay to persist for a chosen set of tools. Emits <c>enabledTools</c> only,
    /// and only for names present in <paramref name="agentTools"/>, so a portal write is
    /// structurally incapable of naming a tool the agent lacks. Returns <see langword="null"/> when
    /// every configured tool is selected - "no restriction" is represented by an ABSENT overlay,
    /// not by an overlay that happens to list everything.
    /// </summary>
    /// <param name="agentTools">The agent's configured tool names.</param>
    /// <param name="selected">The tools the operator left enabled.</param>
    public static SessionToolOverlayDto? ForSelection(
        IReadOnlyList<string> agentTools,
        IEnumerable<string> selected)
    {
        ArgumentNullException.ThrowIfNull(agentTools);
        ArgumentNullException.ThrowIfNull(selected);

        var chosen = Normalize([.. selected]);
        var kept = agentTools.Where(chosen.Contains).ToList();

        return kept.Count == agentTools.Count ? null : new SessionToolOverlayDto { EnabledTools = kept };
    }

    private static HashSet<string> Normalize(IReadOnlyList<string>? values)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
            return set;

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                set.Add(value.Trim());
        }

        return set;
    }
}

/// <summary>
/// What the portal renders for a conversation's tool overlay.
/// </summary>
/// <param name="ResolvedTools">The effective tool set, in the agent set's order.</param>
/// <param name="NarrowedTo">The persisted narrowed-to list, intersected with the agent set.</param>
/// <param name="DisabledTools">The persisted disabled list, intersected with the agent set.</param>
/// <param name="RefusedTools">Names the overlay asked to enable that the agent does not have.</param>
/// <param name="IsRestricted">Whether any restriction is in force.</param>
public readonly record struct SessionToolOverlayProjection(
    IReadOnlyList<string> ResolvedTools,
    IReadOnlyList<string> NarrowedTo,
    IReadOnlyList<string> DisabledTools,
    IReadOnlyList<string> RefusedTools,
    bool IsRestricted);
