using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>Real handle regressions for #4746: last-turn cache measurements survive both blocking projections.</summary>
public sealed class InProcessAgentHandleUsageTests
{
    private static readonly Usage FirstUsage = new() { Input = 11, Output = 7, CacheRead = 101, CacheWrite = 23 };
    private static readonly Usage LastUsage = new() { Input = 17, Output = 13, CacheRead = 211, CacheWrite = 31 };

    [Fact]
    public async Task PromptAsync_MeasuredResponse_PreservesAllFourLastTurnFields()
    {
        var provider = new ScriptedProvider(new Turn("answer", LastUsage));
        var (_, handle) = CreateHandle(provider);
        await using var lifetime = handle;

        var response = await handle.PromptAsync("hello");

        response.Content.ShouldBe("answer");
        response.TurnCount.ShouldBe(1);
        response.Completion.ShouldNotBeNull().Status.ShouldBe("Completed");
        response.TerminalError.ShouldBeNull();
        response.ToolCalls.ShouldBeEmpty();
        AssertAggregate(response, 259, 13);
        AssertUsage(response.Usage, 17, 13, 211, 31);
    }

    [Fact]
    public async Task PromptAsync_MultipleTurns_KeepsLastUsageSeparateFromCacheInclusiveRunUsage()
    {
        var provider = new ScriptedProvider(
            new Turn("checking", FirstUsage, StopReason.ToolUse, Call: true),
            new Turn("answer", LastUsage));
        var (_, handle) = CreateHandle(provider);
        await using var lifetime = handle;

        var response = await handle.PromptAsync("hello");

        provider.Calls.ShouldBe(2);
        response.TurnCount.ShouldBe(2);
        AssertToolTimelineAndAudit(response);
        // (11 + 101 + 23) + (17 + 211 + 31); cache is counted once, not also exposed on RunUsage.
        AssertAggregate(response, 394, 20);
        AssertUsage(response.Usage, 17, 13, 211, 31);
    }

    [Fact]
    public async Task PromptAsync_InterruptedAfterMeasuredProviderTurn_PreservesPartialDispositionTimelineAndAudit()
    {
        var provider = new ScriptedProvider(
            new Turn("checking", FirstUsage, StopReason.ToolUse, Call: true),
            new Turn("partial answer", LastUsage, StopReason.Aborted, "provider aborted"));
        var (agent, handle) = CreateHandle(provider);
        await using var lifetime = handle;
        using var cancellation = new CancellationTokenSource();
        using var subscription = agent.Subscribe((evt, _) =>
        {
            // State processes AgentEnd before subscribers. Cancel only after the measured terminal
            // assistant and its cancellation disposition are settled; no synthetic abort replaces it.
            if (evt is AgentEndEvent)
                cancellation.Cancel();
            return Task.CompletedTask;
        });

        var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() =>
            handle.PromptWhenAvailableAsync(new AgentUserMessage("hello"), () => Task.CompletedTask, cancellation.Token));

