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
        var correlationId = message.GetCorrelationId() ?? "n/a";
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["SessionKey"] = message.SessionKey,
            ["AgentName"] = AgentName
        });

        // Try command routing first
        if (_commandRouter is not null)
        {
            var handled = await _commandRouter.TryHandleAsync(message, cancellationToken).ConfigureAwait(false);
            if (handled) return;
        }

        _logger.LogInformation("Processing message for session {SessionKey}", message.SessionKey);

        try
        {
            // Create streaming callback if channel supports it
            Func<string, Task>? onDelta = null;
            if (_responseChannel is not null && _responseChannel.SupportsStreaming)
            {
                onDelta = async (delta) =>
                {
                    await _responseChannel.SendDeltaAsync(message.ChatId, delta, cancellationToken: cancellationToken).ConfigureAwait(false);
                };
            }

            var response = await _agentLoop.ProcessAsync(message, onDelta, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response) && _responseChannel is not null)
            {
                _logger.LogInformation("Sending response to channel {Channel}", message.Channel);
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
