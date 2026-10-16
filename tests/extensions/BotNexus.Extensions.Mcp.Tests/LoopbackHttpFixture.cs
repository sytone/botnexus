using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BotNexus.Extensions.Mcp.Tests;

/// <summary>
/// A single-request HTTP endpoint that keeps its port-zero socket from bind through
/// shutdown. Disposal cancels pending accepts/reads and observes the serving task;
/// request counters are stable only after disposal completes.
/// </summary>
internal sealed class LoopbackHttpFixture : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _headerReadPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _stopTask;

    internal IPEndPoint Endpoint { get; }
    internal Uri Address { get; }
    internal Task Accepted => _accepted.Task;
    internal Task HeaderReadPending => _headerReadPending.Task;
    internal bool IsBound => _listener.Server.IsBound;
    internal Task Completion { get; }
    internal int RequestCount { get; private set; }
    internal string? Authorization { get; private set; }

    // The start seam permits a deterministic failure after socket acquisition.
    // Production tests use the default: one exclusive bind, never a free-port probe.
    internal LoopbackHttpFixture(Uri? redirect = null, Action<TcpListener>? start = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        try
        {
            if (start is null)
                _listener.Start();
            else
                start(_listener);
            Endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Address = new Uri($"http://127.0.0.1:{Endpoint.Port}/mcp");
            Completion = ServeAsync(redirect, _shutdown.Token);
        }
        catch
        {
            _listener.Stop();
            _shutdown.Dispose();
            throw;
        }
    }

    private async Task ServeAsync(Uri? redirect, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await _listener.AcceptTcpClientAsync(cancellationToken);
            _accepted.TrySetResult();
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(requestLine))
                throw new EndOfStreamException("HTTP client closed before sending a request line.");

            while (true)
            {
                var read = reader.ReadLineAsync(cancellationToken);
                if (!read.IsCompleted)
                    _headerReadPending.TrySetResult();
                var line = await read
                    ?? throw new EndOfStreamException("HTTP client closed before completing its headers.");
                if (line.Length == 0)
                    break;
                var separator = line.IndexOf(':');
                if (separator > 0 && line[..separator].Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    Authorization = line[(separator + 1)..].Trim();
            }

            RequestCount++;
            var response = redirect is null
                ? "HTTP/1.1 200 OK\r\n"
                : $"HTTP/1.1 302 Found\r\nLocation: {redirect.AbsoluteUri}\r\n";
            response += "Content-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only our requested shutdown is normal; I/O, bind and protocol failures surface.
        }
    }

    public ValueTask DisposeAsync() => new(_stopTask ??= StopAsync());

    private async Task StopAsync()
    {
        try
        {
            await _shutdown.CancelAsync();
            await Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _listener.Stop();
            _shutdown.Dispose();
        }
    }
}
