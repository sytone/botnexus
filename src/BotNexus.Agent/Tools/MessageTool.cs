using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Tools;

/// <summary>Tool for sending messages back to a channel from within an agent.</summary>
public sealed class MessageTool : ToolBase
{
    private readonly IChannel? _channel;

    public MessageTool(IChannel? channel = null, ILogger? logger = null)
        : base(logger)
    {
        _channel = channel;
    }

    /// <inheritdoc/>
    public override ToolDefinition Definition => new(
        "send_message",
        "Send a message to a specific chat or channel.",
        new Dictionary<string, ToolParameterSchema>
        {
            ["chat_id"] = new("string", "The chat ID to send the message to", Required: true),
            ["content"] = new("string", "The message content to send", Required: true),
            ["channel"] = new("string", "Optional channel name override", Required: false)
        });

    /// <inheritdoc/>
    protected override async Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        if (_channel is null)
            return "Error: No channel available";

        var chatId = GetRequiredString(arguments, "chat_id");
        var content = GetRequiredString(arguments, "content");
        var channelName = GetOptionalString(arguments, "channel", _channel.Name);

        var message = new OutboundMessage(channelName, chatId, content);
        await _channel.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return "Message sent successfully";
    }
}
