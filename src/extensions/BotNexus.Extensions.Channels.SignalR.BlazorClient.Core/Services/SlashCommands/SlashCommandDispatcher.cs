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
    /// </summary>
    Task ExecuteAsync(string agentId, SlashCommand command);
}

/// <inheritdoc />
public sealed class SlashCommandDispatcher(IAgentInteractionService interaction) : ISlashCommandDispatcher
{
    private readonly IAgentInteractionService _interaction = interaction;

    /// <inheritdoc />
    public Task ExecuteAsync(string agentId, SlashCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Kind switch
        {
            SlashCommandKind.ResetSession => _interaction.ResetSessionAsync(agentId),
            SlashCommandKind.CompactSession => _interaction.CompactSessionAsync(agentId),
            SlashCommandKind.ClearLocalMessages => ClearLocal(agentId),
            SlashCommandKind.SendToAgent => _interaction.SendMessageAsync(agentId, command.Name),
            _ => Task.CompletedTask
        };
    }

    private Task ClearLocal(string agentId)
    {
        _interaction.ClearLocalMessages(agentId);
        return Task.CompletedTask;
    }
}
