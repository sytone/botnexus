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

    private async Task<string> Call(string action, int pid, CancellationToken token = default, int wait = 0)
    {
        var tool = (await new ProcessToolContributor().ContributeAsync(Context())).Tools.ShouldHaveSingleItem();
        var result = await tool.ExecuteAsync("manage", new Dictionary<string, object?>
        { ["action"] = action, ["pid"] = pid, ["timeoutMs"] = wait, ["tail"] = 0 }, token);
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
        child.WriteInput("continue\n");
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

    [Fact]
    public async Task TailOne_ReturnsLastContentLineRatherThanTrailingSplitSentinel()
    {
        var pid = await Launch("[Console]::WriteLine('hello')");
        (await Call("status", pid, wait: 30_000)).ShouldContain("Status: exited");
        var child = BackgroundProcessRegistry.Instance.Get(_owner, pid);
        child.ShouldNotBeNull();
        child.GetOutput(1).TrimEnd('\r').ShouldBe("hello");
    }

    private sealed class AllowPaths : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }
}