        interrupted.CancellationToken.ShouldBe(cancellation.Token);
        var response = interrupted.PartialResponse;
        response.Content.ShouldBe("partial answer");
        response.TerminalError.ShouldBeNull("an aborted turn is cancellation, not a provider fault");
        var completion = response.Completion.ShouldNotBeNull();
        completion.Status.ShouldBe("Cancelled");
        completion.StopReason.ShouldBe("Cancellation");
        completion.Detail.ShouldBe("provider aborted");
        response.TurnCount.ShouldBe(2);
        provider.Calls.ShouldBe(2);
        AssertToolTimelineAndAudit(response);
        AssertAggregate(response, 394, 20);
        AssertUsage(response.Usage, 17, 13, 211, 31);
    }

    [Fact]
    public async Task PromptAsync_ProviderReportedZero_RemainsMeasuredWithAbsentCacheFields()
    {
        var (_, handle) = CreateHandle(new ScriptedProvider(new Turn("free", Usage.Empty())));
        await using var lifetime = handle;

        var response = await handle.PromptAsync("hello");

        AssertUsage(response.Usage, 0, 0, null, null);
        AssertAggregate(response, 0, 0);
    }

    [Fact]
    public async Task PromptAsync_CancelledBeforeProviderReportsUsage_DoesNotInventZeroMeasurements()
    {
        var provider = new ScriptedProvider(new Turn("unused", LastUsage));
        var (_, handle) = CreateHandle(provider);
        await using var lifetime = handle;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() =>
            handle.PromptAsync(new AgentUserMessage("hello"), cancellation.Token));

        provider.Calls.ShouldBe(0);
        interrupted.PartialResponse.Usage.ShouldBeNull();
        interrupted.PartialResponse.RunUsage.ShouldBeNull();
    }

    [Fact]
    public async Task StreamAsync_MeasuredResponse_StillPreservesAllFourFields()
    {
        var (_, handle) = CreateHandle(new ScriptedProvider(new Turn("answer", LastUsage)));
        await using var lifetime = handle;
        var events = new List<AgentStreamEvent>();

        await foreach (var evt in handle.StreamAsync("hello"))
            events.Add(evt);

        events.Where(evt => evt.Type == AgentStreamEventType.MessageStart).ShouldHaveSingleItem();
        var end = events.Where(evt => evt.Type == AgentStreamEventType.MessageEnd).ShouldHaveSingleItem();
        AssertUsage(end.Usage, 17, 13, 211, 31);
    }

    private static void AssertUsage(AgentResponseUsage? usage, int input, int output, int? read, int? write)
    {
        var measured = usage.ShouldNotBeNull();
        measured.InputTokens.ShouldBe(input);
        measured.OutputTokens.ShouldBe(output);
        measured.CacheRead.ShouldBe(read);
        measured.CacheWrite.ShouldBe(write);
    }

    private static void AssertAggregate(AgentResponse response, int input, int output)
        => AssertUsage(response.RunUsage, input, output, null, null);

    private static void AssertToolTimelineAndAudit(AgentResponse response)
    {
        var call = response.ToolCalls.ShouldHaveSingleItem();
        call.ToolCallId.ShouldBe("call-4746");
        call.ToolName.ShouldBe("read");
        call.Arguments.ShouldNotBeNull().ShouldContain("sample.txt");
        call.ResultContent.ShouldBe("file body");
        call.IsError.ShouldBeFalse();
        call.IsIncomplete.ShouldBeFalse();
        var sink = DefaultToolAuditSink.Instance;
        var row = sink.ProjectBlockingRun(sink.CaptureBlockingRun(response)).ShouldHaveSingleItem();
        row.Kind.ShouldBe(MessageKind.ToolResult);
        row.ToolCallId.ShouldBe("call-4746");
        row.ToolName.ShouldBe("read");
        row.ToolArgs.ShouldNotBeNull().ShouldContain("sample.txt");
        row.Content.ShouldContain("file body");
        row.ToolIsError.ShouldBeFalse();
    }

    private static (BotNexus.Agent.Core.Agent Agent, InProcessAgentHandle Handle) CreateHandle(ScriptedProvider provider)
    {
        var model = new LlmModel("test-model", "test-model", "test-api", "test-provider", "http://localhost",
            false, ["text"], new ModelCost(0, 0, 0, 0), 8192, 1024);
        var models = new ModelRegistry();
        models.Register(model.Provider, model);
        var providers = new ApiProviderRegistry();
        providers.Register(provider);
        var session = new GatewaySession
        {
            AgentId = AgentId.From("agent-4746"), SessionId = SessionId.From("session-4746"),
            ConversationId = ConversationId.From("conversation-4746")
        };
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.AppendEntriesAsync(session.SessionId, It.IsAny<IReadOnlyList<SessionEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId _, IReadOnlyList<SessionEntry> rows, CancellationToken _) =>
            {
                session.AddEntries(rows);
                return new SessionAppendMutationResult(SessionMutationOutcome.Applied, rows.Count);
            });
        var audit = new ToolAuditWriteAhead(store.Object, DefaultToolAuditSink.Instance, new SecretRedactor(),
            session.AgentId, session.SessionId, NullLogger.Instance);
        IAgentTool[] tools = [new ReadTool()];
        var options = new AgentOptions(
            new AgentInitialState(SystemPrompt: "test", Model: model, Tools: tools), model,
            new LlmClient(providers, models), null, null,
            (_, _) => Task.FromResult<ProviderExecutionOptions?>(null), null, null,
            ToolExecutionMode.Parallel, null,
            (ctx, _) =>
            {
                audit.RecordCompleted(ctx.ToolCallRequest.Id);
                return Task.FromResult<BotNexus.Agent.Core.ExtensionPoints.ToolResults.ToolResultTransformResult?>(null);
            },
            new GenerationOptions(), QueueMode.All, QueueMode.All, SessionId: session.SessionId.Value,
            ToolAuditGate: async (ctx, ct) =>
            {
                await audit.PersistStartAsync(ctx.ToolCallRequest.Id, ctx.ToolCallRequest.Name, ctx.ValidatedArgs, ct);
                return null;
            });
        var agent = new BotNexus.Agent.Core.Agent(options);
        return (agent, new InProcessAgentHandle(agent, session.AgentId, session.SessionId,
            NullLogger.Instance, tools: tools, toolWriteAhead: audit));
    }

    private sealed record Turn(string Text, Usage Usage, StopReason Reason = StopReason.Stop,
        string? Error = null, bool Call = false);

    private sealed class ScriptedProvider(params Turn[] turns) : IApiProvider
    {
        public string Api => "test-api";
        public int Calls { get; private set; }
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context);
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var turn = turns[Calls++];
            var content = new List<ContentBlock> { new TextContent(turn.Text) };
            if (turn.Call)
                content.Add(new ToolCallContent("call-4746", "read", new Dictionary<string, object?> { ["path"] = "sample.txt" }));
            var message = new AssistantMessage(content, model.Api, model.Provider, model.Id,
                turn.Usage, turn.Reason, turn.Error, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var stream = new LlmStream();
            stream.Push(new StartEvent(message with { Content = [] }));
            stream.Push(new TextDeltaEvent(0, turn.Text, message));
            stream.Push(new DoneEvent(turn.Reason, message));
            return stream;
        }
    }

    private sealed class ReadTool : IAgentTool
    {
        public string Name => "read";
        public string Label => "read";
        public Tool Definition => new(Name, "read test content",
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }));
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "file body")]));
    }
}
