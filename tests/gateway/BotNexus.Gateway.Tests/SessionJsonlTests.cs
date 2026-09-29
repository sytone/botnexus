using System.IO.Abstractions.TestingHelpers;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests;

public sealed class SessionJsonlTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TestEntry[] Delta = [new("first"), new("second")];

    [Fact]
    public async Task AppendAsync_PreCancelled_DoesNotCreateDirectoryOrFile()
    {
        var fileSystem = new MockFileSystem();
        var path = Path.Combine("store", "session.jsonl");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            SessionJsonl.AppendAsync(fileSystem, path, Delta, JsonOptions, cancellation.Token));

        fileSystem.Directory.Exists("store").ShouldBeFalse();
        fileSystem.File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteAllAsync_PreCancelled_PreservesExistingBytes()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["session.jsonl"] = new("existing\n")
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            SessionJsonl.WriteAllAsync(fileSystem, "session.jsonl", Delta, JsonOptions, cancellation.Token));

        fileSystem.File.ReadAllText("session.jsonl").ShouldBe("existing\n");
    }

    [Fact]
    public async Task AppendToStreamAsync_WhenCancelledDuringDelta_RollsBackBeforeRetry()
    {
        var baseline = Encoding.UTF8.GetBytes("{\"value\":\"existing\"}\n");
        await using var storage = new MemoryStream();
        await storage.WriteAsync(baseline);
        using var cancellation = new CancellationTokenSource();
        await using var controlled = new CancelAfterWriteStream(storage, cancellation, writesBeforeCancellation: 1);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            SessionJsonl.AppendToStreamAsync(controlled, Delta, JsonOptions, cancellation.Token));

        storage.ToArray().ShouldBe(baseline);
        await SessionJsonl.AppendToStreamAsync(storage, Delta, JsonOptions);
        DeserializeValues(storage).ShouldBe(["existing", "first", "second"]);
    }

    [Fact]
    public async Task WriteAllToStreamAsync_WhenCancelledDuringReplacement_DoesNotComplete()
    {
        await using var storage = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        await using var controlled = new CancelAfterWriteStream(storage, cancellation, writesBeforeCancellation: 1);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            SessionJsonl.WriteAllToStreamAsync(controlled, Delta, JsonOptions, cancellation.Token));

        DeserializeValues(storage).ShouldBe(["first"]);
    }

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
        DeserializeValues(storage).ShouldBe(["existing", "first", "second"]);
    }

    private static string?[] DeserializeValues(MemoryStream storage) =>
        Encoding.UTF8.GetString(storage.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<TestEntry>(line, JsonOptions)?.Value)
            .ToArray();

    private sealed record TestEntry(string Value);

    private sealed class CancelAfterWriteStream(
        Stream inner,
        CancellationTokenSource cancellation,
        int writesBeforeCancellation) : Stream
    {
        private int _writes;

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

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await inner.WriteAsync(buffer, cancellationToken);
            if (Interlocked.Increment(ref _writes) == writesBeforeCancellation)
                cancellation.Cancel();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            // The test owns the underlying storage independently.
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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
