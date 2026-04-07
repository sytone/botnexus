namespace BotNexus.Gateway.Abstractions.Sessions;

public interface ISessionLifecycleEvents
{
    event Func<SessionLifecycleEvent, CancellationToken, Task>? SessionChanged;
}
