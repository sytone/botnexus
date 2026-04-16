namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Implemented by extensions that contribute user-facing slash commands.
/// </summary>
public interface ICommandContributor
{
    /// <summary>
    /// Gets the commands contributed by this extension.
    /// </summary>
    /// <returns>Command descriptors exposed by this contributor.</returns>
    IReadOnlyList<CommandDescriptor> GetCommands();

    /// <summary>
    /// Executes a contributed command.
    /// </summary>
    /// <param name="commandName">Resolved command name.</param>
    /// <param name="context">Execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Command execution result.</returns>
    Task<CommandResult> ExecuteAsync(
        string commandName,
        CommandExecutionContext context,
        CancellationToken cancellationToken = default);
}
