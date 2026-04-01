using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Hooks;

/// <summary>
/// A hook that logs agent processing events for observability.
/// </summary>
public sealed class LoggingHook : IAgentHook
{
    private readonly ILogger<LoggingHook> _logger;

    public LoggingHook(ILogger<LoggingHook> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task OnBeforeAsync(AgentHookContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Agent {AgentName} processing message for session {SessionKey}",
            context.AgentName, context.SessionKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task OnAfterAsync(AgentHookContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Agent {AgentName} completed processing for session {SessionKey}",
            context.AgentName, context.SessionKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task OnErrorAsync(AgentHookContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogError(context.Error,
            "Agent {AgentName} error for session {SessionKey}: {Message}",
            context.AgentName, context.SessionKey, context.Error?.Message);
        return Task.CompletedTask;
    }
}
