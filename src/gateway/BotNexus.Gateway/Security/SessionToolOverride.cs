using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Security;

/// <summary>
/// A session-scoped narrowing overlay over the agent's configured tool set (issue #2523).
/// </summary>
/// <remarks>
/// <para>
/// Persisted on the conversation row as opaque JSON so the restriction survives reconnect, a new
/// session in the same conversation, and a gateway restart. Both lists are optional and
/// <c>null</c>/empty means "no opinion" - an absent overlay resolves to the agent set unchanged, so
/// every existing conversation behaves exactly as before.
/// </para>
/// <para>
/// This is an AVAILABILITY axis and is deliberately distinct from <c>ToolPolicyConfig</c>, which is
/// an APPROVAL axis (risk level / never-approve). A tool removed here is never offered to the model
/// at all, so there is nothing to approve.
/// </para>
/// </remarks>
public sealed record SessionToolOverride
{
    /// <summary>
    /// When non-empty, the session is narrowed to (at most) these tools. Names the agent does not
    /// have are REFUSED, never granted - see <see cref="SessionToolOverrideResolver"/>.
    /// </summary>
    [JsonPropertyName("enabledTools")]
    public IReadOnlyList<string>? EnabledTools { get; init; }

    /// <summary>
    /// Tools to drop for this session. Applied after <see cref="EnabledTools"/>, so a tool named in
    /// both is dropped: a contradiction resolves toward the narrower outcome.
    /// </summary>
    [JsonPropertyName("disabledTools")]
    public IReadOnlyList<string>? DisabledTools { get; init; }

    /// <summary>Gets a value indicating whether this overlay expresses any restriction at all.</summary>
    [JsonIgnore]
    public bool HasRestrictions =>
        EnabledTools is { Count: > 0 } || DisabledTools is { Count: > 0 };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serialises this overlay for persistence on the conversation row.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Parses a persisted overlay. Returns <see langword="null"/> for absent, blank, or CORRUPT
    /// JSON: an unreadable restriction must not throw on the agent-construction path, and falling
    /// back to "no overlay" keeps the conversation usable. Note this fails OPEN by design - the
    /// overlay is a convenience lever for blast-radius reduction, not the security boundary. The
    /// agent's configured tool set remains the actual boundary and is unaffected by a parse failure.
    /// </summary>
    public static SessionToolOverride? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SessionToolOverride>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The outcome of applying a <see cref="SessionToolOverride"/> to an agent's configured tool set.
/// </summary>
/// <param name="Tools">
/// The resolved tool names, in the agent set's original order. This is the authority - a caller
/// must use this list rather than re-deriving availability from the overlay.
/// </param>
/// <param name="RefusedTools">
/// Names the overlay asked to ENABLE that the agent does not have. Surfaced so the refusal is
/// observable (and reportable to the operator) instead of silently swallowed - the #3244 lesson
/// that an unrepresentable outcome is an unfixable defect.
/// </param>
/// <param name="IsNarrowed">Whether the resolved set is strictly smaller than the agent set.</param>
public readonly record struct SessionToolResolution(
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> RefusedTools,
    bool IsNarrowed);

/// <summary>
/// Applies a session-scoped tool overlay to an agent's configured tool set (issue #2523).
/// </summary>
/// <remarks>
/// <para>
/// <b>Narrowing-only.</b> The resolved set is always a SUBSET of the agent's configured set. This is
/// the whole security property of the feature: if a session could add a tool, then anyone able to
/// write a conversation override could grant themselves <c>exec</c> on an agent deliberately
/// configured without it, and the overlay would be a privilege-escalation seam rather than a
/// blast-radius control. Widening is therefore structurally impossible here - the implementation
/// only ever removes from <c>agentTools</c>, never appends - and is pinned by
/// <c>SessionToolOverrideResolverTests</c> on the resolved list.
/// </para>
/// <para>
/// Widening remains available through the mechanism that already carries the right authority:
/// editing the agent's own <c>toolIds</c>.
/// </para>
/// </remarks>
public static class SessionToolOverrideResolver
{
    /// <summary>
    /// Resolves the effective tool set for a session.
    /// </summary>
    /// <param name="agentTools">The agent's configured tool names - the ceiling, never exceeded.</param>
    /// <param name="overrides">The session overlay, or <see langword="null"/> for none.</param>
    /// <param name="isPinned">
    /// Optional predicate identifying runtime-pinned tools that must not be dropped. Mirrors
    /// <see cref="DefaultToolPolicyProvider.IsPinned"/> so this overlay and the deny-list path agree
    /// on which tools are load-bearing. A pinned tool is exempt from REMOVAL only; it is still never
    /// added to an agent that does not have it.
    /// </param>
    public static SessionToolResolution Resolve(
        IReadOnlyList<string> agentTools,
        SessionToolOverride? overrides,
        Func<string, bool>? isPinned = null)
    {
        ArgumentNullException.ThrowIfNull(agentTools);

        if (overrides is null || !overrides.HasRestrictions)
            return new SessionToolResolution(agentTools, [], IsNarrowed: false);

        var available = new HashSet<string>(agentTools, StringComparer.OrdinalIgnoreCase);
        var enabled = Normalize(overrides.EnabledTools);
        var disabled = Normalize(overrides.DisabledTools);

        // A requested ENABLE for a tool the agent does not have is a widening attempt: record it as
        // refused and drop it. It is never added to the resolved set.
        var refused = enabled.Where(tool => !available.Contains(tool))
            .ToList();

        var resolved = new List<string>(agentTools.Count);
        foreach (var tool in agentTools)
        {
            if (resolved.Contains(tool, StringComparer.OrdinalIgnoreCase))
                continue;

            var pinned = isPinned?.Invoke(tool) == true;

            // An allow-list, when present, narrows to its intersection with the agent set. Pinned
            // tools are exempt from being dropped by an omission.
            if (enabled.Count > 0 && !enabled.Contains(tool) && !pinned)
                continue;

            // Deny wins over allow for the same tool, and over an unrestricted set. Pinned tools
            // survive - consistent with DefaultToolPolicyProvider.IsDenied.
            if (disabled.Contains(tool) && !pinned)
                continue;

            resolved.Add(tool);
        }

        return new SessionToolResolution(
            resolved,
            refused,
            IsNarrowed: resolved.Count < available.Count);
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
