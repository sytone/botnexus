using System.Diagnostics;
using System.Text;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

public sealed class BackgroundProcessUnresolvedTests
{
    [Fact]
    public async Task ExitedUnconfirmedKill_ReleasesResourcesAndPreservesUncertaintyAndCapturedOutput()
    {
        using var root = StartRoot();
        using var stdout = new HeldStream("captured-before-cleanup");
        using var stderr = new HeldStream("stderr-before-cleanup");
        var handle = root.SafeHandle;
        using var child = new UnconfirmedProcess(root, stdout, stderr, exitOnKill: true);
        await Task.WhenAll(stdout.Blocked.Task, stderr.Blocked.Task).WaitAsync(TimeSpan.FromSeconds(10));

        child.Kill().ShouldBeFalse();
        handle.IsClosed.ShouldBeTrue("exited uncertain roots must release the OS handle, not pin it forever");
        stdout.Closed.ShouldBeTrue();
        stderr.Closed.ShouldBeTrue();
        await child.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        child.IsTerminal.ShouldBeTrue();
        child.IsComplete.ShouldBeFalse("unresolved termination is not successful completion");
        child.KillUnconfirmed.ShouldBeTrue();
        child.ExitCode.ShouldNotBeNull();
        child.Kill().ShouldBeFalse("cleanup cannot convert uncertainty into a confirmed kill");
        child.Attempts.ShouldBe(1,"an exited unresolved root must never be re-killed or reattached");
        child.GetOutput().ShouldContain("captured-before-cleanup");
        child.GetOutput().ShouldContain("stderr-before-cleanup");
        child.GetOutput().ShouldContain("unresolved");
        child.GetOutput().ShouldContain("operator intervention");
        child.OutputCaptureIncomplete.ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(() => child.WriteInputAsync("x"));
        child.Dispose();
    }

    [Fact]
    public async Task Reap_BoundsUnresolvedDiagnosticsOldestFirstAndPreservesOwnership()
    {
        var registry = new BackgroundProcessRegistry(1);
        using var firstRoot = StartRoot();
        using var secondRoot = StartRoot();
        using var first = new UnconfirmedProcess(firstRoot, Stream.Null, Stream.Null, true, DateTimeOffset.UtcNow.AddMinutes(-1));
        using var second = new UnconfirmedProcess(secondRoot, Stream.Null, Stream.Null, true);
        registry.Register("owner", first);
        first.Kill().ShouldBeFalse();
        await first.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        registry.Get("other", first.Pid).ShouldBeNull();
        registry.Remove("other", first.Pid).ShouldBeFalse();
        registry.Register("owner", second);
        second.Kill().ShouldBeFalse();
        await second.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        registry.Reap();
        registry.Get("owner", first.Pid).ShouldBeNull("unresolved diagnostics count towards the completed-entry cap");
        registry.Get("owner", second.Pid).ShouldBeSameAs(second);
        second.KillUnconfirmed.ShouldBeTrue();
        registry.Clear("other");
        registry.Get("owner", second.Pid).ShouldBeSameAs(second);
        registry.Clear("owner");
        registry.List("owner").ShouldBeEmpty();
    }

