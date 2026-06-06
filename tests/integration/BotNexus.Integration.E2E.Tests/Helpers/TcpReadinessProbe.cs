using System.Net;
using System.Net.Sockets;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// TCP-level readiness probe for integration tests.
/// Waits for a host:port to accept TCP connections before the HTTP-level health check fires.
/// Prevents spurious test failures on slow CI runners where Kestrel hasn't bound yet.
/// </summary>
internal static class TcpReadinessProbe
{
    /// <summary>
    /// Polls the specified endpoint until a TCP handshake succeeds or the timeout is reached.
    /// Uses exponential backoff starting at <paramref name="initialDelayMs"/> and capping at
    /// <paramref name="maxDelayMs"/>.
    /// </summary>
    /// <param name="host">Target host (e.g. "127.0.0.1").</param>
    /// <param name="port">Target port.</param>
    /// <param name="timeout">Maximum time to wait for the port to become available.</param>
    /// <param name="initialDelayMs">Initial retry delay in ms (default 50).</param>
    /// <param name="maxDelayMs">Maximum retry delay cap in ms (default 1000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the TCP handshake succeeded before timeout; <c>false</c> otherwise.</returns>
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
                // Port not yet listening — back off and retry.
            }

            await Task.Delay(Math.Min(delayMs, maxDelayMs), cancellationToken);
            delayMs = Math.Min(delayMs * 2, maxDelayMs);
        }

        return false;
    }
}
