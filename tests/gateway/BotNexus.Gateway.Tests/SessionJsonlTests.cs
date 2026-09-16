using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests;

public sealed class SessionJsonlTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TestEntry[] Delta = [new("first"), new("second")];

    [Fact]
    public async Task AppendToStreamAsync_WhenWriteFailsAfterCompleteLine_RollsBackBeforeRetry()
    {
        await AssertFailedAppendRollsBackAsync(bytesIntoSecondLine: 0, failOnFlush: false);
    }

    [Fact]
    public async Task AppendToStreamAsync_WhenWriteFailsWithinFinalLine_RollsBackBeforeRetry()
    {
        await AssertFailedAppendRollsBackAsync(bytesIntoSecondLine: 3, failOnFlush: false);
    }

    [Fact]
    public async Task AppendToStreamAsync_WhenFlushFails_RollsBackBeforeRetry()
    {
        await AssertFailedAppendRollsBackAsync(bytesIntoSecondLine: null, failOnFlush: true);
    }

    private static async Task AssertFailedAppendRollsBackAsync(int? bytesIntoSecondLine, bool failOnFlush)
    {
        var baseline = Encoding.UTF8.GetBytes("{\"value\":\"existing\"}\n");
        await using var storage = new MemoryStream();
        await storage.WriteAsync(baseline);

        var firstLineLength = JsonSerializer.SerializeToUtf8Bytes(Delta[0], JsonOptions).Length + 1;
        int? failAfterBytes = bytesIntoSecondLine is null ? null : firstLineLength + bytesIntoSecondLine.Value;
        await using var faulting = new FaultingStream(storage, failAfterBytes, failOnFlush);

        await Should.ThrowAsync<IOException>(() =>
            SessionJsonl.AppendToStreamAsync(faulting, Delta, JsonOptions));

        storage.ToArray().ShouldBe(baseline, "a failed append must restore the exact pre-append bytes");

        await SessionJsonl.AppendToStreamAsync(storage, Delta, JsonOptions);
        var values = Encoding.UTF8.GetString(storage.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<TestEntry>(line, JsonOptions)?.Value)
            .ToArray();

        values.ShouldBe(["existing", "first", "second"]);
    }

    private sealed record TestEntry(string Value);

    private sealed class FaultingStream(Stream inner, int? failAfterBytes, bool failOnFlush) : Stream
    {
        private int _written;
        private bool _writeFailed;
        private bool _flushFailed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_writeFailed && failAfterBytes is int limit && _written + buffer.Length > limit)
            {
                var permitted = Math.Max(0, limit - _written);
                if (permitted > 0)
                {
                    inner.Write(buffer.Span[..permitted]);
                    _written += permitted;
                }

                _writeFailed = true;
                throw new IOException("Injected partial write failure.");
            }

            _written += buffer.Length;
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (failOnFlush && !_flushFailed)
            {
                _flushFailed = true;
                throw new IOException("Injected flush failure.");
            }

            return inner.FlushAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // The test owns the underlying storage independently.
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
