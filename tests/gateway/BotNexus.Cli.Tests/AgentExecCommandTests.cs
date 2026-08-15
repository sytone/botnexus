using System.Net;
using System.Text.Json;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Behaviour tests for <c>botnexus agent exec</c> (#2396). Every assertion goes through a stub
/// transport rather than a live gateway, so the suite exercises the command's contract - exit codes,
/// stream discipline, request shape - without starting an agent.
/// </summary>
public sealed class AgentExecCommandTests
{
    private const string Agent = "farnsworth";
    private const string Prompt = "summarise the build";

    private static AgentExecRequest Request(
        bool asJson = false,
        int timeoutSeconds = 30,
        string? model = null,
        string? thinking = null,
        string? conversation = null,
        string agentId = Agent,
        string prompt = Prompt,
        string baseUrl = "http://localhost:5005",
        string? token = null)
        => new(
            AgentId: agentId,
            Prompt: prompt,
            AsJson: asJson,
            TimeoutSeconds: timeoutSeconds,
            Model: model,
            Thinking: thinking,
            ConversationId: conversation,
            BaseUrl: baseUrl,
            Token: token);

    // ── happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exec_WhenRunSucceeds_WritesOnlyTheAnswerToStdoutAndReturnsZero()
    {
        var handler = StubHandler.Ok("""
            {"sessionId":"s1","content":"the build is green","usage":{"inputTokens":10,"outputTokens":4},"toolCalls":[]}
            """);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await AgentExecCommand.ExecuteAsync(Request(), CancellationToken.None, stdout, stderr, handler);

        exit.ShouldBe(AgentExecExitCode.Success);
        stdout.ToString().Trim().ShouldBe("the build is green");
        // Stream discipline: nothing may contaminate stdout on the success path.
        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task Exec_PostsAgentIdPromptAndOverridesToTheChatEndpoint()
    {
        var handler = StubHandler.Ok("""{"sessionId":"s1","content":"ok","toolCalls":[]}""");

        var exit = await AgentExecCommand.ExecuteAsync(
            Request(model: "claude-opus-5", thinking: "high", conversation: "existing-session"),
            CancellationToken.None,
            new StringWriter(),
            new StringWriter(),
            handler);

        exit.ShouldBe(AgentExecExitCode.Success);
        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/chat");
        var sent = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        sent.GetProperty("agentId").GetString().ShouldBe(Agent);
        sent.GetProperty("message").GetString().ShouldBe(Prompt);
        sent.GetProperty("model").GetString().ShouldBe("claude-opus-5");
        sent.GetProperty("thinking").GetString().ShouldBe("high");
        sent.GetProperty("sessionId").GetString().ShouldBe("existing-session");
    }

    // ── --json shape ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Exec_WithJson_EmitsTextToolCallsUsageAndSessionIdAsOneParseableDocument()
    {
        var handler = StubHandler.Ok("""
            {"sessionId":"sess-42","content":"done","usage":{"inputTokens":100,"outputTokens":25,"cacheRead":7,"cacheWrite":3},
             "toolCalls":[{"toolCallId":"tc1","toolName":"read","isError":false}]}
            """);
        var stdout = new StringWriter();

        var exit = await AgentExecCommand.ExecuteAsync(
            Request(asJson: true), CancellationToken.None, stdout, new StringWriter(), handler);

        exit.ShouldBe(AgentExecExitCode.Success);
        var doc = JsonDocument.Parse(stdout.ToString()).RootElement;
        doc.GetProperty("sessionId").GetString().ShouldBe("sess-42");
        doc.GetProperty("agentId").GetString().ShouldBe(Agent);
        doc.GetProperty("text").GetString().ShouldBe("done");
        doc.GetProperty("exitCode").GetInt32().ShouldBe(AgentExecExitCode.Success);
        doc.GetProperty("usage").GetProperty("inputTokens").GetInt32().ShouldBe(100);
        doc.GetProperty("usage").GetProperty("cacheWrite").GetInt32().ShouldBe(3);
        var calls = doc.GetProperty("toolCalls");
        calls.GetArrayLength().ShouldBe(1);
        calls[0].GetProperty("toolName").GetString().ShouldBe("read");
        calls[0].GetProperty("isError").GetBoolean().ShouldBeFalse();
    }

    // ── sad paths ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exec_WhenAgentIsUnknown_ReturnsUnknownAgentCodeAndKeepsStdoutEmpty()
    {
        var handler = StubHandler.Status(HttpStatusCode.NotFound, """{"error":"Agent 'nope' is not registered."}""");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await AgentExecCommand.ExecuteAsync(
            Request(agentId: "nope"), CancellationToken.None, stdout, stderr, handler);

        exit.ShouldBe(AgentExecExitCode.UnknownAgent);
        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().ShouldContain("not registered");
    }

    [Fact]
    public async Task Exec_WhenRunExceedsTimeout_ReturnsTimeoutCodeDistinctFromGenericFailure()
    {
        var handler = StubHandler.Hangs();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await AgentExecCommand.ExecuteAsync(
            Request(timeoutSeconds: 1), CancellationToken.None, stdout, stderr, handler);

        exit.ShouldBe(AgentExecExitCode.Timeout);
        exit.ShouldNotBe(AgentExecExitCode.Failure);
        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().ShouldContain("timeout");
    }

    [Fact]
    public async Task Exec_WhenAToolCallFailed_ReturnsToolFailureCodeEvenThoughTheModelAnswered()
    {
        var handler = StubHandler.Ok("""
            {"sessionId":"s1","content":"I could not read the file","toolCalls":[
              {"toolCallId":"tc1","toolName":"read","isError":true}]}
            """);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await AgentExecCommand.ExecuteAsync(Request(), CancellationToken.None, stdout, stderr, handler);

        exit.ShouldBe(AgentExecExitCode.ToolFailure);
        // The answer is still delivered - a tool failure is not a reason to swallow the text.
        stdout.ToString().Trim().ShouldBe("I could not read the file");
        stderr.ToString().ShouldContain("read");
    }

    [Fact]
    public async Task Exec_WithJson_StillEmitsOneParseableDocumentWhenAToolFailed()
    {
        var handler = StubHandler.Ok("""
            {"sessionId":"s1","content":"partial","toolCalls":[{"toolCallId":"tc1","toolName":"exec","isError":true}]}
            """);
        var stdout = new StringWriter();

        var exit = await AgentExecCommand.ExecuteAsync(
            Request(asJson: true), CancellationToken.None, stdout, new StringWriter(), handler);

        exit.ShouldBe(AgentExecExitCode.ToolFailure);
        var doc = JsonDocument.Parse(stdout.ToString()).RootElement;
        doc.GetProperty("exitCode").GetInt32().ShouldBe(AgentExecExitCode.ToolFailure);
        doc.GetProperty("toolCalls")[0].GetProperty("isError").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Exec_WhenGatewayReturnsServerError_ReturnsGenericFailure()
    {
        var handler = StubHandler.Status(HttpStatusCode.InternalServerError, "boom");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await AgentExecCommand.ExecuteAsync(Request(), CancellationToken.None, stdout, stderr, handler);

        exit.ShouldBe(AgentExecExitCode.Failure);
        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().ShouldContain("500");
    }

    [Fact]
    public async Task Exec_WhenGatewayIsUnreachable_ReturnsFailureWithoutThrowing()
    {
        var handler = StubHandler.Throws(new HttpRequestException("connection refused"));
        var stderr = new StringWriter();

        var exit = await AgentExecCommand.ExecuteAsync(Request(), CancellationToken.None, new StringWriter(), stderr, handler);

        exit.ShouldBe(AgentExecExitCode.Failure);
        stderr.ToString().ShouldContain("connection refused");
    }

    [Fact]
    public async Task Exec_WithEmptyPrompt_ReturnsFailureWithoutContactingTheGateway()
    {
        var handler = StubHandler.Ok("""{"sessionId":"s1","content":"never"}""");

        var exit = await AgentExecCommand.ExecuteAsync(
            Request(prompt: "   "), CancellationToken.None, new StringWriter(), new StringWriter(), handler);

        exit.ShouldBe(AgentExecExitCode.Failure);
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Exec_WithNonPositiveTimeout_ReturnsFailureWithoutContactingTheGateway()
    {
        var handler = StubHandler.Ok("""{"sessionId":"s1","content":"never"}""");

        var exit = await AgentExecCommand.ExecuteAsync(
            Request(timeoutSeconds: 0), CancellationToken.None, new StringWriter(), new StringWriter(), handler);

        exit.ShouldBe(AgentExecExitCode.Failure);
        handler.CallCount.ShouldBe(0);
    }

    // ── approval posture (load-bearing clause of #2396) ────────────────────────

    [Fact]
    public void Exec_ExposesNoOptionThatCouldWaiveToolApproval()
    {
        // The headless runner must not become an approval bypass. It has no authority to waive a
        // policy decision made inside the gateway, so no such option may exist on the surface -
        // adding one is the mechanical form of the bypass this test exists to prevent.
        var command = AgentExecCommand.Build(new System.CommandLine.Option<bool>("--verbose"));

        var optionNames = command.Options.Select(o => o.Name).ToArray();
        var aliases = command.Options.SelectMany(o => o.Aliases).ToArray();

        foreach (var forbidden in new[] { "yes", "auto-approve", "approve-all", "no-approval", "skip-approval", "force", "dangerously-skip-permissions" })
        {
            optionNames.ShouldNotContain(forbidden);
            aliases.ShouldNotContain($"--{forbidden}");
        }
    }

    [Fact]
    public async Task Exec_RunsThroughTheGatewayChatEndpoint_SoToolPolicyAndApprovalApplyUnchanged()
    {
        // The approval and tool-policy hooks live inside the gateway's execution path. Delegating to
        // /api/chat is what makes a headless run subject to them; executing the agent in-process from
        // the CLI would necessarily re-derive - and could silently omit - those hooks. This asserts
        // the delegation is real, and that the CLI sends no policy-affecting field of its own.
        var handler = StubHandler.Ok("""{"sessionId":"s1","content":"ok","toolCalls":[]}""");

        await AgentExecCommand.ExecuteAsync(Request(), CancellationToken.None, new StringWriter(), new StringWriter(), handler);

        handler.CallCount.ShouldBe(1);
        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/chat");

        var sent = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        foreach (var forbidden in new[] { "autoApprove", "approveAll", "skipApproval", "bypassApproval", "toolPolicy", "allowedTools", "force" })
            sent.TryGetProperty(forbidden, out _).ShouldBeFalse($"agent exec must not send '{forbidden}' - approval is the gateway's decision.");
    }

    [Fact]
    public async Task Exec_AgainstARemoteGatewayWithoutAToken_IsRefusedRatherThanSentUnauthenticated()
    {
        // The credential policy (#2747) must apply to this command exactly as it does to the others:
        // a new command surface that skipped it would be a second, weaker definition of the rule.
        var handler = StubHandler.Ok("""{"sessionId":"s1","content":"ok"}""");
        var stderr = new StringWriter();

        var exit = await AgentExecCommand.ExecuteAsync(
            Request(baseUrl: "https://gateway.example.com"),
            CancellationToken.None,
            new StringWriter(),
            stderr,
            handler);

        exit.ShouldBe(AgentExecExitCode.Failure);
        handler.CallCount.ShouldBe(0);
        stderr.ToString().ShouldContain("--token");
    }

    // ── registration ──────────────────────────────────────────────────────────

    [Fact]
    public void AgentCommandGroup_ExposesExecAlongsideTheConfigurationSubcommands()
    {
        var group = new AgentCommands().Build(
            new System.CommandLine.Option<bool>("--verbose"),
            new System.CommandLine.Option<string?>("--target"));

        var exec = group.Subcommands.SingleOrDefault(c => c.Name == "exec");
        exec.ShouldNotBeNull();

        var optionNames = exec.Options.Select(o => o.Name).ToArray();
        optionNames.ShouldContain("json");
        optionNames.ShouldContain("timeout");
        optionNames.ShouldContain("model");
        optionNames.ShouldContain("thinking");
        optionNames.ShouldContain("conversation");

        exec.Arguments.Select(a => a.Name).ShouldBe(["agentId", "prompt"]);
    }

    /// <summary>
    /// Deterministic transport stub. Records the outbound request so the tests can assert the wire
    /// shape, which is the only way to prove the CLI delegates rather than re-implements.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        private StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => _respond = respond;

        public int CallCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public string? LastRequestBody { get; private set; }

        public static StubHandler Ok(string json) => Status(HttpStatusCode.OK, json);

        public static StubHandler Status(HttpStatusCode status, string body)
            => new((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            }));

        public static StubHandler Throws(Exception ex) => new((_, _) => Task.FromException<HttpResponseMessage>(ex));

        public static StubHandler Hangs()
            => new(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new UnreachableException();
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(CancellationToken.None);

            return await _respond(request, cancellationToken);
        }

        private sealed class UnreachableException : Exception;
    }
}
