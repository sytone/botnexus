using BotNexus.Gateway.A2A;

namespace BotNexus.Extensions.A2A;

internal interface IA2AClient : IDisposable
{
    Task<A2ATaskResult> SendMessageAsync(
        A2AClientOptions options,
        A2AMessage message,
        DateTimeOffset deadline,
        CancellationToken cancellationToken = default);
}

internal interface IA2AClientFactory
{
    IA2AClient Create();
}
