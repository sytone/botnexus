namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;

/// <summary>
/// Opt-in approval/protection hook consulted by the <see cref="ISlashCommandDispatcher"/> before
/// executing a command whose <see cref="SlashCommand.RequiresApproval"/> flag is set (issue #1950,
/// part of #1580). Unprotected commands never reach this hook. Implementations decide, per attempt,
/// whether a protected command may run — for example by prompting the user for confirmation, checking
/// an allow-list, or enforcing extension-owner policy.
/// </summary>
/// <remarks>
/// The hook is registered as an injectable service. When no implementation is supplied the dispatcher
/// falls back to denying protected commands (fail-closed), so a command flagged as protected is never
/// executed without an explicit approval decision.
/// </remarks>
public interface ISlashCommandApprovalHook
{
    /// <summary>
    /// Returns <see langword="true"/> to allow <paramref name="command"/> to execute for
    /// <paramref name="agentId"/>, or <see langword="false"/> to deny (block) execution.
    /// </summary>
    Task<bool> IsApprovedAsync(string agentId, SlashCommand command);
}
