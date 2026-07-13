namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;

/// <summary>
/// The shared slash-command registry consumed by the chat command palette across the desktop
/// and mobile SignalR Blazor clients. Lifted from the desktop <c>ChatPanel.razor</c> inline list
/// so both clients expose the same full command surface (issue #1949, part of #1580).
/// </summary>
/// <remarks>
/// Per Jon's confirmed direction (2026-06-24) the registry exposes the full command surface,
/// not just the four original quick actions. Commands that the gateway command pipeline owns
/// (<c>/help</c>, <c>/status</c>, <c>/agents</c>, <c>/context</c>, <c>/model</c>,
/// <c>/reasoning</c>, <c>/prompts</c>) are dispatched by sending the command text to the agent
/// via <see cref="IAgentInteractionService.SendMessageAsync"/>; client-side actions
/// (<c>/new</c>, <c>/compact</c>, <c>/clear</c>) call the dedicated interaction methods directly,
/// preserving the exact desktop behaviour.
/// </remarks>
public static class SlashCommandRegistry
{
    /// <summary>
    /// The full ordered set of slash commands. Ordering drives palette presentation: the original
    /// desktop quick actions appear first, followed by the remaining gateway command surface.
    /// </summary>
    public static readonly IReadOnlyList<SlashCommand> All =
    [
        // ── Original desktop quick actions (behaviour preserved verbatim) ──
        new("/new", "Reset session and start fresh", SlashCommandKind.ResetSession),
        new("/compact", "Compact session to reduce tokens", SlashCommandKind.CompactSession),
        new("/clear", "Clear local messages", SlashCommandKind.ClearLocalMessages),
        new("/prompts", "Browse reusable prompt templates", SlashCommandKind.SendToAgent),

        // ── Remaining gateway command surface (handled by the gateway pipeline) ──
        new("/help", "List all available commands", SlashCommandKind.SendToAgent),
        new("/status", "Show gateway health and runtime status", SlashCommandKind.SendToAgent),
        new("/agents", "List registered agents and their models", SlashCommandKind.SendToAgent),
        new("/context", "Show context window usage for the current session", SlashCommandKind.SendToAgent),
        new("/model", "Show, set, or clear the per-conversation model override", SlashCommandKind.SendToAgent),
        new("/reasoning", "Show, set, or clear the per-conversation thinking override", SlashCommandKind.SendToAgent)
    ];

    /// <summary>
    /// Filters the registry for the command palette. When <paramref name="input"/> is exactly
    /// <c>"/"</c> the full list is returned; otherwise commands whose name starts with the typed
    /// text (case-insensitive) are returned. Any non-command input yields an empty result.
    /// </summary>
    public static IReadOnlyList<SlashCommand> Filter(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (!text.StartsWith('/') || text.Contains(' '))
            return [];

        if (text == "/")
            return All;

        return All
            .Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
