using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Command;

/// <summary>
/// Routes commands (e.g., /help, /reset) with priority-based handler registration.
/// Commands are matched exactly (e.g., "/help") or by prefix (e.g., "/set ").
/// </summary>
public sealed class CommandRouter : ICommandRouter
{
    private sealed record CommandEntry(
        string Command,
        Func<InboundMessage, CancellationToken, Task<string?>> Handler,
        int Priority,
        bool IsPrefix);

    private readonly List<CommandEntry> _handlers = [];
    private readonly ILogger<CommandRouter> _logger;
    private readonly IChannel? _replyChannel;

    public CommandRouter(ILogger<CommandRouter> logger, IChannel? replyChannel = null)
    {
        _logger = logger;
        _replyChannel = replyChannel;
    }

    /// <inheritdoc/>
    public void Register(string command, Func<InboundMessage, CancellationToken, Task<string?>> handler, int priority = 0)
    {
        var isPrefix = command.EndsWith('*');
        var normalizedCommand = isPrefix ? command[..^1] : command;
        _handlers.Add(new CommandEntry(normalizedCommand.ToLowerInvariant(), handler, priority, isPrefix));
        _handlers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    /// <inheritdoc/>
    public async Task<bool> TryHandleAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        var content = message.Content.Trim();
        if (!content.StartsWith('/'))
            return false;

        var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var commandText = parts[0].ToLowerInvariant();

        foreach (var entry in _handlers)
        {
            bool matched = entry.IsPrefix
                ? commandText.StartsWith(entry.Command)
                : commandText == entry.Command;

            if (!matched) continue;

            try
            {
                var response = await entry.Handler(message, cancellationToken).ConfigureAwait(false);
                if (response is not null && _replyChannel is not null)
                {
                    await _replyChannel.SendAsync(
                        new OutboundMessage(message.Channel, message.ChatId, response),
                        cancellationToken).ConfigureAwait(false);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling command {Command}", commandText);
                return true;
            }
        }

        return false;
    }
}
