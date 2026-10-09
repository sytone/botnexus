using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Streaming;
using Moq;
using static BotNexus.Gateway.Tests.RunCorrelation4796Fixture;
using CoreUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Tests;

public sealed class AgentRunCorrelation4796Tests
{
    [Fact]
    public async Task Handle_CancelledTool_ResponseRetainsUnknownInvocationAndRunIdentity()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider((call, _) => call == 1 ? Calls(1) : TextResponse());
        var tool = new ProbeTool(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        });
        var (agent, handle) = Create(provider, [tool]);
        await using var owned = handle;
        AgentEndEvent? terminal = null;
        using var subscription = agent.Subscribe((evt, _) =>
        {
            if (evt is AgentEndEvent end) terminal = end;
            return Task.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();
        var task = handle.PromptAsync("go", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var response = await task.WaitAsync(TimeSpan.FromSeconds(10));
        var end = terminal.ShouldNotBeNull();
        Id(response).ShouldBe(Id(end));
        response.Completion.ShouldNotBeNull().Status.ShouldBe("Cancelled");
        response.ToolCalls.ShouldHaveSingleItem().IsIncomplete.ShouldBeTrue();
        var row = DefaultToolAuditSink.Instance.ProjectBlockingRun(DefaultToolAuditSink.Instance.CaptureBlockingRun(response)).ShouldHaveSingleItem();
        row.ToolIsIncomplete.ShouldBeTrue();
        Id(row).ShouldBe(Id(end));
    }

    [Fact]
    public async Task Guard_WarningThenProgress_PreservesRecoveredPayloadFreeEvidence()
    {
        var provider = new ScriptedProvider((call, _) => call <= 4 ? Calls(call) : TextResponse());
        var classifications = 0;
        var (_, handle) = Create(provider, [new ProbeTool()], progress: (_, _) =>
            Task.FromResult<ToolProgressDecision?>(++classifications <= 3
                ? ToolProgressDecision.NoProgress("private-payload-4796", "private-payload-4796", "unchanged-read")
                : ToolProgressDecision.Progress));
        await using var owned = handle;
        var completion = (await handle.PromptAsync("go")).Completion.ShouldNotBeNull();
        completion.Status.ShouldBe("Completed");
        var guards = Guards(completion);
        guards.GetArrayLength().ShouldBe(1);
        guards[0].GetProperty("Disposition").GetString().ShouldBe("recovered");
        guards[0].GetProperty("ConsecutiveCount").GetInt32().ShouldBe(3);
        guards[0].GetProperty("TotalResults").GetInt32().ShouldBe(3);
        guards[0].GetProperty("EvidenceReferences").GetArrayLength().ShouldBe(1);
        guards.GetRawText().ShouldNotContain("private-payload-4796");
    }

    [Fact]
    public async Task Guard_SteeringRecovery_RepeatedEpisodesKeepEvidenceBounded()
    {
        var provider = new ScriptedProvider((call, _) => call <= 60 ? Calls(call) : TextResponse());
        var (agent, handle) = Create(provider, [new ProbeTool()], progress: (_, _) =>
            Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress("scope", "evidence", "unchanged-read")));
        await using var owned = handle;
        var turns = 0;
        using var subscription = agent.Subscribe((evt, _) =>
        {
            if (evt is TurnEndEvent && ++turns % 3 == 0)
                agent.Steer(new CoreUserMessage("new direction"));
            return Task.CompletedTask;
        });
        var completion = (await handle.PromptAsync("go")).Completion.ShouldNotBeNull();
        completion.Status.ShouldBe("Completed");
        var guards = Guards(completion);
        guards.GetArrayLength().ShouldBe(16);
        guards.EnumerateArray().ShouldAllBe(g => g.GetProperty("Disposition").GetString() == "recovered");
        guards.EnumerateArray().ShouldAllBe(g => g.GetProperty("EvidenceReferences").GetArrayLength() == 1);
        guards.GetRawText().ShouldNotContain("private-payload-4796");
    }

    [Fact]
    public async Task Agent_SteeringFollowUpParallelAndCompletionContinuation_ShareOneAuthoritativeId()
    {
        var provider = new ScriptedProvider((call, _) => call == 1 ? Calls(call, 2) : TextResponse());
        var tool = new ProbeTool();
        var evaluations = 0;
        var (agent, handle) = Create(provider, [tool], _ => Task.FromResult(
            Interlocked.Increment(ref evaluations) == 1
                ? RunCompletionDecision.Continue(["open"], "continue internally")
                : RunCompletionDecision.Completed));
        await using var ownedHandle = handle;
        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe((evt, _) =>
        {
            events.Add(evt);
            if (evt is ToolExecutionStartEvent { ToolCallId: "call-1-0" })
            {
                agent.Steer(new CoreUserMessage("new direction"));
                agent.FollowUp(new CoreUserMessage("follow-up"));
            }
            return Task.CompletedTask;
        });

        await agent.PromptAsync("go");

        provider.Calls.ShouldBeGreaterThanOrEqualTo(3);
        tool.Executions.ShouldBe(2);
        events.OfType<AgentStartEvent>().ShouldHaveSingleItem();
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status.ShouldBe(RunCompletionStatus.Completed);
        events.OfType<MessageEndEvent>().Select(evt => evt.Message).OfType<CoreUserMessage>().Select(message => message.Content).ShouldContain("new direction");
        events.OfType<MessageEndEvent>().Select(evt => evt.Message).OfType<CoreUserMessage>().Select(message => message.Content).ShouldContain("follow-up");
        events.OfType<ToolExecutionStartEvent>().Count().ShouldBe(2);
        events.OfType<ToolExecutionUpdateEvent>().ShouldNotBeEmpty();
        var firstId = Id(events[0]);
        events.ShouldAllBe(evt => Id(evt) == firstId);

        events.Clear();
        agent.FollowUp(new CoreUserMessage("public continuation"));
        await agent.ContinueAsync();
        var continuationId = Id(events.OfType<AgentStartEvent>().ShouldHaveSingleItem());
        continuationId.ShouldNotBe(firstId, "a public ContinueAsync is a new run, unlike internal continuation turns");
        events.ShouldAllBe(evt => Id(evt) == continuationId);
        events.Clear();
        await agent.PromptWithoutToolsAsync("finalize");
        var finalizationId = Id(events.OfType<AgentStartEvent>().ShouldHaveSingleItem());
        finalizationId.ShouldNotBe(firstId);
        finalizationId.ShouldNotBe(continuationId);
        events.ShouldAllBe(evt => Id(evt) == finalizationId);
    }

    [Fact]
    public async Task Agent_CancelledToolAndNextPrompt_PreserveTerminalIdWithoutLeakingIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new ProbeTool(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        });
        var provider = new ScriptedProvider((call, _) => call == 1 ? Calls(call) : TextResponse());
        var (agent, handle) = Create(provider, [tool]);
        await using var ownedHandle = handle;
        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        var run = agent.PromptAsync("go", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        end.Completion.Status.ShouldBe(RunCompletionStatus.Cancelled);
        events.OfType<ToolExecutionStartEvent>().ShouldHaveSingleItem();
        var cancelledId = Id(events.OfType<AgentStartEvent>().ShouldHaveSingleItem());
        Id(end).ShouldBe(cancelledId);
        events.ShouldAllBe(evt => Id(evt) == cancelledId);
        events.Clear();
        await agent.PromptAsync("next");
        var nextId = Id(events.OfType<AgentStartEvent>().ShouldHaveSingleItem());
        nextId.ShouldNotBe(cancelledId);
        events.ShouldAllBe(evt => Id(evt) == nextId);
    }

    [Fact]
    public async Task Handle_BlockingAndStreaming_PropagateCoreIdToTimelineAndHistory()
    {
        // Same provider repeats call ids in separate runs: correlation cannot be derived from call id.
        var provider = new ScriptedProvider((call, _) => call % 2 == 1 ? Calls(1, 2) : TextResponse());
        var (agent, handle) = Create(provider, [new ProbeTool()]);
        await using var ownedHandle = handle;
        var core = new List<AgentEvent>();
        using var subscription = agent.Subscribe((evt, _) => { core.Add(evt); return Task.CompletedTask; });
        var response = await handle.PromptAsync("blocking");
        response.ToolCalls.Count.ShouldBe(2);
        var blockingId = Id(core.OfType<AgentStartEvent>().ShouldHaveSingleItem());
        Id(response).ShouldBe(blockingId);
        var sink = DefaultToolAuditSink.Instance;
        var timeline = sink.CaptureBlockingRun(response);
        timeline.Count.ShouldBe(2);
        timeline.ShouldAllBe(record => Id(record) == blockingId);
        sink.ProjectBlockingRun(timeline).ShouldAllBe(row => Id(row) == blockingId);
        core.Clear();

        var session = new GatewaySession { AgentId = handle.AgentId, SessionId = handle.SessionId };
        var published = new List<AgentStreamEvent>();
        var result = await StreamingSessionHelper.ProcessAndSaveAsync(handle.StreamAsync("streaming"), session,
            new Mock<ISessionStore>().Object, new StreamingSessionOptions(OnEventAsync: (evt, _) =>
            {
                published.Add(evt);
                return ValueTask.CompletedTask;
            }));
        var streamingId = Id(core.OfType<AgentStartEvent>().ShouldHaveSingleItem());
        streamingId.ShouldNotBe(blockingId);
        core.ShouldAllBe(evt => Id(evt) == streamingId);
        published.ShouldNotBeEmpty();
        published.ShouldAllBe(evt => Id(evt) == streamingId);
        result.ToolInvocations.Count.ShouldBe(2);
        result.ToolInvocations.ShouldAllBe(record => Id(record) == streamingId);
        session.GetHistorySnapshot().Where(row => row.Role.Equals(MessageRole.Tool)).Count().ShouldBe(4);
        session.GetHistorySnapshot().ShouldAllBe(row => Id(row) == streamingId);
    }

    [Theory]
    [InlineData(false, 1, 6, 6)]
    [InlineData(false, 4, 8, 8)]
    [InlineData(true, 3, 0, 129)]
    public async Task Guard_TerminalEvidence_DistinguishesSemanticStopFromFuseAndCountsExecutedBatch(
        bool absolute, int batchSize, int consecutive, int total)
    {
        var tool = new ProbeTool();
        var provider = new ScriptedProvider((call, _) => call <= 44 ? Calls(call, batchSize) : TextResponse());
        var (agent, handle) = Create(provider, [tool], progress: (_, _) => Task.FromResult<ToolProgressDecision?>(
            absolute ? ToolProgressDecision.Progress : ToolProgressDecision.NoProgress("scope", "evidence", "unchanged-read")));
        await using var ownedHandle = handle;
        AgentEndEvent? terminal = null;
        using var subscription = agent.Subscribe((evt, _) =>
        {
            if (evt is AgentEndEvent end) terminal = end;
            return Task.CompletedTask;
        });
        var response = await handle.PromptAsync("private-payload-4796");
        tool.Executions.ShouldBe(total);
        response.ToolCalls.Count.ShouldBe(total);
        var end = terminal.ShouldNotBeNull();
        end.Completion.Status.ShouldBe(RunCompletionStatus.Parked);
        AssertGuard(end.Completion, absolute ? "absolute-tool-result-limit" : "unchanged-read", consecutive, total, absolute);
        var projected = response.Completion.ShouldNotBeNull();
        AssertGuard(projected, absolute ? "absolute-tool-result-limit" : "unchanged-read", consecutive, total, absolute);
        Guards(projected).GetRawText().ShouldBe(Guards(end.Completion).GetRawText());
        Id(response).ShouldBe(Id(end));
    }

    [Fact]
    public async Task Guard_StreamingTerminalEvidence_SurvivesSessionSaveAndJsonReload()
    {
        var provider = new ScriptedProvider((call, _) => call <= 6 ? Calls(call) : TextResponse());
        var (_, handle) = Create(provider, [new ProbeTool()], progress: (_, _) =>
            Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress("scope", "evidence", "unchanged-read")));
        await using var ownedHandle = handle;
        var directory = Path.Combine(Path.GetTempPath(), "guard-4796", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "sessions.db")};Pooling=False";
            var conversations = new BotNexus.Gateway.Conversations.InMemoryConversationStore();
            var store = new BotNexus.Gateway.Sessions.SqliteSessionStore(connectionString,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BotNexus.Gateway.Sessions.SqliteSessionStore>.Instance,
                conversations);
            var session = await store.GetOrCreateAsync(handle.SessionId, handle.AgentId);
            var result = await StreamingSessionHelper.ProcessAndSaveAsync(handle.StreamAsync("go"), session, store);
            AssertGuard(result.Completion.ShouldNotBeNull(), "unchanged-read", 6, 6, false);
            var reopened = new BotNexus.Gateway.Sessions.SqliteSessionStore(connectionString,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BotNexus.Gateway.Sessions.SqliteSessionStore>.Instance,
                conversations);
            var reloaded = (await reopened.GetAsync(session.SessionId)).ShouldNotBeNull();
            AssertGuard(reloaded.RunCompletion.ShouldNotBeNull(), "unchanged-read", 6, 6, false);
            var json = System.Text.Json.JsonSerializer.Serialize(reloaded.RunCompletion);
            var completion = System.Text.Json.JsonSerializer.Deserialize<RunCompletionSignal>(json).ShouldNotBeNull();
            AssertGuard(completion, "unchanged-read", 6, 6, false);
            reloaded.GetHistorySnapshot().Where(row => row.Role.Equals(MessageRole.Tool)).Count().ShouldBe(12);
            var id = Id(result.ToolInvocations[0]);
            reloaded.GetHistorySnapshot().ShouldAllBe(row => Id(row) == id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
