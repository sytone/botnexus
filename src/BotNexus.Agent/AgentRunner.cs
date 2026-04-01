using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent;

/// <summary>
/// Coordinates channel → agent → channel flow.
/// Reads messages from the bus, routes them to the appropriate agent, and sends responses.
/// </summary>
public sealed class AgentRunner : IAgentRunner
{
    private readonly AgentLoop _agentLoop;
    public string AgentName { get; }
    private readonly IChannel? _responseChannel;
    private readonly ICommandRouter? _commandRouter;
    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(
        string agentName,
        AgentLoop agentLoop,
        ILogger<AgentRunner> logger,
        IChannel? responseChannel = null,
        ICommandRouter? commandRouter = null)
    {
        AgentName = agentName;
        _agentLoop = agentLoop;
        _logger = logger;
        _responseChannel = responseChannel;
        _commandRouter = commandRouter;
    }

    /// <inheritdoc/>
    public async Task RunAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        // Try command routing first
        if (_commandRouter is not null)
        {
            var handled = await _commandRouter.TryHandleAsync(message, cancellationToken).ConfigureAwait(false);
            if (handled) return;
        }

        _logger.LogInformation("Processing message for session {SessionKey}", message.SessionKey);

        try
        {
            var response = await _agentLoop.ProcessAsync(message, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response) && _responseChannel is not null)
            {
                await _responseChannel.SendAsync(
                    new OutboundMessage(message.Channel, message.ChatId, response),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process message for session {SessionKey}", message.SessionKey);

            if (_responseChannel is not null)
            {
                await _responseChannel.SendAsync(
                    new OutboundMessage(message.Channel, message.ChatId,
                        "Sorry, I encountered an error processing your request."),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
