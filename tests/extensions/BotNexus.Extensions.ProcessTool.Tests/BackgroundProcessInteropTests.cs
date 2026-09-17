using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.ExecTool;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.ProcessTool.Tests;

public sealed class BackgroundProcessInteropTests : IDisposable
{
    private readonly string _owner = "interop-" + Guid.NewGuid().ToString("N");
    public void Dispose() => BackgroundProcessRegistry.Instance.Clear(_owner);

    private AgentToolContributionContext Context(params string[] tools) => new(
        new AgentDescriptor { AgentId = AgentId.From(_owner), DisplayName = _owner, ModelId = "test", ApiProvider = "test", ToolIds = tools },
        new AgentExecutionContext { SessionId = SessionId.Create() }, Path.GetTempPath(), new AllowPaths(), null,
        (_, _) => Task.FromResult<string?>(null));

    private async Task<int> Launch(string script, string? input = null, CancellationToken cancellationToken = default)
    {
        var tool = (await new ExecToolContributor().ContributeAsync(Context())).Tools.ShouldHaveSingleItem();
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new[] { "pwsh", "-NoProfile", "-Command", script },
            ["background"] = true, ["input"] = input,
            // These are foreground-only budgets, not a licence to abandon a background child.
            ["timeoutMs"] = 1, ["noOutputTimeoutMs"] = 1,
        });
        var result = await tool.ExecuteAsync("launch", args, cancellationToken);
        using var json = JsonDocument.Parse(result.Content[0].Value);
        return json.RootElement.GetProperty("pid").GetInt32();
    }

    private async Task<string> Call(
        string action,
        int pid,
        CancellationToken token = default,
        int wait = 0,
        string? content = null)
    {
        var tool = (await new ProcessToolContributor().ContributeAsync(Context())).Tools.ShouldHaveSingleItem();
        var result = await tool.ExecuteAsync("manage", new Dictionary<string, object?>
        {
            ["action"] = action,
            ["pid"] = pid,
            ["timeoutMs"] = wait,
            ["tail"] = 0,
            ["content"] = content,
        }, token);
        return result.Content[0].Value;
    }

    [Fact]
    public async Task LargeUnbrokenOutput_DrainsWithoutDeadlockAndDisclosesBoundedTail()
    {
        var pid = await Launch("[Console]::Write(('x' * 300000)); [Console]::ReadLine(); [Console]::Error.Write('stderr-end'); [Console]::Write('stdout-end')");
        var child = BackgroundProcessRegistry.Instance.Get(_owner, pid);
        child.ShouldNotBeNull();
        // Initial stdin blocks the final markers until all large output has been drained. Await the
        // producer's input boundary by using a finite initial payload in a second phase is unnecessary:
        // total output fits the pipe only after drains have started, and the child cannot exit first.
        await child.WriteInputAsync("continue\n");
        (await Call("status", pid, wait: 30_000)).ShouldContain("Status: exited");
        var output = await Call("output", pid);
        output.ShouldContain("output truncated:");
        output.ShouldContain("stdout-end");
        output.ShouldContain("stderr-end");
        Encoding.UTF8.GetByteCount(output).ShouldBeLessThan(OutputRetentionPolicy.MaxOutputBytes + 512);
    }

    [Fact]
    public async Task InitialInput_ClosesStdinAndOutputRemainsAfterExecReturns()
    {
        var pid = await Launch("$text = [Console]::In.ReadToEnd(); [Console]::Write('received:' + $text)", "hello");
        (await Call("status", pid, wait: 30_000)).ShouldContain("Status: exited");
        (await Call("output", pid)).ShouldContain("received:hello");
        (await Call("status", pid)).ShouldContain("Exit Code: 0");
    }

    [Fact]
    public async Task LaunchCancellationAfterReturn_DoesNotCancelOwnedChild_AndKillRetainsExit()
    {
        using var launch = new CancellationTokenSource();
        var pid = await Launch("[Console]::ReadLine()", cancellationToken: launch.Token);
        await launch.CancelAsync();
        (await Call("status", pid)).ShouldContain("Status: running");
        (await Call("kill", pid)).ShouldContain("terminated");
        (await Call("status", pid, wait: 30_000)).ShouldContain("Status: exited");
        BackgroundProcessRegistry.Instance.Get(_owner, pid).ShouldNotBeNull();
    }

    [Fact]
    public async Task CancelStatusWait_PropagatesWithoutKillingChild()
    {
        var pid = await Launch("[Console]::ReadLine()");
        using var wait = new CancellationTokenSource();
        var pending = Call("status", pid, wait.Token, 30_000);
        await wait.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        (await Call("status", pid)).ShouldContain("Status: running");
        (await Call("kill", pid)).ShouldContain("terminated");
    }

    [Fact]
    public async Task CancelBlockedInput_PropagatesWithoutLosingProcessManagement()
    {
        var pid = await Launch("[Console]::WriteLine('ready'); Start-Sleep -Seconds 120");
        try
        {
            using var write = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var payload = new string('x', 8 * 1024 * 1024);

            Func<Task> send = async () =>
                _ = await Call("input", pid, write.Token, content: payload)
                    .WaitAsync(TimeSpan.FromSeconds(10));

            await Should.ThrowAsync<OperationCanceledException>(send);
            (await Call("status", pid)).ShouldContain("Status: running");
        }
        finally
        {
            (await Call("kill", pid)).ShouldContain("terminated");
            (await Call("status", pid, wait: 30_000)).ShouldContain("Status: exited");
        }
    }

    [Fact]
    public async Task Contributors_HonorAllowlist_AndProcessHasNoPublicParameterlessConstructor()
    {
        (await new ProcessToolContributor().ContributeAsync(Context("read"))).Tools.ShouldBeEmpty();
        (await new ExecToolContributor().ContributeAsync(Context("read"))).Tools.ShouldBeEmpty();
        typeof(ProcessTool).GetConstructor(Type.EmptyTypes).ShouldBeNull();
        (await new ProcessToolContributor().ContributeAsync(Context("PROCESS"))).Tools.ShouldHaveSingleItem().Name.ShouldBe("process");
    }

    [Fact]
    public void Decoder_SplitUnicodeAndAnsi_MatchesWholeStreamAtEveryBoundary()
    {
        const string text = "before\u001b[31m😀\u001b[0mafter\u001b]0;title\u0007!";
        for (var split = 0; split <= text.Length; split++)
        {
            var decoder = new BackgroundOutputDecoder();
            var output = decoder.Append(text.AsSpan(0, split)) + decoder.Append(text.AsSpan(split), final: true);
            output.ShouldBe("before😀after!");
        }
    }

    [Theory]
    [InlineData("\u001bPpayload\u001b\\", "\u0090payload\u009c")]
    [InlineData("\u001bXpayload\u001b\\", "\u0098payload\u009c")]
    [InlineData("\u001b^payload\u001b\\", "\u009epayload\u009c")]
    [InlineData("\u001b_payload\u001b\\", "\u009fpayload\u009c")]
    public void Decoder_EscAndC1ControlStrings_MatchAtEveryBoundary(string escString, string c1String)
    {
        foreach (var controlString in new[] { escString, c1String })
        {
            var text = $"before{controlString}after";
            for (var split = 0; split <= text.Length; split++)
            {
                var decoder = new BackgroundOutputDecoder();
                var output = decoder.Append(text.AsSpan(0, split)) + decoder.Append(text.AsSpan(split), final: true);
                output.ShouldBe("beforeafter");
            }
        }
    }

    [Fact]
    public void Buffer_SplitSurrogatePair_UsesActualUtf8Bytes()
    {
        var buffer = new BackgroundOutputBuffer(4);
        buffer.AppendChunk("\ud83d");
        buffer.AppendChunk("\ude00");
        buffer.RawSnapshot().ShouldBe("😀");
        buffer.RetainedBytes.ShouldBe(4);
        buffer.DiscardedBytes.ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DrainFailure_RetainsCapturedBytesAndIsNotReportedAsSuccessfulCompletion(bool failStdout)
    {
        var captured = new FailingReadStream("captured-before-failure", waitForRelease: true);
        using var root = StartProcess("exit 0");
        var child = new BackgroundProcess(
            root,
            "faulted-drain",
            DateTimeOffset.UtcNow,
            failStdout ? new StreamReader(captured) : StreamReader.Null,
            failStdout ? StreamReader.Null : new StreamReader(captured));
        var registry = new BackgroundProcessRegistry(maxExitedRetained: 0);
        registry.Register("owner", child);
        var tool = new ProcessTool(new ProcessManager(registry, "owner"));
        captured.Release();

        await child.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(30));

        child.IsComplete.ShouldBeFalse("a failed output drain cannot be successful completion");
        child.IsTerminal.ShouldBeTrue("failed capture must still reach a bounded terminal lifecycle state");
        child.OutputCaptureStatus.ShouldBe("incomplete (read failure)");
        var output = child.GetOutput();
        output.ShouldContain("[output capture incomplete (read failure)]");
        output.ShouldContain("captured-before-failure");
        var statusResult = await tool.ExecuteAsync("status", new Dictionary<string, object?>
        {
            ["action"] = "status",
            ["pid"] = child.Pid,
        });
        statusResult.Content[0].Value.ShouldContain("Output Capture: incomplete (read failure)");
        var outputResult = await tool.ExecuteAsync("output", new Dictionary<string, object?>
        {
            ["action"] = "output",
            ["pid"] = child.Pid,
            ["tail"] = 0,
        });
        outputResult.Content[0].Value.ShouldContain("[output capture incomplete (read failure)]");

        registry.Reap();
        registry.Get("owner", child.Pid).ShouldBeNull("terminal failed captures remain subject to bounded retention");
    }

    [Fact]
    public async Task DisposedDrain_IsReportedSeparatelyAndReachesTerminalLifecycle()
    {
        using var root = StartProcess("exit 0");
        var child = new BackgroundProcess(
            root,
            "disposed-drain",
            DateTimeOffset.UtcNow,
            new StreamReader(new FailingReadStream("", disposeInsteadOfFail: true)),
            StreamReader.Null);

        await child.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(30));

        child.IsComplete.ShouldBeFalse();
        child.IsTerminal.ShouldBeTrue();
        child.OutputCaptureStatus.ShouldBe("incomplete (stream closed during cleanup)");
        child.GetOutput().ShouldContain("[output capture incomplete (stream closed during cleanup)]");
    }

    [Fact]
    public async Task TailOne_ReturnsLastContentLineRatherThanTrailingSplitSentinel()
    {
        var pid = await Launch("[Console]::WriteLine('hello')");
        (await Call("status", pid, wait: 30_000)).ShouldContain("Status: exited");
        var child = BackgroundProcessRegistry.Instance.Get(_owner, pid);
        child.ShouldNotBeNull();
        child.GetOutput(1).TrimEnd('\r').ShouldBe("hello");
    }

    private static Process StartProcess(string command)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
        }
        else
        {
            info.ArgumentList.Add("-c");
        }
        info.ArgumentList.Add(command);
        return Process.Start(info) ?? throw new InvalidOperationException("child did not start");
    }

    private sealed class FailingReadStream(
        string captured,
        bool disposeInsteadOfFail = false,
        bool waitForRelease = false) : Stream
    {
        private readonly byte[] _captured = Encoding.UTF8.GetBytes(captured);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _returnedCaptured;

        public void Release() => _released.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (waitForRelease) await _released.Task.WaitAsync(cancellationToken);
            if (_returnedCaptured || _captured.Length == 0)
            {
                if (disposeInsteadOfFail) throw new ObjectDisposedException(nameof(FailingReadStream));
                throw new IOException("deterministic output capture failure");
            }
            _returnedCaptured = true;
            _captured.CopyTo(buffer);
            return _captured.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AllowPaths : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }
}
