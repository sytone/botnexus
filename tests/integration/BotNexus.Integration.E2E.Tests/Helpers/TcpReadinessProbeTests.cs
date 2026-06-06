using System.Net;
using System.Net.Sockets;

namespace BotNexus.Integration.E2E.Tests;

public sealed class TcpReadinessProbeTests
{
    [Fact]
    public async Task WaitForTcpReadyAsync_ReturnsTrue_WhenPortIsListening()
    {
        // Arrange — start a listener on a free port
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            // Act
            var result = await TcpReadinessProbe.WaitForTcpReadyAsync(
                "127.0.0.1", port, TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(result);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task WaitForTcpReadyAsync_ReturnsFalse_WhenPortNeverOpens()
    {
        // Pick a port that is guaranteed not to be listening (bind + immediately release).
        var port = GetUnusedPort();

        // Act — short timeout to avoid slow test
        var result = await TcpReadinessProbe.WaitForTcpReadyAsync(
            "127.0.0.1", port, TimeSpan.FromMilliseconds(300), initialDelayMs: 30, maxDelayMs: 100);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WaitForTcpReadyAsync_ReturnsTrue_WhenPortOpensAfterDelay()
    {
        // Arrange — start listener after a delay
        var port = GetUnusedPort();
        var listenerStarted = new TaskCompletionSource();

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listenerStarted.SetResult();
            // Keep listening for the duration of the test
            await Task.Delay(5000);
            listener.Stop();
        });

        // Act
        var result = await TcpReadinessProbe.WaitForTcpReadyAsync(
            "127.0.0.1", port, TimeSpan.FromSeconds(5), initialDelayMs: 50);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForTcpReadyAsync_ThrowsOnCancellation()
    {
        var port = GetUnusedPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            TcpReadinessProbe.WaitForTcpReadyAsync(
                "127.0.0.1", port, TimeSpan.FromSeconds(30),
                initialDelayMs: 20, cancellationToken: cts.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task WaitForTcpReadyAsync_ThrowsOnInvalidPort(int port)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            TcpReadinessProbe.WaitForTcpReadyAsync("127.0.0.1", port, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WaitForTcpReadyAsync_ThrowsArgumentException_OnEmptyOrWhitespaceHost(string host)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            TcpReadinessProbe.WaitForTcpReadyAsync(host, 8080, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task WaitForTcpReadyAsync_ThrowsArgumentNullException_OnNullHost()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            TcpReadinessProbe.WaitForTcpReadyAsync(null!, 8080, TimeSpan.FromSeconds(1)));
    }

    private static int GetUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
