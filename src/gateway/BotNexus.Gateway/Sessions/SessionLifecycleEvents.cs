using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

public sealed class SessionLifecycleEvents(ILogger<SessionLifecycleEvents> logger) : ISessionLifecycleEvents
{
    private readonly ILogger<SessionLifecycleEvents> _logger = logger;

    public event Func<SessionLifecycleEvent, CancellationToken, Task>? SessionChanged;

    public async Task PublishAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
    {
        var handlers = SessionChanged;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Func<SessionLifecycleEvent, CancellationToken, Task>>())
        {
            try
            {
                await handler(lifecycleEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session lifecycle subscriber failed for session '{SessionId}'.", lifecycleEvent.SessionId);
            }
        }
    }
}
