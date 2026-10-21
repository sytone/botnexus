using System.Text.Json;

namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>Wire contract verified against upstream agent-browser v0.33.2, without a browser.</summary>
public sealed class AgentBrowserCliContractTests
{
    private static AgentBrowserCli Create(IAgentBrowserProcessRunner runner) => new(
        "contract-session", new AgentBrowserResolution(AgentBrowserSource.ConfiguredPath, "/fake/agent-browser", null),
        runner, new BrowserToolsConfig { CommandTimeoutSeconds = 17 }, _ => null);

    [Fact]
    public async Task Navigate_UsesCanonicalOpenAndColdThenWarmBudgets()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        await using var cli = Create(runner);
        await cli.NavigateAsync("https://example.com/a b");
        await cli.NavigateAsync("https://example.com/second");
        runner.Invocations[0].Arguments.ShouldBe(new[] { "--session", "contract-session", "open", "https://example.com/a b" });
        runner.Invocations[0].Timeout.ShouldBe(TimeSpan.FromSeconds(120));
        runner.Invocations[1].Timeout.ShouldBe(TimeSpan.FromSeconds(17));
    }

    [Fact]
    public async Task Url_RequestsJsonAndExtractsOnlyDataUrl()
    {
        var runner = new FakeAgentBrowserProcessRunner { DefaultResult = new(0, "{\"success\":true,\"data\":{\"url\":\"https://example.com/a?x=1&y=2\",\"title\":\"ignored\"},\"error\":null}", "") };
        await using var cli = Create(runner);
        (await cli.GetCurrentUrlAsync()).ShouldBe("https://example.com/a?x=1&y=2");
        runner.Invocations.Single().Arguments.ShouldBe(new[] { "--session", "contract-session", "--json", "get", "url" });
    }

    [Fact]
    public async Task Snapshot_RequestsJsonAndPreservesDecodedText()
    {
        const string text = "  - heading \"hello\"\n- text: café 😀\n";
        var runner = new FakeAgentBrowserProcessRunner { DefaultResult = new(0, JsonSerializer.Serialize(new { success = true, data = new { snapshot = text, refs = new { } } }), "") };
        await using var cli = Create(runner);
        (await cli.GetPageTextAsync()).ShouldBe(text);
        runner.Invocations.Single().Arguments.ShouldBe(new[] { "--session", "contract-session", "--json", "snapshot" });
    }

    [Fact]
    public async Task Reads_RejectMalformedOrUnsuccessfulEnvelopesWithoutLeakingPayload()
    {
        string[] outputs = ["SECRET-raw", "[]", "null", "{}", "{\"success\":\"true\",\"data\":{}}",
            "{\"success\":false,\"error\":\"SECRET-error\",\"data\":{\"url\":\"SECRET-url\",\"snapshot\":\"SECRET-page\"}}",
            "{\"success\":true,\"data\":null}", "{\"success\":true,\"data\":{}}",
            "{\"success\":true,\"data\":{\"url\":12,\"snapshot\":false}}"];
        foreach (var output in outputs)
        {
            var runner = new FakeAgentBrowserProcessRunner { DefaultResult = new(0, output, "") };
            await using var cli = Create(runner);
            var urlError = await Should.ThrowAsync<AgentBrowserUnavailableException>(() => cli.GetCurrentUrlAsync());
            var snapshotError = await Should.ThrowAsync<AgentBrowserUnavailableException>(() => cli.GetPageTextAsync());
            urlError.Message.ShouldNotContain("SECRET");
            snapshotError.Message.ShouldNotContain("SECRET");
        }
    }

    [Fact]
    public async Task EmptySnapshot_IsValidButEmptyUrlIsNot()
    {
        var runner = new FakeAgentBrowserProcessRunner { DefaultResult = new(0, "{\"success\":true,\"data\":{\"url\":\"  \",\"snapshot\":\"\"}}", "") };
        await using var cli = Create(runner);
        (await cli.GetPageTextAsync()).ShouldBe(string.Empty);
        await Should.ThrowAsync<AgentBrowserUnavailableException>(() => cli.GetCurrentUrlAsync());
    }

    [Fact]
    public async Task Type_SubmitUsesSeparatePressAndNoSubmitFlag()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        await using var cli = Create(runner);
        await cli.TypeAsync("input[name=q]", "hello world; & \"quoted\"", submit: true);
        await cli.TypeAsync("input", "plain");
        runner.Commands.ShouldBe(new[] { "type", "press", "type" });
        runner.Invocations[0].Arguments.ShouldBe(new[] { "--session", "contract-session", "type", "input[name=q]", "hello world; & \"quoted\"" });
        runner.Invocations[1].Arguments.ShouldBe(new[] { "--session", "contract-session", "press", "Enter" });
        runner.Invocations.ShouldAllBe(i => i.Timeout == TimeSpan.FromSeconds(17));
    }

    [Theory]
    [InlineData("--clear", false)]
    [InlineData("--clear", true)]
    [InlineData("--delay", false)]
    [InlineData("--delay", true)]
    public async Task Type_UnsupportedOptionLiteralIsRejectedBeforeAnyProcess(string text, bool submit)
    {
        var runner = new FakeAgentBrowserProcessRunner();
        await using var cli = Create(runner);
        var error = await Should.ThrowAsync<ArgumentException>(() => cli.TypeAsync("input", text, submit));
        error.ParamName.ShouldBe("text");
        error.Message.ShouldNotContain(text);
        runner.Invocations.ShouldBeEmpty();
        await cli.ClickAsync("button").WaitAsync(TimeSpan.FromSeconds(5));
        runner.Commands.ShouldBe(new[] { "click" });
    }

    [Fact]
    public async Task Type_OnlyExactUnsupportedLiteralsAreRejected()
    {
        string[] texts = ["ordinary text", "\"--clear\"", "'--delay'", "prefix --clear", "--clear ", "--delay=5", "--CLEAR"];
        var runner = new FakeAgentBrowserProcessRunner();
        await using var cli = Create(runner);
        foreach (var text in texts)
        {
            await cli.TypeAsync("input", text);
            runner.Invocations.Last().Arguments.ShouldBe(new[] { "--session", "contract-session", "type", "input", text });
        }
        runner.Commands.ShouldBe(Enumerable.Repeat("type", texts.Length));
    }

    [Fact]
    public async Task Type_FailureDoesNotPressEnter()
    {
        var runner = new FakeAgentBrowserProcessRunner().ForCommand("type", new(1, "", "failed"));
        await using var cli = Create(runner);
        await Should.ThrowAsync<AgentBrowserUnavailableException>(() => cli.TypeAsync("input", "secret", true));
        runner.Commands.ShouldBe(new[] { "type" });
    }

    [Fact]
    public async Task Type_PressFailureIsNotReportedAsSuccess()
    {
        var runner = new FakeAgentBrowserProcessRunner().ForCommand("press", new(1, "", "failed"));
        await using var cli = Create(runner);
        await Should.ThrowAsync<AgentBrowserUnavailableException>(() => cli.TypeAsync("input", "secret", true));
        runner.Commands.ShouldBe(new[] { "type", "press" });
    }

    [Fact]
    public async Task Type_SubmitSequenceCannotInterleaveAnotherCommand()
    {
        var runner = new PausingRunner();
        await using var cli = Create(runner);
        var typing = cli.TypeAsync("input", "text", true);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var click = cli.ClickAsync("button"); // synchronously reaches the occupied session gate
        runner.Release.SetResult();
        await Task.WhenAll(typing, click).WaitAsync(TimeSpan.FromSeconds(5));
        runner.Commands.ShouldBe(new[] { "type", "press", "click" });
    }

    [Fact]
    public async Task Type_CancellationDoesNotPressAndReleasesSessionGate()
    {
        using var cts = new CancellationTokenSource();
        var runner = new PausingRunner();
        await using var cli = Create(runner);
        var typing = cli.TypeAsync("input", "text", true, cts.Token);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        runner.Release.SetResult();
        await Should.ThrowAsync<OperationCanceledException>(() => typing);
        await cli.ClickAsync("button");
        runner.Commands.ShouldBe(new[] { "type", "click" });
    }

    [Fact]
    public async Task Type_SuccessfulRunnerCancellingTokenDoesNotPressAndReleasesSessionGate()
    {
        using var cts = new CancellationTokenSource();
        var runner = new SuccessfulCancellingRunner(cts);
        await using var cli = Create(runner);
        await Should.ThrowAsync<OperationCanceledException>(() => cli.TypeAsync("input", "text", true, cts.Token));
        cts.IsCancellationRequested.ShouldBeTrue();
        runner.Commands.ShouldBe(new[] { "type" });
        await cli.ClickAsync("button").WaitAsync(TimeSpan.FromSeconds(5));
        runner.Commands.ShouldBe(new[] { "type", "click" });
    }

    [Fact]
    public async Task Type_SubmitSequenceCannotInterleaveClose()
    {
        var runner = new PausingRunner();
        var cli = Create(runner);
        var typing = cli.TypeAsync("input", "text", true);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = cli.DisposeAsync().AsTask();
        runner.Release.SetResult();
        await Task.WhenAll(typing, closing).WaitAsync(TimeSpan.FromSeconds(5));
        runner.Commands.ShouldBe(new[] { "type", "press", "close" });
        await cli.DisposeAsync();
        runner.Commands.ShouldBe(new[] { "type", "press", "close" });
        await Should.ThrowAsync<ObjectDisposedException>(() => cli.ClickAsync("button"));
    }

    [Fact]
    public async Task Type_FailureReleasesSessionGate()
    {
        var runner = new FakeAgentBrowserProcessRunner().ForCommand("type", new(1, "", "failed"));
        await using var cli = Create(runner);
        await Should.ThrowAsync<AgentBrowserUnavailableException>(() => cli.TypeAsync("input", "secret", true));
        await cli.ClickAsync("button").WaitAsync(TimeSpan.FromSeconds(5));
        runner.Commands.ShouldBe(new[] { "type", "click" });
    }

    [Fact]
    public async Task Screenshot_UsesPositionalPathAndFullFlag()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        await using var cli = Create(runner);
        const string path = "/workspace/screen shot.png";
        (await cli.ScreenshotAsync(path, true)).ShouldBe(path);
        await cli.ScreenshotAsync(path);
        runner.Invocations[0].Arguments.ShouldBe(new[] { "--session", "contract-session", "screenshot", path, "--full" });
        runner.Invocations[1].Arguments.ShouldBe(new[] { "--session", "contract-session", "screenshot", path });
    }

    private sealed class SuccessfulCancellingRunner(CancellationTokenSource source) : IAgentBrowserProcessRunner
    {
        public List<string> Commands { get; } = [];
        public Task<AgentBrowserProcessResult> RunAsync(string binaryPath, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var command = arguments[2];
            Commands.Add(command);
            if (command == "type")
            {
                cancellationToken.ShouldBe(source.Token);
                source.Cancel();
                // Deliberately return success without observing the now-cancelled token.
            }
            return Task.FromResult(new AgentBrowserProcessResult(0, "", ""));
        }
    }

    private sealed class PausingRunner : IAgentBrowserProcessRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Commands { get; } = [];
        public async Task<AgentBrowserProcessResult> RunAsync(string binaryPath, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var command = arguments[2];
            Commands.Add(command);
            if (command == "type")
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new(0, "", "");
        }
    }
}
