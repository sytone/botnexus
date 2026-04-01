using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Contract for routing commands (e.g., /help, /reset).</summary>
public interface ICommandRouter
{
    /// <summary>Tries to handle a message as a command. Returns true if handled.</summary>
    Task<bool> TryHandleAsync(InboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>Registers a command handler.</summary>
    void Register(string command, Func<InboundMessage, CancellationToken, Task<string?>> handler, int priority = 0);
}
