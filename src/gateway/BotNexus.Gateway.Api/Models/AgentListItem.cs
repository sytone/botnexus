namespace BotNexus.Gateway.Api.Models;

using BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Lean list-view projection of <see cref="AgentDescriptor"/> returned by
/// <c>GET /api/agents</c> (issue #2755).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> <c>GET /api/agents</c> previously serialised the full
/// <see cref="AgentDescriptor"/> domain model — 36 properties per agent — while every
/// portal boot-path consumer reads a strict subset. Measured against the live gateway with
/// 18 agents registered: 37,881 B full vs 4,169 B projected, an 89% reduction. Because the
/// domain model was on the wire directly, every property added to <see cref="AgentDescriptor"/>
/// silently widened the portal's cold-boot payload; the brotli wire size grew 8,007 B → 14,006 B
/// in two weeks with no compression regression. This DTO makes the list contract explicit so
/// widening it becomes a deliberate edit rather than a side effect.
/// </para>
/// <para>
/// <b>Security.</b> The full descriptor carries <c>systemPrompt</c>, <c>fileAccess</c>
/// (allowed/denied path lists), <c>toolPolicy</c>, <c>memory</c> and <c>extensionConfig</c> —
/// configuration detail that was being broadcast on a list call that is unauthenticated by
/// default (see #506). None of those fields appear here.
/// </para>
/// <para>
/// <b>Field set.</b> The first five fields mirror <c>AgentSummary</c> in the Blazor client
/// (<c>HubContracts.cs:18-23</c>) so the wire contract matches what the client already
/// deserialises rather than introducing a third shape. <see cref="ApiProvider"/> and
/// <see cref="ModelId"/> are additions, each justified by a current consumer that
/// demonstrably reads it: <c>Pages/Agents.razor:92-93</c> renders them as table columns in
/// the agent-management grid, binding them via its local <c>AgentRow</c> record
/// (<c>Agents.razor:119-125</c>). No other field survives that test — notably
/// <c>AgentRow.SystemPrompt</c> is declared but never rendered or read, so it is deliberately
/// not projected.
/// </para>
/// <para>
/// Callers needing the complete shape use <c>GET /api/agents/{agentId}</c>, which continues to
/// return the full <see cref="AgentDescriptor"/>.
/// </para>
/// </remarks>
/// <param name="AgentId">Stable agent identifier.</param>
/// <param name="DisplayName">Human-readable name shown in pickers and the sidebar.</param>
/// <param name="Emoji">Optional emoji rendered beside the display name.</param>
/// <param name="Description">Optional short description shown in the agent list.</param>
/// <param name="IsBuiltIn">Whether this is a built-in platform archetype agent.</param>
/// <param name="ApiProvider">Provider instance key; rendered as a column by <c>Agents.razor:92</c>.</param>
/// <param name="ModelId">Model identifier; rendered as a column by <c>Agents.razor:93</c>.</param>
public sealed record AgentListItem(
    string AgentId,
    string DisplayName,
    string? Emoji,
    string? Description,
    bool IsBuiltIn,
    string ApiProvider,
    string ModelId)
{
    /// <summary>
    /// Projects a full <see cref="AgentDescriptor"/> down to its list-view fields.
    /// </summary>
    /// <remarks>
    /// This is the single projection seam. Adding a field here widens the boot payload for every
    /// portal cold load and reconnect, so add one only alongside a named consumer that reads it.
    /// </remarks>
    public static AgentListItem FromDescriptor(AgentDescriptor descriptor) => new(
        descriptor.AgentId.Value,
        descriptor.DisplayName,
        descriptor.Emoji,
        descriptor.Description,
        descriptor.IsBuiltIn,
        descriptor.ApiProvider,
        descriptor.ModelId);
}
