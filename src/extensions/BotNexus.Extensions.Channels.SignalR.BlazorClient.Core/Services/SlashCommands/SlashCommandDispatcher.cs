namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;

/// <summary>
/// Maps a <see cref="SlashCommand"/> to the matching <see cref="IAgentInteractionService"/> call.
/// Lifted from the desktop <c>ChatPanel.ExecuteCommand</c> switch so desktop and mobile clients
/// dispatch commands identically (issue #1949, part of #1580).
/// </summary>
public interface ISlashCommandDispatcher
{
    /// <summary>
    /// Executes <paramref name="command"/> for the given <paramref name="agentId"/> by invoking the
    /// corresponding interaction-service method. Behaviour is a verbatim lift of the original desktop
    /// switch: <c>/new</c> resets the session, <c>/compact</c> compacts, <c>/clear</c> clears local
    /// messages, and every other command is sent to the agent as its command text so the gateway
    /// command pipeline handles it.
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
    Task<bool> ExecuteAsync(string agentId, SlashCommand command);
}

/// <inheritdoc />
public sealed class SlashCommandDispatcher(
    IAgentInteractionService interaction,
    ISlashCommandApprovalHook? approvalHook = null) : ISlashCommandDispatcher
{
    private readonly IAgentInteractionService _interaction = interaction;
    private readonly ISlashCommandApprovalHook? _approvalHook = approvalHook;

    /// <inheritdoc />
    public async Task<bool> ExecuteAsync(string agentId, SlashCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

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

        await Dispatch(agentId, command).ConfigureAwait(false);
        return true;
    }

    private Task Dispatch(string agentId, SlashCommand command) => command.Kind switch
    {
        SlashCommandKind.ResetSession => _interaction.ResetSessionAsync(agentId),
        SlashCommandKind.CompactSession => _interaction.CompactSessionAsync(agentId),
        SlashCommandKind.ClearLocalMessages => ClearLocal(agentId),
        SlashCommandKind.SendToAgent => _interaction.SendMessageAsync(agentId, command.Name),
        _ => Task.CompletedTask
    };

    private Task ClearLocal(string agentId)
    {
        _interaction.ClearLocalMessages(agentId);
        return Task.CompletedTask;
    }
}