    [Fact]
    public async Task RunningUnconfirmedKill_StaysPinnedUntilRootActuallyExits()
    {
        var registry = new BackgroundProcessRegistry(0);
        using var root = StartRoot();
        var handle = root.SafeHandle;
        using var child = new UnconfirmedProcess(root, Stream.Null, Stream.Null, false);
        registry.Register("owner", child);
        child.Kill().ShouldBeFalse();
        child.Dispose();
        registry.Reap();
        registry.Clear("owner");
        registry.Remove("owner", child.Pid).ShouldBeFalse();
        registry.Get("owner", child.Pid).ShouldBeSameAs(child);
        handle.IsClosed.ShouldBeFalse();
        child.IsRunning.ShouldBeTrue();
        root.StandardInput.Close();
        await child.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        handle.IsClosed.ShouldBeTrue("natural root exit after an unconfirmed kill must also release resources");
        child.KillUnconfirmed.ShouldBeTrue();
        registry.Reap();
        registry.List("owner").ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_ExitedUnresolvedEntryReleasesResourcesWithoutClaimingTermination()
    {
        var registry = new BackgroundProcessRegistry();
        using var root = StartRoot();
        using var child = new UnconfirmedProcess(root, Stream.Null, Stream.Null, true);
        registry.Register("owner", child);
        child.Kill().ShouldBeFalse();
        await child.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        registry.Remove("owner", child.Pid).ShouldBeTrue();
        child.KillUnconfirmed.ShouldBeTrue();
        child.IsComplete.ShouldBeFalse();
        registry.Get("owner", child.Pid).ShouldBeNull();
    }

    [Fact]
    public async Task ExitedUnconfirmedKill_WithPendingStdinWrite_ReleasesHandleAndCompletesWithoutClearingUncertainty()
    {
        using var root = StartRoot();
        using var stdin = new HeldWriteStream();
        using var writer = new StreamWriter(stdin, new UTF8Encoding(false), bufferSize: 128);
        using var stdout = new HeldStream("stdout-before-pending-write");
        using var stderr = new HeldStream("stderr-before-pending-write");
        var handle = root.SafeHandle;
        using var child = new UnconfirmedProcess(root, stdout, stderr, true, standardInput: writer);
        var write = child.WriteInputAsync(new string('x', 8192));
        try
        {
            await Task.WhenAll(stdin.Blocked.Task, stdout.Blocked.Task, stderr.Blocked.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            write.IsCompleted.ShouldBeFalse("the real StreamWriter must still have an outstanding async write");

            child.Kill().ShouldBeFalse();
            stdin.Closed.ShouldBeTrue("cleanup closes the underlying stdin even while its writer is busy");
            handle.IsClosed.ShouldBeTrue("a pending stdin write must not prevent OS handle release");
            write.IsCompleted.ShouldBeFalse("cleanup must not depend on the pending write settling");
            await child.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            child.IsTerminal.ShouldBeTrue();
            child.IsComplete.ShouldBeFalse();
            child.IsRunning.ShouldBeFalse();
            child.KillUnconfirmed.ShouldBeTrue();
            child.IsUnresolved.ShouldBeTrue();
            child.ExitCode.ShouldNotBeNull();
            child.OutputCaptureIncomplete.ShouldBeTrue();
            stdout.Closed.ShouldBeTrue();
            stderr.Closed.ShouldBeTrue();
            child.GetOutput().ShouldContain("stdout-before-pending-write");
            child.GetOutput().ShouldContain("stderr-before-pending-write");
            child.GetOutput().ShouldContain("operator intervention");
            child.Kill().ShouldBeFalse();
            child.Attempts.ShouldBe(1);
            child.Dispose();
            handle.IsClosed.ShouldBeTrue();
            await Should.ThrowAsync<ObjectDisposedException>(() => child.WriteInputAsync("later"));
        }
        finally
        {
            // Always settle the test-owned writer, including when the catch-removal mutation fails.
            stdin.Release.TrySetResult();
            try { await write.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    private static Process StartRoot()
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "set /p value=" : "read value");
        return Process.Start(info) ?? throw new InvalidOperationException("root did not start");
    }

    private sealed class UnconfirmedProcess : BackgroundProcess
    {
        private readonly Process _root;
        private readonly bool _exitOnKill;
        public int Attempts { get; private set; }
        public UnconfirmedProcess(Process root, Stream stdout, Stream stderr, bool exitOnKill, DateTimeOffset? started = null, StreamWriter? standardInput = null)
            : base(root, "uncertain", started ?? DateTimeOffset.UtcNow, new StreamReader(stdout), new StreamReader(stderr), standardInput)
        {
            _root = root;
            _exitOnKill = exitOnKill;
        }
        protected override bool KillCore()
        {
            Attempts++;
            if (_exitOnKill)
            {
                _root.Kill(entireProcessTree: true);
                _root.WaitForExit(5_000).ShouldBeTrue();
            }
            return false;
        }
    }

    private sealed class HeldWriteStream : Stream
    {
        public bool Closed { get; private set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Blocked.TrySetResult();
            // Deliberately ignore disposal: closing a stream need not synchronously settle its write.
            await Release.Task;
            ObjectDisposedException.ThrowIf(Closed, this);
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !Closed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { ObjectDisposedException.ThrowIf(Closed, this); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HeldStream(string content) : Stream
    {
        private bool _sent;
        public bool Closed { get; private set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                return Encoding.UTF8.GetBytes(content.AsSpan(), buffer.Span);
            }
            Blocked.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
        public override bool CanRead => !Closed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
