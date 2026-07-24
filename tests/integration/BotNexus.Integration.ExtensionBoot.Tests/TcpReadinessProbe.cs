using System.Net;
using System.Net.Sockets;

namespace BotNexus.Integration.ExtensionBoot.Tests;

/// <summary>
/// TCP-level readiness probe. Waits for a host:port to accept TCP connections before
/// the HTTP-level health check fires, preventing spurious connection-refused errors on
/// slow CI runners where Kestrel has not bound the port yet.
/// </summary>
internal static class TcpReadinessProbe
{
    public static async Task<bool> WaitForTcpReadyAsync(
        string host,
        int port,
        TimeSpan timeout,
        int initialDelayMs = 50,
        int maxDelayMs = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");

        var deadline = DateTime.UtcNow + timeout;
        var delayMs = initialDelayMs;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port), cancellationToken);
                return true;
            }
            catch (SocketException)
            {
                // Port not yet listening - back off and retry.
            }

            await Task.Delay(Math.Min(delayMs, maxDelayMs), cancellationToken);
            delayMs = Math.Min(delayMs * 2, maxDelayMs);
        }

        return false;
    }
}
