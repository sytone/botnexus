namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;

/// <summary>
/// Maps a <see cref="SlashCommand"/> to the matching <see cref="IAgentInteractionService"/> call.
/// Lifted from the desktop <c>ChatPanel.ExecuteCommand</c> switch so desktop and mobile clients
/// dispatch commands identically (issue #1949, part of #1580).
/// </summary>
public interface ISlashCommandDispatcher
{
    /// <summary>
    /// Executes <paramref name="command"/> for the given <paramref name="agentId"/> and
    /// <paramref name="conversationId"/> by invoking the
    /// corresponding interaction-service method. Behaviour is a verbatim lift of the original desktop
    /// switch: <c>/new</c> resets the session, <c>/compact</c> compacts, <c>/clear</c> clears local
    /// messages, gateway-owned commands execute through the gateway command pipeline (#2873), and
    /// <see cref="SlashCommandKind.SendToAgent"/> commands are sent to the agent as message text.
    /// <para>
    /// #3063: <paramref name="conversationId"/> is required because the send path below no longer
    /// re-derives a conversation from ambient client state. #3211 extended that to every action
    /// path routed here (reset, compact, clear, gateway command), so the whole dispatch table is
    /// now conversation-explicit. Callers supply the conversation the palette was opened against.
    /// </para>
    /// <para>
    /// When <see cref="SlashCommand.RequiresApproval"/> is set the dispatcher first consults the
    /// injected <see cref="ISlashCommandApprovalHook"/> (issue #1950); if the hook denies the command
    /// it is not executed and the returned value is <see langword="false"/>. Unprotected commands
    /// bypass the hook and always execute.
    /// </para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the command was executed; <see langword="false"/> if a protected
    /// command was blocked by the approval hook.
    /// </returns>
    Task<bool> ExecuteAsync(string agentId, string conversationId, SlashCommand command);
}

/// <inheritdoc />
public sealed class SlashCommandDispatcher(
    IAgentInteractionService interaction,
    ISlashCommandApprovalHook? approvalHook = null) : ISlashCommandDispatcher
{
    private readonly IAgentInteractionService _interaction = interaction;
    private readonly ISlashCommandApprovalHook? _approvalHook = approvalHook;

    /// <inheritdoc />
    public async Task<bool> ExecuteAsync(string agentId, string conversationId, SlashCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        // Opt-in protection (issue #1950): only protected commands consult the hook. Fail closed
        // when a command is protected but no hook is registered so it never runs unapproved.
        if (command.RequiresApproval)
        {
            if (_approvalHook is null)
                return false;

            var approved = await _approvalHook.IsApprovedAsync(agentId, command).ConfigureAwait(false);
            if (!approved)
                return false;
        }

        await Dispatch(agentId, conversationId, command).ConfigureAwait(false);
        return true;
    }

    private Task Dispatch(string agentId, string conversationId, SlashCommand command) => command.Kind switch
    {
        SlashCommandKind.ResetSession => _interaction.ResetSessionAsync(agentId, conversationId),
        SlashCommandKind.CompactSession => _interaction.CompactSessionAsync(agentId, conversationId),
        SlashCommandKind.ClearLocalMessages => ClearLocal(agentId, conversationId),
        SlashCommandKind.SendToAgent => _interaction.SendMessageAsync(agentId, conversationId, command.Name),
        SlashCommandKind.GatewayCommand => _interaction.ExecuteGatewayCommandAsync(agentId, conversationId, command.Name),
        _ => Task.CompletedTask
    };

    private Task ClearLocal(string agentId, string conversationId)
    {
        _interaction.ClearLocalMessages(agentId, conversationId);
        return Task.CompletedTask;
    }
}
