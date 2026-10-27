using System.Text.Json;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using UserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Agent.Core.Tests.Loop;

[Collection(ApiProviderRegistryCollection.Name)]
public sealed class AgentLoopRunnerToolResultBudgetTests
{
    private sealed class ReceiptTool : IAgentTool
    {
        public int Calls;
        public string Name => "opaque_receipt";
        public string Label => Name;
        public Tool Definition => new(Name, "receipt", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone());
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            Interlocked.Increment(ref Calls);
            if (toolCallId == "call-0") throw new InvalidOperationException("prior unresolved failure");
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, toolCallId)]));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConfiguredBudget_FinalizerPreservesGuardEvidenceAndRunIdentity(bool suppliedIdentity, bool cancelAtBoundary)
    {
        using var cancellation = new CancellationTokenSource();
        var api = $"budget-run-evidence-{suppliedIdentity}-{cancelAtBoundary}";
        var tool = new ReceiptTool();
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
            Interlocked.Increment(ref requests) == 1
                ? TestStreamFactory.CreateToolCallResponse(("call-0", tool.Name, new Dictionary<string, object?>()), ("call-1", tool.Name, new Dictionary<string, object?>()), ("call-2", tool.Name, new Dictionary<string, object?>()))
                : TestStreamFactory.CreateTextResponse("summary")));
        var expectedIdentity = suppliedIdentity ? AgentRunId.From("configured-budget-run") : (AgentRunId?)null;
        var snapshots = new List<IReadOnlyList<GuardObservation>>();
        var events = new List<AgentEvent>();
        await AgentLoopRunner.RunAsync([new UserMessage("work")], new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
            {
                MaxToolResults = 2,
                AgentRunId = expectedIdentity,
                GuardEvidenceObserver = guards => snapshots.Add(guards)
            }, evt =>
            {
                events.Add(evt);
                if (cancelAtBoundary && evt is TurnEndEvent { ToolResults.Count: > 0 }) cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token);
        requests.ShouldBe(cancelAtBoundary ? 1 : 2);
        tool.Calls.ShouldBe(3);
        var runIdentity = events.OfType<AgentStartEvent>().ShouldHaveSingleItem().AgentRunId.ShouldNotBeNull();
        if (expectedIdentity is { } supplied) runIdentity.ShouldBe(supplied);
        events.ShouldAllBe(evt => evt.AgentRunId == runIdentity);
        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        end.Completion.Status.ShouldBe(RunCompletionStatus.Parked);
        end.Completion.StopReason.ShouldBe(RunStopReason.ToolResultBudget);
        end.Completion.ToolResultBudget.ShouldBe(new ToolResultBudgetEvidence(2, 3, 1));
        var guard = end.Completion.GuardObservations.ShouldHaveSingleItem();
        guard.GuardKind.ShouldBe("absolute-tool-result-limit");
        guard.TotalResults.ShouldBe(3L);
        guard.StopThreshold.ShouldBe(2);
        guard.AbsoluteLimitReached.ShouldBeTrue();
        guard.Disposition.ShouldBe("terminal");
        Guid.TryParseExact(guard.EvidenceReferences.ShouldHaveSingleItem(), "N", out _).ShouldBeTrue();
        snapshots.ShouldNotBeEmpty();
        snapshots.Last().ShouldHaveSingleItem().ShouldBe(guard);
        end.Messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(3);
        end.Messages.OfType<ToolResultAgentMessage>().Single(m => m.ToolCallId == "call-0").IsError.ShouldBeTrue();
    }

    [Theory]
    [InlineData("done")]
    [InlineData("error")]
    [InlineData("interrupted")]
    [InlineData("result-only")]
    public async Task BudgetSummary_UnsolicitedCallsNeverEscapeAsDispatchEvidence(string outcome)
    {
        var api = "budget-unsolicited-" + outcome;
        var tool = new ReceiptTool();
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
        {
            if (++requests == 1)
                return TestStreamFactory.CreateToolCallResponse(("call-0", tool.Name, new Dictionary<string, object?>()));
            var call = new ToolCallContent("forbidden", tool.Name, new Dictionary<string, object?>());
            var message = new AssistantMessage([new TextContent("retained summary"), call], api, "test-provider", "test-model",
                new Usage { Input = 17, Output = 9, TotalTokens = 26 }, StopReason.ToolUse, null, null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var stream = new LlmStream();
            if (outcome != "result-only")
            {
                stream.Push(new StartEvent(message));
                stream.Push(new TextDeltaEvent(0, "retained summary", message));
                stream.Push(new ToolCallStartEvent(1, message));
                stream.Push(new ToolCallDeltaEvent(1, "{}", message));
                stream.Push(new ToolCallEndEvent(1, call, message));
                if (outcome == "done") stream.Push(new DoneEvent(StopReason.ToolUse, message));
                if (outcome == "error") stream.Push(new ErrorEvent(StopReason.Error, message));
            }
            if (outcome == "interrupted") stream.EndFaulted(new InvalidOperationException("summary interrupted"));
            else stream.End(message);
            return stream;
        }));
        var events = new List<AgentEvent>();
        await AgentLoopRunner.RunAsync([new UserMessage("work")], new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with { MaxToolResults = 1 },
            evt => { events.Add(evt); return Task.CompletedTask; }, CancellationToken.None);
        requests.ShouldBe(2);
        tool.Calls.ShouldBe(1);
        events.OfType<ToolExecutionStartEvent>().ShouldHaveSingleItem().ToolCallId.ShouldBe("call-0");
        events.OfType<ToolExecutionEndEvent>().ShouldHaveSingleItem().ToolCallId.ShouldBe("call-0");
        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        end.Completion.Status.ShouldBe(RunCompletionStatus.Parked);
        end.Completion.StopReason.ShouldBe(RunStopReason.ToolResultBudget);
        end.Completion.ToolResultBudget.ShouldBe(new ToolResultBudgetEvidence(1, 1, 0));
        end.Messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem().IsError.ShouldBeTrue();
        var summary = end.Messages.OfType<AssistantAgentMessage>().Last();
        summary.Content.ShouldBe("retained summary");
        summary.Usage.ShouldNotBeNull().InputTokens.ShouldBe(17);
        summary.Usage.ShouldNotBeNull().OutputTokens.ShouldBe(9);
        end.Metrics.ShouldNotBeNull().InputTokens.ShouldBe(27);
        end.Metrics.ShouldNotBeNull().OutputTokens.ShouldBe(14);
        // Pin the entire public event surface, not just the fact that no tool was executed.
        foreach (var snapshot in events.Select(evt => evt switch
        {
            MessageStartEvent { Message: AssistantAgentMessage message } => message,
            MessageUpdateEvent update => update.Message,
            MessageEndEvent { Message: AssistantAgentMessage message } => message,
            TurnEndEvent turn => turn.Message,
            _ => null
        }).OfType<AssistantAgentMessage>().Append(summary))
        {
            (snapshot.ToolCalls ?? []).ShouldAllBe(call => call.Id == "call-0");
            (snapshot.ContentBlocks ?? []).OfType<ToolCallContent>().ShouldAllBe(call => call.Id == "call-0");
        }
        events.OfType<MessageUpdateEvent>().ShouldAllBe(update => update.ToolCallId != "forbidden");
        var lastEnd = events.OfType<MessageEndEvent>().Last(e => e.Message is AssistantAgentMessage);
        lastEnd.Message.ShouldBe(summary);
        events.OfType<TurnEndEvent>().Last().Message.ShouldBe(summary);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000001)]
    public async Task InvalidBudget_IsRejectedBeforeProviderOrToolExecution(int limit)
    {
        var config = TestHelpers.CreateTestConfig() with { MaxToolResults = limit };
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => AgentLoopRunner.RunAsync(
            [new UserMessage("work")], new AgentContext(null, [], []), config, _ => Task.CompletedTask, CancellationToken.None));
        var options = TestHelpers.CreateTestOptions(null, config.Model) with { MaxToolResults = limit };
        Should.Throw<ArgumentOutOfRangeException>(() => new BotNexus.Agent.Core.Agent(options));
    }

    [Fact]
    public async Task AgentOptions_ConfiguredBudgetStopsAfterActualExecutedResults()
    {
        const string api = "configured-budget-options";
        var tool = new ReceiptTool();
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
            Interlocked.Increment(ref requests) == 1
                ? TestStreamFactory.CreateToolCallResponse(("one", tool.Name, new Dictionary<string, object?>()), ("two", tool.Name, new Dictionary<string, object?>()))
                : TestStreamFactory.CreateTextResponse("summary")));
        var model = TestHelpers.CreateTestModel(api);
        var initial = new AgentInitialState(Model: model, Tools: [tool], Messages: []);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(initial, model) with { MaxToolResults = 2 });
        await agent.PromptAsync("work");
        requests.ShouldBe(2);
        tool.Calls.ShouldBe(2);
        agent.State.LastCompletion.ShouldNotBeNull().ToolResultBudget.ShouldBe(new ToolResultBudgetEvidence(2, 2, 0));
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("leak")]
    [InlineData("error")]
    [InlineData("refusal")]
    [InlineData("cancel")]
    [InlineData("caller-cancel")]
    [InlineData("timeout")]
    [InlineData("markup")]
    public async Task AbsoluteBoundary_OneToolFreeSummaryPreservesIncompleteBatch(string summary)
    {
        using var cancellation = new CancellationTokenSource();
        var api = "budget-summary-" + summary;
        var tool = new ReceiptTool();
        var requests = 0;
        var summaryToolCount = -1;
        SimpleStreamOptions? summaryOptions = null;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, options) =>
        {
            var request = Interlocked.Increment(ref requests);
            if (request == 1)
                return TestStreamFactory.CreateToolCallResponse(Enumerable.Range(0, 130).Select(i => ($"call-{i}", tool.Name, new Dictionary<string, object?>())).ToArray());
            summaryToolCount = context.Tools?.Count ?? 0;
            summaryOptions = options;
            if (summary == "caller-cancel") cancellation.Cancel();
            return summary switch
            {
                "leak" => TestStreamFactory.CreateToolCallResponse(("forbidden", tool.Name, new Dictionary<string, object?>())),
                "error" => TestStreamFactory.CreateErrorResponse("summary failed"),
                "refusal" => TestStreamFactory.CreateTextResponse("refused", StopReason.Sensitive),
                "cancel" => throw new OperationCanceledException("summary cancelled"),
                "timeout" => throw new TimeoutException("summary timed out"),
                "markup" => TestStreamFactory.CreateTextResponse("<invoke name=\"opaque_receipt\"><parameter name=\"path\">forbidden</parameter></invoke>"),
                _ => TestStreamFactory.CreateTextResponse("Everything is complete.")
            };
        }));
        var events = new List<AgentEvent>();
        var messages = await AgentLoopRunner.RunAsync([new UserMessage("do work")], new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with { ToolExecutionMode = ToolExecutionMode.Parallel },
            evt => { events.Add(evt); return Task.CompletedTask; }, cancellation.Token);
        requests.ShouldBe(2);
        summaryToolCount.ShouldBe(0);
        var generation = summaryOptions.ShouldNotBeNull();
        generation.ToolChoiceMode.ShouldBe(ToolChoice.None);
        generation.MaxTokens.ShouldNotBeNull().ShouldBeInRange(1, 1024);
        generation.CancellationToken.CanBeCanceled.ShouldBeTrue();
        tool.Calls.ShouldBe(130);
        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(130);
        messages.OfType<ToolResultAgentMessage>().Single(m => m.ToolCallId == "call-0").IsError.ShouldBeTrue();
        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        end.Completion.Status.ShouldBe(RunCompletionStatus.Parked);
        end.Completion.OpenItemIds.ShouldContain("tool-result-budget");
        end.Completion.StopReason.ShouldBe(RunStopReason.ToolResultBudget);
        end.Completion.ToolResultBudget.ShouldBe(new ToolResultBudgetEvidence(128, 130, 2));
        end.Completion.Detail.ShouldNotBeNull().ShouldContain("130");
        end.Completion.Detail.ShouldNotBeNull().ShouldContain("128");
        end.Messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(130);
        var metrics = end.Metrics.ShouldNotBeNull();
        metrics.ToolCallCount.ShouldBe(130);
        metrics.InputTokens.ShouldBeGreaterThanOrEqualTo(10);
        metrics.OutputTokens.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Theory]
    [InlineData("followup")]
    [InlineData("completion")]
    [InlineData("compaction")]
    public async Task AbsoluteBudget_IsMonotonicAcrossContinuationsAndResetsForNewRun(string continuation)
    {
        var api = "budget-continuation-" + continuation;
        var tool = new ReceiptTool();
        var requests = 0;
        var followups = 0;
        var evaluations = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
        {
            var n = Interlocked.Increment(ref requests);
            if ((context.Tools?.Count ?? 0) == 0) return TestStreamFactory.CreateTextResponse("summary");
            if (n == 2 && continuation != "compaction") return TestStreamFactory.CreateTextResponse("progress");
            return TestStreamFactory.CreateToolCallResponse(Enumerable.Range(0, 64).Select(i => ($"batch-{n}-{i}", tool.Name, new Dictionary<string, object?>())).ToArray());
        }));
        var context = new AgentContext(null, [], [tool]);
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            FollowUpMessageProvider = _ => Task.FromResult<IReadOnlyList<AgentMessage>>(continuation == "followup" && Interlocked.Increment(ref followups) == 1 ? [new UserMessage("more work")] : []),
            RunCompletionPolicy = _ => Task.FromResult(continuation == "completion" && Interlocked.Increment(ref evaluations) == 1 ? RunCompletionDecision.Continue(["unresolved"], "still open") : RunCompletionDecision.Completed),
            ContextCompactionService = continuation == "compaction" ? _ => Task.FromResult<AgentContext?>(context) : null
        };
        for (var run = 1; run <= 2; run++)
        {
            var events = new List<AgentEvent>();
            var messages = await AgentLoopRunner.RunAsync([new UserMessage("work")], context, config,
                evt => { events.Add(evt); return Task.CompletedTask; }, CancellationToken.None);
            tool.Calls.ShouldBe(128 * run);
            messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(128);
            messages.OfType<AssistantAgentMessage>().Last().Content.ShouldBe("summary");
            events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Parked);
        }
    }


    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Agent_CompletedBatchBoundaryCancellationPreservesBudgetOnlyAtHardLimit(bool cancelCaller, bool belowLimit)
    {
        using var cancellation = new CancellationTokenSource();
        var api = $"budget-boundary-{cancelCaller}-{belowLimit}";
        var tool = new ReceiptTool();
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
            Interlocked.Increment(ref requests) == 1
                ? TestStreamFactory.CreateToolCallResponse(("call-0", tool.Name, new Dictionary<string, object?>()))
                : TestStreamFactory.CreateTextResponse("summary")));
        var model = TestHelpers.CreateTestModel(api);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(new AgentInitialState(Model: model, Tools: [tool], Messages: []), model) with { MaxToolResults = belowLimit ? 2 : 1 });
        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe((evt, _) =>
        {
            events.Add(evt);
            if (evt is TurnEndEvent { ToolResults.Count: > 0 })
            {
                if (cancelCaller) cancellation.Cancel();
                throw new OperationCanceledException("completed batch observer cancelled");
            }
            return Task.CompletedTask;
        });
        await agent.PromptAsync("work", cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
        events.OfType<TurnEndEvent>().Count(e => e.ToolResults.Count > 0).ShouldBe(1);
        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        var completion = agent.State.LastCompletion.ShouldNotBeNull();
        if (belowLimit)
        {
            completion.Status.ShouldBe(cancelCaller ? RunCompletionStatus.Cancelled : RunCompletionStatus.Failed);
            completion.ToolResultBudget.ShouldBeNull();
            requests.ShouldBe(1);
        }
        else
        {
            completion.Status.ShouldBe(RunCompletionStatus.Parked);
            completion.StopReason.ShouldBe(RunStopReason.ToolResultBudget);
            completion.ToolResultBudget.ShouldBe(new ToolResultBudgetEvidence(1, 1, 0));
            end.Messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem().IsError.ShouldBeTrue();
            end.Metrics.ShouldNotBeNull().ToolCallCount.ShouldBe(1);
            end.Metrics.ShouldNotBeNull().InputTokens.ShouldBe(cancelCaller ? 10 : 20);
            requests.ShouldBe(cancelCaller ? 1 : 2);
            agent.State.Messages.OfType<AssistantAgentMessage>().ShouldNotContain(m => m.ErrorMessage == "Operation aborted" || m.ErrorMessage == "completed batch observer cancelled");
        }
        tool.Calls.ShouldBe(1);
        agent.State.Messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem().IsError.ShouldBeTrue();
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("turn-end", false)]
    [InlineData("audit", false)]
    [InlineData("audit", true)]
    public async Task Agent_BudgetBoundaryAuditPrecedesSummaryAndCannotOverwriteParked(string failingEvent, bool cancelCaller)
    {
        using var cancellation = new CancellationTokenSource();
        var api = $"budget-boundary-audit-{failingEvent}-{cancelCaller}";
        var tool = new ReceiptTool();
        var requests = 0;
        var message = new AssistantMessage(
            [new TextContent("I filed issue #1234."), new ToolCallContent("call-0", tool.Name, new Dictionary<string, object?>())],
            api, "test-provider", "test-model", new Usage { Input = 10, Output = 5, TotalTokens = 15 },
            StopReason.ToolUse, null, "boundary", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var stream = new LlmStream();
        stream.Push(new StartEvent(message));
        stream.Push(new DoneEvent(StopReason.ToolUse, message));
        stream.End(message);
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
            Interlocked.Increment(ref requests) == 1 ? stream : TestStreamFactory.CreateTextResponse("summary")));
        var model = TestHelpers.CreateTestModel(api);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(new AgentInitialState(Model: model, Tools: [tool], Messages: []), model) with
        {
            MaxToolResults = 1,
            ClaimAudit = BotNexus.Agent.Core.Diagnostics.ClaimAuditOptions.CreateDefault()
        });
        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe((evt, _) =>
        {
            events.Add(evt);
            if ((failingEvent == "turn-end" && evt is TurnEndEvent { ToolResults.Count: > 0 }) ||
                (failingEvent == "audit" && evt is ClaimAuditEvent))
            {
                if (cancelCaller) cancellation.Cancel();
                throw new OperationCanceledException("boundary audit observer cancelled");
            }
            return Task.CompletedTask;
        });
        await agent.PromptAsync("work", cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
        events.OfType<ClaimAuditEvent>().ShouldHaveSingleItem();
        var boundary = events.FindIndex(e => e is TurnEndEvent { ToolResults.Count: > 0 });
        var audit = events.FindIndex(e => e is ClaimAuditEvent);
        boundary.ShouldBeGreaterThanOrEqualTo(0);
        audit.ShouldBeGreaterThan(boundary);
        if (!cancelCaller)
        {
            events.OfType<TurnEndEvent>().Count().ShouldBe(2);
            audit.ShouldBeLessThan(events.FindIndex(e => e is MessageStartEvent { Message: UserMessage user } && user.Content.StartsWith("[Tool result budget]", StringComparison.Ordinal)));
        }
        requests.ShouldBe(cancelCaller ? 1 : 2);
        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        end.Completion.Status.ShouldBe(RunCompletionStatus.Parked);
        end.Completion.StopReason.ShouldBe(RunStopReason.ToolResultBudget);
        agent.State.LastCompletion.ShouldNotBeNull().ToolResultBudget.ShouldBe(new ToolResultBudgetEvidence(1, 1, 0));
        end.Messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem().IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Agent_CompletedBatchBoundaryStallIsBoundedAndDoesNotDuplicateEvents()
    {
        const string api = "budget-boundary-stall";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new ReceiptTool();
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
            Interlocked.Increment(ref requests) == 1
                ? TestStreamFactory.CreateToolCallResponse(("call-0", tool.Name, new Dictionary<string, object?>()))
                : TestStreamFactory.CreateTextResponse("summary")));
        var model = TestHelpers.CreateTestModel(api);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(new AgentInitialState(Model: model, Tools: [tool], Messages: []), model) with { MaxToolResults = 1 });
        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe((evt, _) =>
        {
            events.Add(evt);
            return evt is TurnEndEvent { ToolResults.Count: > 0 } ? release.Task : Task.CompletedTask;
        });
        try
        {
            await agent.PromptAsync("work").WaitAsync(TimeSpan.FromSeconds(40));
            requests.ShouldBe(1);
            events.OfType<TurnEndEvent>().ShouldHaveSingleItem().ToolResults.ShouldHaveSingleItem().IsError.ShouldBeTrue();
            var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
            end.Completion.Status.ShouldBe(RunCompletionStatus.Parked);
            end.Completion.StopReason.ShouldBe(RunStopReason.ToolResultBudget);
            end.Completion.ToolResultBudget.ShouldBe(new ToolResultBudgetEvidence(1, 1, 0));
            end.Metrics.ShouldNotBeNull().ToolCallCount.ShouldBe(1);
            end.Metrics.ShouldNotBeNull().InputTokens.ShouldBe(10);
            var snapshot = agent.State.Messages.ToArray();
            release.TrySetResult();
            agent.State.Messages.ShouldBe(snapshot);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_BudgetTerminalListenerCancellationCannotOverwriteParked(bool cancelCaller)
    {
        using var cancellation = new CancellationTokenSource();
        const string api = "budget-terminal-listener";
        var tool = new ReceiptTool();
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
            Interlocked.Increment(ref requests) == 1
                ? TestStreamFactory.CreateToolCallResponse(("call-0", tool.Name, new Dictionary<string, object?>()))
                : TestStreamFactory.CreateTextResponse("summary")));
        var model = TestHelpers.CreateTestModel(api);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(new AgentInitialState(Model: model, Tools: [tool], Messages: []), model) with { MaxToolResults = 1 });
        var ends = new List<AgentEndEvent>();
        using var subscription = agent.Subscribe((evt, _) =>
        {
            if (evt is AgentEndEvent end)
            {
                ends.Add(end);
                if (cancelCaller) cancellation.Cancel();
                throw new OperationCanceledException("terminal observer cancelled");
            }
            return Task.CompletedTask;
        });
        await agent.PromptAsync("work", cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
        ends.ShouldHaveSingleItem().Completion.StopReason.ShouldBe(RunStopReason.ToolResultBudget);
        agent.State.LastCompletion.ShouldNotBeNull().Status.ShouldBe(RunCompletionStatus.Parked);
        agent.State.Messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem().IsError.ShouldBeTrue();
        agent.State.Messages.OfType<AssistantAgentMessage>().ShouldNotContain(m => m.ErrorMessage == "Operation aborted" || m.ErrorMessage == "terminal observer cancelled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_InterruptedSummaryPersistsPartialAndUsageWithoutLateMutation(bool blockListener)
    {
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = "budget-partial-" + blockListener;
        var tool = new ReceiptTool();
        var requests = 0;
        var partial = new AssistantMessage([new TextContent("partial summary")], api, "test-provider", "test-model",
            new Usage { Input = 17, Output = 9, TotalTokens = 26 }, StopReason.Stop, null, "partial", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var stream = new LlmStream();
        stream.Push(new StartEvent(partial));
        stream.Push(new TextDeltaEvent(0, "partial summary", partial));
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
            Interlocked.Increment(ref requests) == 1
                ? TestStreamFactory.CreateToolCallResponse(("call-0", tool.Name, new Dictionary<string, object?>())) : stream));
        var model = TestHelpers.CreateTestModel(api);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(new AgentInitialState(Model: model, Tools: [tool], Messages: []), model) with { MaxToolResults = 1 });
        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe((evt, _) =>
        {
            events.Add(evt);
            if (evt is MessageUpdateEvent update && update.Message.Content == "partial summary")
            {
                cancellation.Cancel();
                if (blockListener) return release.Task;
            }
            return Task.CompletedTask;
        });
        try
        {
            await agent.PromptAsync("work", cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            var saved = agent.State.Messages.OfType<AssistantAgentMessage>().Single(m => m.Content == "partial summary");
            saved.FinishReason.ShouldBe(StopReason.Aborted);
            saved.Usage.ShouldNotBeNull().InputTokens.ShouldBe(17);
            agent.State.StreamingMessage.ShouldBeNull();
            events.OfType<MessageEndEvent>().Count(e => e.Message is AssistantAgentMessage m && m.Content == "partial summary").ShouldBe(1);
            var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
            end.Completion.Status.ShouldBe(RunCompletionStatus.Parked);
            end.Metrics.ShouldNotBeNull().InputTokens.ShouldBe(27);
            end.Metrics.ShouldNotBeNull().OutputTokens.ShouldBe(14);
            var snapshot = agent.State.Messages.ToArray();
            release.TrySetResult();
            stream.Push(new DoneEvent(StopReason.Stop, partial with { Content = [new TextContent("late summary")] }));
            agent.State.Messages.ShouldBe(snapshot);
        }
        finally { release.TrySetResult(); stream.EndCancelled(cancellation.Token); }
    }

    [Fact]
    public async Task BudgetDeadline_BoundsUncooperativeProducerAndTerminalListener()
    {
        const string api = "budget-uncooperative-producer";
        var tool = new ReceiptTool();
        var requests = 0;
        SimpleStreamOptions? summaryOptions = null;
        var stream = new LlmStream();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, options) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
                return TestStreamFactory.CreateToolCallResponse(("call-0", tool.Name, new Dictionary<string, object?>()));
            summaryOptions = options;
            return stream; // Never emits or completes, and ignores the generation token.
        }));
        var events = new List<AgentEvent>();
        try
        {
            var messages = await AgentLoopRunner.RunAsync([new UserMessage("work")], new AgentContext(null, [], [tool]),
                TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with { MaxToolResults = 1 },
                evt =>
                {
                    events.Add(evt);
                    return evt is AgentEndEvent ? release.Task : Task.CompletedTask;
                }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(40));
            summaryOptions.ShouldNotBeNull().CancellationToken.IsCancellationRequested.ShouldBeTrue();
            requests.ShouldBe(2);
            tool.Calls.ShouldBe(1);
            events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Parked);
            messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem().IsError.ShouldBeTrue();
            var snapshot = messages.ToArray();
            release.TrySetResult();
            stream.EndCancelled(summaryOptions.ShouldNotBeNull().CancellationToken);
            messages.ShouldBe(snapshot);
        }
        finally { release.TrySetResult(); stream.EndCancelled(CancellationToken.None); }
    }

    [Fact]
    public async Task BudgetTerminalTranscriptUsesRunLedgerAfterNonzeroHistoryCompaction()
    {
        const string api = "budget-compacted-ledger";
        var tool = new ReceiptTool();
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
            (context.Tools?.Count ?? 0) == 0 ? TestStreamFactory.CreateTextResponse("summary") :
            TestStreamFactory.CreateToolCallResponse(($"call-{Interlocked.Increment(ref requests) - 1}", tool.Name, new Dictionary<string, object?>()))));
        var history = Enumerable.Range(0, 10).Select(i => (AgentMessage)new UserMessage($"history-{i}")).ToArray();
        var compactions = 0;
        var events = new List<AgentEvent>();
        var messages = await AgentLoopRunner.RunAsync([new UserMessage("work")], new AgentContext(null, history, [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
            {
                MaxToolResults = 2,
                ContextCompactionService = _ => Task.FromResult<AgentContext?>(Interlocked.Increment(ref compactions) == 2 ? new AgentContext(null, [new UserMessage("compacted")], [tool]) : null)
            }, evt => { events.Add(evt); return Task.CompletedTask; }, CancellationToken.None);
        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        end.Messages.ShouldBe(messages);
        end.Messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(2);
        end.Messages.OfType<ToolResultAgentMessage>().Single(m => m.ToolCallId == "call-0").IsError.ShouldBeTrue();
        end.Messages.OfType<UserMessage>().ShouldNotContain(m => m.Content.StartsWith("history-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbsoluteBoundary_ExternalSteeringCannotDispatchAnotherBatch()
    {
        const string api = "budget-steering";
        var tool = new ReceiptTool();
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
            Interlocked.Increment(ref requests) <= 2
                ? TestStreamFactory.CreateToolCallResponse(Enumerable.Range(0, 128).Select(i => ($"call-{i}", tool.Name, new Dictionary<string, object?>())).ToArray())
                : TestStreamFactory.CreateTextResponse("Done.")));
        var events = new List<AgentEvent>();
        var polls = 0;
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            SteeringMessageProvider = _ => Task.FromResult<IReadOnlyList<AgentMessage>>(Interlocked.Increment(ref polls) == 2 ? [new UserMessage("continue anyway")] : [])
        };
        var messages = await AgentLoopRunner.RunAsync([new UserMessage("work")], new AgentContext(null, [], [tool]), config,
            evt => { events.Add(evt); return Task.CompletedTask; }, CancellationToken.None);
        tool.Calls.ShouldBe(128);
        requests.ShouldBe(2);
        messages.OfType<UserMessage>().ShouldContain(m => m.Content == "continue anyway");
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Parked);
    }
}
