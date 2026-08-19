using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Mcp.Tests.Transport;

/// <summary>
/// Pins the bound on <see cref="HttpSseMcpTransport"/>'s inbound response buffer (#3400).
/// <para>
/// The SSE pump writes every frame the server emits; the correlator reads exactly one frame per
/// outbound request. Before this bound, every uncorrelated frame - a notification, a duplicate,
/// a late reply to a timed-out call - was retained for the life of the transport, making a chatty
/// or hostile MCP endpoint a remote-input-driven heap-growth path.
/// </para>
/// </summary>
public sealed class HttpSseResponseChannelBoundTests
{
    private static string BuildSseStream(int firstId, int count)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var response = new JsonRpcResponse
            {
                Id = firstId + i,
                Result = JsonSerializer.SerializeToElement(new { seq = firstId + i }),
            };
            var json = JsonSerializer.Serialize(response, JsonContext.Default.JsonRpcResponse);
            sb.Append("event: message\n").Append("data: ").Append(json).Append("\n\n");
        }

        return sb.ToString();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ResponseBuffer_WithNoReader_NeverExceedsCapacity()
    {
        var capacity = HttpSseMcpTransport.ResponseChannelCapacity;
        var logger = new CapturingLogger();
        await using var transport = new HttpSseMcpTransport(
            new Uri("http://localhost/mcp"), logger: logger);

        // Deliberately no reader: every frame lands in the buffer and stays there.
        var overflow = capacity + 64;
        var delivered = await transport.ParseSseStreamAsync(
            new StringReader(BuildSseStream(1, overflow)), CancellationToken.None);

        delivered.ShouldBe(overflow, "every well-formed frame must still be accepted by the writer");
        transport.BufferedResponseCount.ShouldBeLessThanOrEqualTo(capacity);
        transport.BufferedResponseCount.ShouldBe(capacity);
        transport.DroppedResponses.ShouldBe(overflow - capacity);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ResponseBuffer_Saturation_DropsOldestAndWarnsNamingTheEndpoint()
    {
        var capacity = HttpSseMcpTransport.ResponseChannelCapacity;
        var logger = new CapturingLogger();
        var endpoint = new Uri("http://mcp.example.test/mcp");
        await using var transport = new HttpSseMcpTransport(endpoint, logger: logger);

        var overflow = capacity + 10;
        await transport.ParseSseStreamAsync(
            new StringReader(BuildSseStream(1, overflow)), CancellationToken.None);

        // DropOldest: ids 1..10 were evicted, so the buffer now starts at 11.
        var first = await transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        first.Id!.ToString().ShouldBe("11");

        // The drop is observable, not silent.
        transport.DroppedResponses.ShouldBe(10);
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Count.ShouldBe(10);
        warnings.ShouldAllBe(e => e.Message.Contains("mcp.example.test"));
        warnings[0].Message.ShouldContain("saturated");
    }

    [Fact]
    public async Task ResponseBuffer_BelowCapacity_PreservesCorrelationOrderAndPayloads()
    {
        var logger = new CapturingLogger();
        await using var transport = new HttpSseMcpTransport(
            new Uri("http://localhost/mcp"), logger: logger);

        const int count = 5;
        var delivered = await transport.ParseSseStreamAsync(
            new StringReader(BuildSseStream(1, count)), CancellationToken.None);
        delivered.ShouldBe(count);

        for (var expected = 1; expected <= count; expected++)
        {
            var read = await transport.ReceiveAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            read.Id!.ToString().ShouldBe(expected.ToString());
            read.Result!.Value.GetProperty("seq").GetInt32().ShouldBe(expected);
        }

        transport.DroppedResponses.ShouldBe(0);
        transport.BufferedResponseCount.ShouldBe(0);
        logger.Entries.ShouldBeEmpty();
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) { return [.. _entries]; } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
