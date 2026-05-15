namespace BotNexus.TeamsProxy.Services;

public sealed class BotAuthenticationException : Exception
{
    public BotAuthenticationException(string message)
        : base(message)
    {
    }

    public BotAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
