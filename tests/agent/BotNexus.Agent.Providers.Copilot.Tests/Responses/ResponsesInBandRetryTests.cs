using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Agent.Providers.Copilot.Tests.Responses;

/// <summary>Pure in-memory transport/provider/loop composition; never starts a host or gateway.</summary>
public class ResponsesInBandRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_TransientBeforeOutput_RetriesOnlyProviderAttempt(bool websocket)
    {
        var fixture = new Fixture(websocket, attempt => attempt == 1 ? [Failure()] : Success());
        var result = await fixture.RunAsync();
        fixture.Attempts.ShouldBe(2);
        result.OfType<AssistantAgentMessage>().ShouldContain(x => x.Content == "recovered");
        fixture.Events.OfType<MessageEndEvent>().Count(x => x.Message is AssistantAgentMessage { FinishReason: StopReason.Error }).ShouldBe(0);
        fixture.Events.OfType<ProviderRecoveryEvent>().Count(x => x.Observation.Stage == ProviderRecoveryStage.RetryScheduled).ShouldBe(1);
        fixture.Events.OfType<ProviderRecoveryEvent>().Count(x => x.Observation.Stage == ProviderRecoveryStage.Recovered).ShouldBe(1);
        fixture.Coordinator.GetSnapshot(fixture.Scope).InFlightCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_PersistentTransient_FourAttemptsOneTerminalFailure(bool websocket)
    {
        var fixture = new Fixture(websocket, _ => [Failure()]);
        var exception = await Should.ThrowAsync<Exception>(() => fixture.RunAsync());
        fixture.Attempts.ShouldBe(4);
        fixture.Events.OfType<MessageEndEvent>().Count(x => x.Message is AssistantAgentMessage { FinishReason: StopReason.Error }).ShouldBe(0);
        fixture.Events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.ShouldNotBeNull();
        fixture.Events.OfType<ProviderRecoveryEvent>().Count(x => x.Observation.Stage == ProviderRecoveryStage.RetryScheduled).ShouldBe(3);
        fixture.Events.OfType<ProviderRecoveryEvent>().Count(x => x.Observation.Stage == ProviderRecoveryStage.Exhausted).ShouldBe(1);
        exception.Message.ShouldContain("github-copilot");
        exception.Message.ShouldContain("Transient");
        exception.Message.ShouldNotContain("private-envelope");
        exception.Message.Length.ShouldBeLessThan(600);
        fixture.Coordinator.GetSnapshot(fixture.Scope).DistinctTransientFailures.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, "text")]
    [InlineData(true, "text")]
    [InlineData(false, "thinking")]
    [InlineData(true, "thinking")]
    [InlineData(false, "tool")]
    [InlineData(true, "tool")]
    [InlineData(false, "text-end")]
    [InlineData(true, "text-end")]
    public async Task RunAsync_ExposedSemanticOutput_PreservesEvidenceWithoutReplay(bool websocket, string kind)
    {
        var fixture = new Fixture(websocket, _ => [.. Partial(kind), Failure()]);
        var result = await fixture.RunAsync();
        fixture.Attempts.ShouldBe(1);
        var message = result.OfType<AssistantAgentMessage>().Single();
        message.FinishReason.ShouldBe(StopReason.Error);
        message.ErrorMessage.ShouldNotBeNull();
        message.ErrorMessage.ShouldContain("Transient");
        message.ErrorMessage.ShouldContain("verify");
        message.ContentBlocks.ShouldNotBeNull();
        message.ContentBlocks.Count.ShouldBeGreaterThan(0);
        if (kind is "text" or "text-end") message.Content.ShouldContain("visible evidence");
        if (kind == "thinking") message.ContentBlocks.OfType<ThinkingContent>().ShouldHaveSingleItem().Thinking.ShouldBe("visible evidence");
        if (kind == "tool") message.ToolCalls.ShouldNotBeEmpty();
        fixture.Events.OfType<MessageEndEvent>().Where(x => x.Message is AssistantAgentMessage).ShouldHaveSingleItem();
        fixture.Events.OfType<ProviderRecoveryEvent>().ShouldNotContain(x => x.Observation.Stage == ProviderRecoveryStage.Recovered);
        fixture.Coordinator.GetSnapshot(fixture.Scope).DistinctTransientFailures.ShouldBe(1);
    }

    [Theory]
    [InlineData("invalid_api_key", "invalid api key")]
    [InlineData("insufficient_quota", "quota exhausted")]
    [InlineData("policy_rejection", "content policy rejected")]
    public async Task RunAsync_NonTransientHalfOpenFailure_DoesNotReportSuccess(string code, string detail)
    {
        var fixture = new Fixture(true, _ => [Failure(code, detail)]);
        await fixture.OpenCircuitAsync();
        try { await fixture.RunAsync(); } catch (Exception) { }
        fixture.Attempts.ShouldBe(1);
        fixture.Coordinator.GetSnapshot(fixture.Scope).State.ShouldNotBe(ProviderRecoveryState.Closed);
        fixture.Events.OfType<ProviderRecoveryEvent>().ShouldNotContain(x => x.Observation.Stage == ProviderRecoveryStage.Recovered);
        fixture.Events.OfType<ProviderRecoveryEvent>().ShouldNotContain(x => x.Observation.Stage == ProviderRecoveryStage.RetryScheduled);
        var released = fixture.Coordinator.GetSnapshot(fixture.Scope);
        released.State.ShouldBe(ProviderRecoveryState.Open);
        released.InFlightCalls.ShouldBe(0);
        released.DistinctTransientFailures.ShouldBe(10);
        released.NextProbeAt.ShouldNotBeNull();
        if (code is "invalid_api_key" or "insufficient_quota")
        {
            fixture.Suspensions.IsSuspended(fixture.Scope.Provider, fixture.Scope.AuthProfile).ShouldBeTrue();
            await Should.ThrowAsync<ProviderExhaustedException>(() => fixture.RunAsync());
            fixture.Attempts.ShouldBe(1, "abandoning a probe must not bypass credential suspension");
        }
        fixture.AdvanceToNextProbe();
        var admission = fixture.Coordinator.AcquireAsync(fixture.Scope, Guid.NewGuid(), TimeSpan.FromSeconds(1), CancellationToken.None);
        admission.IsCompletedSuccessfully.ShouldBeTrue("later admission must not remain stranded in HalfOpen");
        var laterProbe = await admission;
        laterProbe.IsProbe.ShouldBeTrue();
        fixture.Attempts.ShouldBe(1, "coordinator admission must not replay the failed run");
        laterProbe.ReportSuccess();
        fixture.Coordinator.GetSnapshot(fixture.Scope).State.ShouldBe(ProviderRecoveryState.Closed);
    }

    [Fact]
    public async Task RunAsync_CancelledHalfOpenProbe_ReopensWithoutRetryAndAllowsLaterRun()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture(true, attempt =>
        {
            if (attempt != 1)
                return Success();
            cancellation.Cancel();
            return [Failure()];
        });
        await fixture.OpenCircuitAsync();
        var generation = fixture.Coordinator.GetSnapshot(fixture.Scope).Generation;

        await Should.ThrowAsync<OperationCanceledException>(() => fixture.RunAsync(cancellation.Token));

        fixture.Attempts.ShouldBe(1);
        var released = fixture.Coordinator.GetSnapshot(fixture.Scope);
        released.State.ShouldBe(ProviderRecoveryState.Open);
        released.Generation.ShouldBe(generation + 1);
        released.InFlightCalls.ShouldBe(0);
        released.DistinctTransientFailures.ShouldBe(10);
        released.NextProbeAt.ShouldNotBeNull();
        fixture.Suspensions.IsSuspended(fixture.Scope.Provider, fixture.Scope.AuthProfile).ShouldBeFalse();
        fixture.Events.OfType<ProviderRecoveryEvent>().ShouldNotContain(x => x.Observation.Stage == ProviderRecoveryStage.Recovered);
        fixture.Events.OfType<ProviderRecoveryEvent>().ShouldNotContain(x => x.Observation.Stage == ProviderRecoveryStage.RetryScheduled);

        fixture.AdvanceToNextProbe();
        var result = await fixture.RunAsync();
        fixture.Attempts.ShouldBe(2);
        result.OfType<AssistantAgentMessage>().ShouldContain(x => x.Content == "recovered");
        fixture.Coordinator.GetSnapshot(fixture.Scope).State.ShouldBe(ProviderRecoveryState.Closed);
        fixture.Coordinator.GetSnapshot(fixture.Scope).InFlightCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_ContextOverflow_RetainsSeparateOneRecoveryBudget()
    {
        var fixture = new Fixture(false, _ => [Failure("context_length_exceeded", "maximum context length exceeded")]);
        await fixture.RunAsync();
        fixture.Attempts.ShouldBe(2);
        fixture.Events.OfType<ProviderRecoveryEvent>().ShouldNotContain(x => x.Observation.Stage == ProviderRecoveryStage.RetryScheduled);
        fixture.Coordinator.GetSnapshot(fixture.Scope).InFlightCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_NoRetryOrRecoverySuccess()
    {
        using var cts = new CancellationTokenSource();
        var fixture = new Fixture(true, _ => { cts.Cancel(); return [Failure()]; });
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.RunAsync(cts.Token));
        fixture.Attempts.ShouldBe(1);
        fixture.Events.OfType<ProviderRecoveryEvent>().ShouldNotContain(x => x.Observation.Stage == ProviderRecoveryStage.Recovered);
        fixture.Coordinator.GetSnapshot(fixture.Scope).InFlightCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CompletedToolThenTransient_ExecutesToolOnce(bool websocket)
    {
        var tool = new CountingTool();
        var fixture = new Fixture(websocket, attempt => attempt switch
        {
            1 => ["{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"t\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"echo\",\"arguments\":\"{}\"}}", "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}"],
            2 => [Failure()],
            _ => Success()
        });
        await fixture.RunAsync(tools: [tool]);
        fixture.Attempts.ShouldBe(3);
        tool.Calls.ShouldBe(1);
        fixture.Events.OfType<ToolExecutionEndEvent>().ShouldHaveSingleItem();
        fixture.ProviderContexts[1].OfType<ToolResultMessage>().ShouldHaveSingleItem().Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("completed evidence");
        fixture.ProviderContexts[2].OfType<ToolResultMessage>().ShouldHaveSingleItem().Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("completed evidence");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ErrorDetail_IsBoundedRedactedAndSeparate(bool websocket)
    {
        var fixture = new Fixture(websocket, _ => [Failure("internal_error", "internal server error secret-5014 " + new string('z', 2000))]);
        var error = await Should.ThrowAsync<Exception>(() => fixture.RunAsync());
        error.Message.ShouldNotContain("secret-5014");
        error.Message.ShouldNotContain("zzzz");
        fixture.Diagnostics.ShouldNotBeEmpty();
        fixture.Diagnostics.ShouldAllBe(x => x.Length <= 512 && !x.Contains("secret-5014") && !x.Contains("private-envelope"));
        fixture.Diagnostics.ShouldContain(x => x.Contains("[REDACTED]"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_PartialExhaustion_PreservesEvidenceAndSuspendsProfile(bool websocket)
    {
        var fixture = new Fixture(websocket, _ => [.. Partial("text"), Failure("insufficient_quota", "quota exhausted")]);
        var result = await fixture.RunAsync();
        result.OfType<AssistantAgentMessage>().Single().Content.ShouldContain("visible evidence");
        fixture.Attempts.ShouldBe(1);
        fixture.Suspensions.IsSuspended("github-copilot", "").ShouldBeTrue();
        await Should.ThrowAsync<ProviderExhaustedException>(() => fixture.RunAsync());
        fixture.Attempts.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_TerminalResponseFailed_BoundsRedactedDetail(bool websocket)
    {
        var payload = JsonSerializer.Serialize(new { type = "response.failed", response = new { error = new { code = "policy_rejection", message = "rejected secret-5014 " + new string('z', 2000) } } });
        var fixture = new Fixture(websocket, _ => [payload]);
        var result = await fixture.RunAsync();
        var error = result.OfType<AssistantAgentMessage>().Single().ErrorMessage;
        error.ShouldNotBeNull();
        error.Length.ShouldBeLessThanOrEqualTo(512);
        error.ShouldNotContain("secret-5014");
        fixture.Attempts.ShouldBe(1);
    }

    private static string Failure(string code = "internal_error", string message = "internal server error") =>
        JsonSerializer.Serialize(new { type = "error", error = new { code, message }, metadata = "private-envelope" + new string('x', 2000) });

    private static string[] Partial(string kind) => kind switch
    {
        "text-end" => ["{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"m\",\"type\":\"message\"}}", "{\"type\":\"response.output_text.done\",\"item_id\":\"m\",\"text\":\"visible evidence\"}", "{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"m\",\"type\":\"message\"}}"],
        "thinking" => ["{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"r\",\"type\":\"reasoning\"}}", "{\"type\":\"response.reasoning_summary_text.delta\",\"item_id\":\"r\",\"delta\":\"visible evidence\"}"],
        "tool" => ["{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"t\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"echo\",\"arguments\":\"\"}}"],
        _ => ["{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"m\",\"type\":\"message\"}}", "{\"type\":\"response.output_text.delta\",\"item_id\":\"m\",\"delta\":\"visible evidence\"}"]
    };

    private static string[] Success() =>
    ["{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"m\",\"type\":\"message\"}}", "{\"type\":\"response.output_text.delta\",\"item_id\":\"m\",\"delta\":\"recovered\"}", "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}"];

    private sealed class Fixture
    {
        private readonly LlmClient _client;
        private readonly LlmModel _model;
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public int Attempts { get; private set; }
        public List<AgentEvent> Events { get; } = [];
        public List<string> Diagnostics { get; } = [];
        public List<IReadOnlyList<Message>> ProviderContexts { get; } = [];
        public ProviderRecoveryCoordinator Coordinator { get; }
        public ProviderSuspensionRegistry Suspensions { get; } = new();
        public ProviderRecoveryScope Scope { get; } = new("github-copilot", "");
        public Fixture(bool websocket, Func<int, string[]> frames)
        {
            string[] Next() => frames(++Attempts);
            var handler = new Handler(() => Next());
            var provider = new CopilotResponsesProvider(new HttpClient(handler), NullLogger<CopilotResponsesProvider>.Instance, new Socket(Next), new Redactor());
            var registry = new ApiProviderRegistry();
            registry.Register(provider);
            _client = new LlmClient(registry, new ModelRegistry());
            _model = CopilotModelDiscoveryProvider.MapToLlmModel(new CopilotModelInfo
            {
                Id = "gpt-5.6-sol", Name = "fixture", Capabilities = new CopilotModelCapabilities { Family = "gpt-5.6-sol" },
                SupportedEndpoints = websocket ? ["/responses", "ws:/responses"] : ["/responses"]
            }) ?? throw new InvalidOperationException("Fixture model missing");
            Coordinator = new ProviderRecoveryCoordinator(new ProviderRecoveryOptions(FailureThreshold: 10), () => _now,
                (delay, token) => { token.ThrowIfCancellationRequested(); _now += delay; return Task.CompletedTask; });
        }
        public async Task OpenCircuitAsync()
        {
            for (var i = 0; i < 10; i++)
            {
                var lease = await Coordinator.AcquireAsync(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(1), CancellationToken.None);
                lease.ReportTransientFailure(null);
            }
            Coordinator.GetSnapshot(Scope).State.ShouldBe(ProviderRecoveryState.Open);
            _now += TimeSpan.FromSeconds(3);
        }
        public void AdvanceToNextProbe()
        {
            var nextProbeAt = Coordinator.GetSnapshot(Scope).NextProbeAt;
            nextProbeAt.ShouldNotBeNull();
            _now = nextProbeAt.Value;
        }
        public Task<IReadOnlyList<AgentMessage>> RunAsync(CancellationToken ct = default, IReadOnlyList<IAgentTool>? tools = null)
        {
            var config = new AgentLoopConfig(_model, _client,
                (messages, _) =>
                {
                    IReadOnlyList<Message> converted = messages.Select(x => x switch
                    {
                        AgentUserMessage user => (Message)new BotNexus.Agent.Providers.Core.Models.UserMessage(new UserMessageContent(user.Content), 1),
                        AssistantAgentMessage assistant => new AssistantMessage(assistant.ContentBlocks ?? [], _model.Api, _model.Provider, _model.Id, Usage.Empty(), assistant.FinishReason, assistant.ErrorMessage, null, 1),
                        ToolResultAgentMessage tool => new ToolResultMessage(tool.ToolCallId, tool.ToolName, tool.Result.Content.Select(c => (ContentBlock)new TextContent(c.Value)).ToList(), tool.IsError, 1),
                        _ => throw new InvalidOperationException("Unexpected fixture message")
                    }).ToList();
                    ProviderContexts.Add(converted);
                    return Task.FromResult(converted);
                },
                null, (_, _) => Task.FromResult<ProviderExecutionOptions?>(new ProviderExecutionOptions { ApiKey = "synthetic-test-key" }),
                null, null, ToolExecutionMode.Sequential, null, null, new GenerationOptions(), MaxRetryDelayMs: 1, RecoveryCoordinator: Coordinator, DiagnosticObserver: Diagnostics.Add, SuspensionRegistry: Suspensions);
            return AgentLoopRunner.RunAsync([new AgentUserMessage("test")], new AgentContext(null, [], tools ?? []), config,
                evt => { Events.Add(evt); return Task.CompletedTask; }, ct);
        }
    }
    private sealed class Redactor : BotNexus.Gateway.Abstractions.Security.ISecretRedactor
    {
        public string Redact(string input) => input.Replace("secret-5014", "[REDACTED]", StringComparison.Ordinal);
        public string RedactForExternalDelivery(string input) => Redact(input);
    }
    private sealed class CountingTool : IAgentTool
    {
        public int Calls { get; private set; }
        public string Name => "echo";
        public string Label => "Echo";
        public Tool Definition => new(Name, "Counts calls", JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone());
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            Calls++;
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "completed evidence")]));
        }
    }
    private sealed class Handler(Func<string[]> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join("", next().Select(x => $"event: {JsonDocument.Parse(x).RootElement.GetProperty("type").GetString()}\ndata: {x}\n\n")), Encoding.UTF8, "text/event-stream")
        });
    }
    private sealed class Socket(Func<string[]> next) : ICopilotResponsesWebSocketTransport
    {
        private Queue<string> _frames = new();
        public CopilotResponsesCloseFrame? LastClose => null;
        public ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) { _frames = new(next()); return ValueTask.CompletedTask; }
        public ValueTask SendAsync(string payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_frames.TryDequeue(out var frame) ? frame : null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
