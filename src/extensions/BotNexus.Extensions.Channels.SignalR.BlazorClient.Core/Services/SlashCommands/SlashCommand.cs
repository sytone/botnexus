namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;

/// <summary>
/// Identifies how a <see cref="SlashCommand"/> is executed against the
/// <see cref="IAgentInteractionService"/>. The dispatcher switches on this so the
/// registry stays a pure data description with no behaviour of its own.
/// </summary>
public enum SlashCommandKind
{
    /// <summary>Reset the current session (client-driven new session). Maps to <see cref="IAgentInteractionService.ResetSessionAsync"/>.</summary>
    ResetSession,

    /// <summary>Compact the current session context. Maps to <see cref="IAgentInteractionService.CompactSessionAsync"/>.</summary>
    CompactSession,

    /// <summary>Clear locally rendered messages only. Maps to <see cref="IAgentInteractionService.ClearLocalMessages"/>.</summary>
    ClearLocalMessages,

    /// <summary>
    /// Send the command text to the agent as a message so the gateway command pipeline
    /// handles it. Maps to <see cref="IAgentInteractionService.SendMessageAsync"/>.
    /// </summary>
    SendToAgent
}

/// <summary>
/// A single slash command available in the chat command palette. This is the shared model
/// lifted out of the desktop <c>ChatPanel.razor</c> so desktop and mobile clients consume the
/// same registry and dispatcher (issue #1949, part of #1580).
/// </summary>
/// <param name="Name">The command token including the leading slash, e.g. <c>/compact</c>.</param>
/// <param name="Description">Human-readable one-line description shown in the palette.</param>
/// <param name="Kind">How the dispatcher executes this command.</param>
/// <param name="RequiresApproval">
/// Opt-in protection flag (issue #1950, part of #1580). When <see langword="true"/> the dispatcher
/// consults the injected <see cref="ISlashCommandApprovalHook"/> before executing the command; a
/// denial prevents execution. Unprotected commands (the default) bypass the hook entirely so the
/// hot path for ordinary commands is unchanged. Set per command at registration time by the user
/// or extension owner.
/// </param>
public sealed record SlashCommand(string Name, string Description, SlashCommandKind Kind, bool RequiresApproval = false);
