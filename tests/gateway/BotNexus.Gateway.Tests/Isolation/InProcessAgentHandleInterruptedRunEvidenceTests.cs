using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
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

/// <summary>
/// #4101: exercises real core cancellation through the real blocking gateway boundary. Only the
/// provider, harmless read tool and persistence destination are test doubles, not either seam.
/// </summary>
/// <remarks>
/// Named mutation: RemoveInterruptedPartialPropagation. Remove the blocking boundary's propagation
/// of a cancelled core run into AgentPromptInterruptedException (return BuildResponse instead).
/// PromptAsync_ToolCancellation_PreservesRunLocalEvidence must fail for BOTH overloads, while
/// PromptAsync_CompletedRun_RemainsAnOrdinaryResponse remains green. Separately restoring core's
/// singleton [abortedMessage] return must fail CorePromptAsync_ToolCancellation_ReturnsRunLocalTimeline.
/// These are mutation specifications, not a claim that a mutation run has already been executed.
/// </remarks>
public sealed class InProcessAgentHandleInterruptedRunEvidenceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task CorePromptAsync_ToolCancellation_ReturnsRunLocalTimeline()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Agent.PromptAsync("current run", cancellation.Token);
        await fixture.Tool.Entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();

        // Core retains its existing return-based cancellation contract; the gateway adapts it.
        var messages = await run.WaitAsync(Deadline);

        fixture.Provider.Calls.ShouldBe(2);
        messages.OfType<AssistantAgentMessage>().Where(message => message.Usage is not null)
            .Count().ShouldBe(2);
        var calls = messages.OfType<AssistantAgentMessage>()
            .SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id).ToArray();
        calls.ShouldBe(new[] { "completed-4101", "interrupted-4101" });
        messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem()
            .ToolCallId.ShouldBe("completed-4101");
        fixture.Agent.State.LastCompletion.ShouldNotBeNull().Status.ShouldBe(RunCompletionStatus.Cancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAsync_ToolCancellation_PreservesRunLocalEvidence(bool structured)
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var run = PromptAsync(fixture.Handle, structured, cancellation.Token);
        await fixture.Tool.Entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();

        var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() => run.WaitAsync(Deadline));

        interrupted.CancellationToken.ShouldBe(cancellation.Token);
        interrupted.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        AssertPartial(fixture, interrupted.PartialResponse);
    }

    [Fact]
    public async Task PromptWhenAvailableAsync_ToolCancellation_PreservesRunLocalEvidence()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var started = 0;
        var run = fixture.Handle.PromptWhenAvailableAsync(new AgentUserMessage("current run"), () =>
        {
            started++;
            return Task.CompletedTask;
        }, cancellation.Token);
        await fixture.Tool.Entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();

        var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() => run.WaitAsync(Deadline));

        started.ShouldBe(1);
        interrupted.CancellationToken.ShouldBe(cancellation.Token);
        AssertPartial(fixture, interrupted.PartialResponse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAsync_CoreAbortWithoutCallerCancellation_StillPropagatesInterruption(bool structured)
    {
        await using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var run = PromptAsync(fixture.Handle, structured, caller.Token);
        await fixture.Tool.Entered.Task.WaitAsync(Deadline);

        await fixture.Agent.AbortAsync().WaitAsync(Deadline);
        var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() => run.WaitAsync(Deadline));

        caller.IsCancellationRequested.ShouldBeFalse("internal abort must not cancel the caller's source");
        interrupted.CancellationToken.IsCancellationRequested.ShouldBeTrue(
            "the exception must carry the cancellation that actually interrupted core, not an unsignalled caller token");
        AssertPartial(fixture, interrupted.PartialResponse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAsync_CancelledBeforeRun_UsageRemainsUnknown(bool structured)
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() =>
            PromptAsync(fixture.Handle, structured, cancellation.Token));

        interrupted.CancellationToken.ShouldBe(cancellation.Token);
        fixture.Provider.Calls.ShouldBe(0);
        interrupted.PartialResponse.ToolCalls.ShouldBeEmpty("seeded session history is not this run");
        interrupted.PartialResponse.Usage.ShouldBeNull();
        interrupted.PartialResponse.RunUsage.ShouldBeNull();
        interrupted.PartialResponse.TurnCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAsync_CompletedRun_RemainsAnOrdinaryResponse(bool structured)
    {
        await using var fixture = new Fixture(blockTool: false);

        var response = await PromptAsync(fixture.Handle, structured, CancellationToken.None).WaitAsync(Deadline);

        response.Content.ShouldBe("finished");
        response.Completion.ShouldNotBeNull().Status.ShouldBe("Completed");
        response.TerminalError.ShouldBeNull();
        response.TurnCount.ShouldBe(3);
        fixture.Provider.Calls.ShouldBe(3);
        response.ToolCalls.Select(call => call.ToolCallId)
            .ShouldBe(new[] { "completed-4101", "interrupted-4101" });
        response.ToolCalls.ShouldAllBe(call => !call.IsIncomplete && !call.IsError);
        response.RunUsage.ShouldNotBeNull().InputTokens.ShouldBe(36);
        response.RunUsage.ShouldNotBeNull().OutputTokens.ShouldBe(22);
    }

    [Fact]
    public async Task StreamAsync_CompletedRun_RetainsToolTimelineAndCompletion()
    {
        await using var fixture = new Fixture(blockTool: false);
        var events = new List<AgentStreamEvent>();

        await foreach (var evt in fixture.Handle.StreamAsync("current run"))
            events.Add(evt);

        events.Where(evt => evt.Type == AgentStreamEventType.ToolStart).Select(evt => evt.ToolCallId)
            .ShouldBe(new[] { "completed-4101", "interrupted-4101" });
        events.Where(evt => evt.Type == AgentStreamEventType.ToolEnd).Count().ShouldBe(2);
        events.ShouldNotContain(evt => evt.Type == AgentStreamEventType.Error);
        events.Last(evt => evt.Completion is not null).Completion.ShouldNotBeNull().Status.ShouldBe("Completed");
        fixture.Provider.Calls.ShouldBe(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherBlockingPrompts_CancelledBeforeAdmission_ExcludeHistoricalEvidence(bool withoutTools)
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() => withoutTools
            ? fixture.Handle.PromptWithoutToolsAsync("finalize", cancellation.Token)
            : fixture.Handle.PromptWhenAvailableAsync(new AgentUserMessage("queued"),
                () => throw new InvalidOperationException("must not start"), cancellation.Token));

        interrupted.CancellationToken.ShouldBe(cancellation.Token);
        interrupted.PartialResponse.ToolCalls.ShouldBeEmpty();
        interrupted.PartialResponse.Usage.ShouldBeNull();
        interrupted.PartialResponse.RunUsage.ShouldBeNull();
        interrupted.PartialResponse.TurnCount.ShouldBe(0);
        fixture.Provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PromptWithoutToolsAsync_ProviderAbort_PreservesMeasurement()
    {
        await using var fixture = new Fixture();
        fixture.Provider.TextOnly = true;
        fixture.Provider.AbortFirst = true;

        var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() =>
            fixture.Handle.PromptWithoutToolsAsync("finalize").WaitAsync(Deadline));

        interrupted.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        interrupted.PartialResponse.Completion.ShouldNotBeNull().Status.ShouldBe("Cancelled");
        interrupted.PartialResponse.TurnCount.ShouldBe(1);
        interrupted.PartialResponse.RunUsage.ShouldNotBeNull().InputTokens.ShouldBe(11);
        interrupted.PartialResponse.ToolCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task PromptAsync_ZeroToolSuccess_RemainsAnOrdinaryResponse()
    {
        await using var fixture = new Fixture();
        fixture.Provider.TextOnly = true;

        var response = await fixture.Handle.PromptAsync("text only").WaitAsync(Deadline);

        response.Content.ShouldBe("finished");
        response.Completion.ShouldNotBeNull().Status.ShouldBe("Completed");
        response.ToolCalls.ShouldBeEmpty();
        response.TurnCount.ShouldBe(1);
        response.RunUsage.ShouldNotBeNull().InputTokens.ShouldBe(11);
    }

    [Fact]
    public async Task PromptAsync_ProviderAbortedMeasurement_IsNotASyntheticTurn()
    {
        await using var fixture = new Fixture();
        fixture.Provider.TextOnly = true;
        fixture.Provider.AbortFirst = true;

        var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() =>
            fixture.Handle.PromptAsync("provider abort").WaitAsync(Deadline));

        interrupted.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        interrupted.PartialResponse.TurnCount.ShouldBe(1);
        interrupted.PartialResponse.Usage.ShouldNotBeNull().InputTokens.ShouldBe(11);
        interrupted.PartialResponse.RunUsage.ShouldNotBeNull().OutputTokens.ShouldBe(7);
        interrupted.PartialResponse.ToolCalls.ShouldBeEmpty();
        interrupted.PartialResponse.TerminalError.ShouldBeNull();
    }

    [Fact]
    public async Task StreamAsync_ToolCancellation_ClosesAuditWithoutInternalError()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var events = new List<AgentStreamEvent>();
        async Task ConsumeAsync()
        {
            await foreach (var evt in fixture.Handle.StreamAsync("current run", cancellation.Token))
                events.Add(evt);
        }
        var run = ConsumeAsync();
        await fixture.Tool.Entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        try { await run.WaitAsync(Deadline); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

        events.ShouldNotContain(evt => evt.Type == AgentStreamEventType.Error);
        fixture.Agent.State.LastCompletion.ShouldNotBeNull().Status.ShouldBe(RunCompletionStatus.Cancelled);
        fixture.Session.History.Where(row => row.ToolCallId == "interrupted-4101"
            && row.Kind == MessageKind.ToolResult).ShouldHaveSingleItem().ToolIsIncomplete.ShouldBeTrue();
    }

    [Fact]
    public async Task CorePromptAsync_InterruptedSnapshot_SurvivesAnotherRun()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Agent.PromptAsync("current run", cancellation.Token);
        await fixture.Tool.Entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        var snapshot = (await run.WaitAsync(Deadline)).ShouldBeOfType<AgentRunMessages>();
        var id = snapshot.AgentRunId;
        var messages = snapshot.ToArray();

        await fixture.Agent.PromptAsync("next run").WaitAsync(Deadline);

        fixture.Agent.State.AgentRunId.ShouldNotBe(id);
        snapshot.AgentRunId.ShouldBe(id);
        snapshot.Completion.ShouldNotBeNull().Status.ShouldBe(RunCompletionStatus.Cancelled);
        snapshot.CancellationToken.ShouldBe(cancellation.Token);
        snapshot.PendingToolCalls.ShouldContain("interrupted-4101");
        snapshot.ToArray().ShouldBe(messages);
    }

    [Fact]
    public async Task CorePromptAsync_FailureAfterTool_KeepsSingletonReturnAndSeparateRunEvidence()
    {
        await using var fixture = new Fixture(blockTool: false);
        fixture.Provider.FailAfterTool = true;

        var messages = await fixture.Agent.PromptAsync("fail after tool").WaitAsync(Deadline);

        messages.ShouldHaveSingleItem().ShouldBeOfType<AssistantAgentMessage>().FinishReason.ShouldBe(StopReason.Error);
        var snapshot = messages.ShouldBeOfType<AgentRunMessages>();
        snapshot.Completion.ShouldNotBeNull().Status.ShouldBe(RunCompletionStatus.Failed);
        snapshot.EvidenceMessages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem().ToolCallId.ShouldBe("completed-4101");
        var response = fixture.Handle.CompleteBlockingRun(snapshot);
        response.TerminalError.ShouldNotBeNull().ShouldContain("provider failure 4101");
        response.ToolCalls.ShouldHaveSingleItem().ResultContent.ShouldBe("harmless content");
        response.RunUsage.ShouldNotBeNull().InputTokens.ShouldBe(11);
        response.Usage.ShouldBeNull("the failure marker has no measured usage");
    }

    [Fact]
    public async Task CompleteBlockingRun_AfterNextRunBegins_DoesNotCloseNextRunsTool()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Agent.PromptAsync("first", cancellation.Token);
        await fixture.Tool.Entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        var snapshot = await first.WaitAsync(Deadline);
        // Deterministically interleave the next run's admission and tool start between core
        // releasing its slot and the old blocking caller projecting its settled response.
        var nextId = BotNexus.Domain.Primitives.AgentRunId.From("next-run-4101");
        fixture.Audit.BeginRun(nextId);
        await fixture.Audit.PersistStartAsync("next-tool", "read", new Dictionary<string, object?>(), CancellationToken.None);

        Should.Throw<AgentPromptInterruptedException>(() => fixture.Handle.CompleteBlockingRun(snapshot));

        fixture.Session.History.ShouldNotContain(row => row.ToolCallId == "next-tool" && row.Kind == MessageKind.ToolResult);
        fixture.Session.History.Where(row => row.ToolCallId == "next-tool").ShouldHaveSingleItem()
            .AgentRunId.ShouldBe(nextId);
        fixture.Audit.RecordCompleted("next-tool");
    }

    [Fact]
    public void BuildToolCalls_UnstartedSiblingWithoutResult_IsIncompleteNotSuccessful()
    {
        AgentMessage[] messages = [new AssistantAgentMessage("requests",
            [Call("started"), Call("unstarted")], StopReason.ToolUse)];

        var calls = InProcessAgentHandle.BuildToolCalls(messages, new HashSet<string> { "started" });

        calls.Select(call => call.ToolCallId).ShouldBe(new[] { "started", "unstarted" });
        calls.ShouldAllBe(call => call.IsIncomplete && call.IsError && call.ResultContent == null);
        var records = InProcessAgentHandle.BuildToolInvocations(messages, new HashSet<string> { "started" });
        records.Select(record => record.ToolCallId).ShouldBe(new[] { "started", "unstarted" });
        records.ShouldAllBe(record => record.IsIncomplete && record.IsError && record.CompletedAt == null);
    }

    [Fact]
    public void BuildResponse_SuccessfulLastAssistantWithoutUsage_DoesNotReuseEarlierUsage()
    {
        AgentMessage[] messages = [
            new AssistantAgentMessage("first", Usage: new AgentUsage(InputTokens: 11, OutputTokens: 7)),
            new AssistantAgentMessage("finished")];

        var response = InProcessAgentHandle.BuildResponse(messages, null);

        response.Content.ShouldBe("finished");
        response.Usage.ShouldBeNull();
        response.RunUsage.ShouldNotBeNull().InputTokens.ShouldBe(11);
        response.TurnCount.ShouldBe(2);
    }

    private static Task<AgentResponse> PromptAsync(InProcessAgentHandle handle, bool structured, CancellationToken token)
        => structured ? handle.PromptAsync(new AgentUserMessage("current run"), token)
            : handle.PromptAsync("current run", token);

    private static void AssertPartial(Fixture fixture, AgentResponse response)
    {
        fixture.Provider.Calls.ShouldBe(2);
        response.AgentRunId.ShouldNotBeNull();
        response.TerminalError.ShouldBeNull("cancellation is not a provider fault");
        var completion = response.Completion.ShouldNotBeNull();
        completion.Status.ShouldBe("Cancelled");
        completion.StopReason.ShouldBe("Cancellation");
        response.TurnCount.ShouldBe(2, "the synthetic abort is not a model invocation");
        var usage = response.Usage.ShouldNotBeNull("retain the last actual provider measurement");
        usage.InputTokens.ShouldBe(17);
        usage.OutputTokens.ShouldBe(13);
        usage.CacheRead.ShouldBeNull();
        usage.CacheWrite.ShouldBeNull();
        var aggregate = response.RunUsage.ShouldNotBeNull();
        aggregate.InputTokens.ShouldBe(28);
        aggregate.OutputTokens.ShouldBe(20);
        response.ToolCalls.Select(call => call.ToolCallId)
            .ShouldBe(new[] { "completed-4101", "interrupted-4101" });
        response.ToolCalls.ShouldAllBe(call => call.AgentRunId == response.AgentRunId);
        var completed = response.ToolCalls[0];
        completed.ToolName.ShouldBe("read");
        completed.Arguments.ShouldNotBeNull().ShouldContain("completed-4101.txt");
        completed.ResultContent.ShouldBe("harmless content");
        completed.IsError.ShouldBeFalse();
        completed.IsIncomplete.ShouldBeFalse();
        var pending = response.ToolCalls[1];
        pending.ToolName.ShouldBe("read");
        pending.Arguments.ShouldNotBeNull().ShouldContain("interrupted-4101.txt");
        pending.ResultContent.ShouldBeNull();
        pending.IsIncomplete.ShouldBeTrue();
        pending.IsError.ShouldBeTrue();
        var sink = DefaultToolAuditSink.Instance;
        var projected = sink.ProjectBlockingRun(sink.CaptureBlockingRun(response));
        projected.Select(row => row.ToolCallId)
            .ShouldBe(new[] { "completed-4101", "interrupted-4101" });
        projected[0].Content.ShouldContain("harmless content");
        projected[1].ToolIsIncomplete.ShouldBeTrue();
        var durable = fixture.Session.History.Where(row => row.ToolCallId == "interrupted-4101"
            && row.Kind == MessageKind.ToolResult).ShouldHaveSingleItem();
        durable.ToolIsIncomplete.ShouldBeTrue("write-ahead must close an interrupted invocation exactly once");
    }

    internal static async Task AssertUnwindAuditAsync(string path)
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        if (path == "crash")
        {
            // Crash after admission with an outstanding durable invocation. The real AgentEnd
            // subscriber, not this fixture, must close it before the run releases its slot.
            var messages = await fixture.Agent.PromptWhenAvailableAsync(new BotNexus.Agent.Core.Types.UserMessage("crash"), async () =>
            {
                await fixture.Audit.PersistStartAsync("crashed-4101", "read", new Dictionary<string, object?>(), CancellationToken.None);
                throw new InvalidOperationException("crash after durable start");
            });
            messages.ShouldBeOfType<AgentRunMessages>().Completion.ShouldNotBeNull().Status.ShouldBe(RunCompletionStatus.Failed);
            fixture.Session.GetHistorySnapshot().Where(row => row.ToolCallId == "crashed-4101" && row.Kind == MessageKind.ToolResult)
                .ShouldHaveSingleItem().ToolIsIncomplete.ShouldBeTrue();
            return;
        }

        if (path == "stream")
        {
            async Task ConsumeAsync()
            {
                await foreach (var _ in fixture.Handle.StreamAsync("cancel stream", cancellation.Token)) { }
            }
            var run = ConsumeAsync();
            await fixture.Tool.Entered.Task.WaitAsync(Deadline);
            cancellation.Cancel();
            try { await run.WaitAsync(Deadline); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        else
        {
            var run = fixture.Handle.PromptAsync("cancel blocking", cancellation.Token);
            await fixture.Tool.Entered.Task.WaitAsync(Deadline);
            cancellation.Cancel();
            var interrupted = await Should.ThrowAsync<AgentPromptInterruptedException>(() => run.WaitAsync(Deadline));
            interrupted.CancellationToken.ShouldBe(cancellation.Token);
            interrupted.PartialResponse.ToolCalls.ShouldContain(call => call.ToolCallId == "completed-4101" && !call.IsIncomplete);
        }
        fixture.Session.GetHistorySnapshot().Where(row => row.ToolCallId == "interrupted-4101" && row.Kind == MessageKind.ToolResult)
            .ShouldHaveSingleItem().ToolIsIncomplete.ShouldBeTrue();
        fixture.Session.GetHistorySnapshot().ShouldNotContain(row => row.ToolCallId == "completed-4101" && row.ToolIsIncomplete);
    }

    [Fact]
    public async Task InterruptedProjection_DeduplicatesWriteAheadWithinRun_WithoutMergingReusedCallIds()
    {
        await using var fixture = new Fixture();
        var sink = DefaultToolAuditSink.Instance;
        foreach (var id in new[] { "first-run", "second-run" })
        {
            var runId = BotNexus.Domain.Primitives.AgentRunId.From(id);
            fixture.Audit.BeginRun(runId);
            await fixture.Audit.PersistStartAsync("reused-call", "read", new Dictionary<string, object?>(), CancellationToken.None);
            await fixture.Audit.RecordInterruptedAsync(CancellationToken.None);
            var response = new AgentResponse
            {
                AgentRunId = runId, Content = string.Empty,
                ToolCalls = [new("reused-call", "read", true, "{}", null, IsIncomplete: true) { AgentRunId = runId }]
            };
            fixture.Session.AddEntries(sink.ProjectBlockingRun(sink.CaptureBlockingRun(response)));
            fixture.Session.GetHistorySnapshot().Where(row => row.AgentRunId == runId && row.Kind == MessageKind.ToolResult)
                .ShouldHaveSingleItem().ToolIsIncomplete.ShouldBeTrue();
        }
        fixture.Session.GetHistorySnapshot().Count(row => row.Kind == MessageKind.ToolResult).ShouldBe(2);
    }

    [Fact]
    public async Task ExchangeConsumer_RealWriteAheadAndHandle_PersistEachResultOnceAcrossReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "interrupted-consumer-4101", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Fixture? fixture = null;
        try
        {
            var conversations = new BotNexus.Gateway.Conversations.InMemoryConversationStore();
            var connection = $"Data Source={Path.Combine(directory, "sessions.db")};Pooling=False";
            var store = new BotNexus.Gateway.Sessions.SqliteSessionStore(connection,
                NullLogger<BotNexus.Gateway.Sessions.SqliteSessionStore>.Instance, conversations);
            var initiator = AgentId.From("initiator-4101");
            var target = AgentId.From("target-4101");
            var registry = new Mock<BotNexus.Gateway.Abstractions.Agents.IAgentRegistry>();
            registry.Setup(r => r.Contains(target)).Returns(true);
            registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
            {
                AgentId = initiator, DisplayName = "Initiator", ModelId = "test", ApiProvider = "test", SubAgentIds = [target.Value]
            });
            var entered = new TaskCompletionSource<Fixture>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supervisor = new Mock<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>();
            supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns(async (AgentId _, SessionId id, CancellationToken ct) =>
                {
                    fixture = new Fixture(session: (await store.GetAsync(id, ct)).ShouldNotBeNull(), sessionStore: store);
                    entered.TrySetResult(fixture);
                    return fixture.Handle;
                });
            var service = new BotNexus.Gateway.Agents.AgentExchangeService(registry.Object, supervisor.Object, store, conversations,
                Microsoft.Extensions.Options.Options.Create(new BotNexus.Gateway.Configuration.GatewayOptions()),
                NullLogger<BotNexus.Gateway.Agents.AgentExchangeService>.Instance);
            using var cancellation = new CancellationTokenSource();
            var run = service.ConverseAsync(new BotNexus.Domain.AgentExchange.AgentExchangeRequest
            {
                InitiatorId = initiator, TargetId = target, Message = "work", MaxTurns = 1
            }, cancellation.Token);
            var active = await entered.Task.WaitAsync(Deadline);
            await active.Tool.Entered.Task.WaitAsync(Deadline);
            cancellation.Cancel();
            var exception = await Should.ThrowAsync<OperationCanceledException>(() => run.WaitAsync(Deadline));
            exception.CancellationToken.ShouldBe(cancellation.Token);
            var reloaded = (await new BotNexus.Gateway.Sessions.SqliteSessionStore(connection,
                NullLogger<BotNexus.Gateway.Sessions.SqliteSessionStore>.Instance, conversations).GetAsync(active.Session.SessionId)).ShouldNotBeNull();
            var results = reloaded.GetHistorySnapshot().Where(row => row.Kind == MessageKind.ToolResult).ToArray();
            results.Length.ShouldBe(2);
            results.Single(row => row.ToolCallId == "completed-4101").ToolIsIncomplete.ShouldBeFalse();
            results.Single(row => row.ToolCallId == "interrupted-4101").ToolIsIncomplete.ShouldBeTrue();
            results.ShouldAllBe(row => row.AgentRunId != null);
            reloaded.GetHistorySnapshot().ShouldNotContain(row => row.Role == MessageRole.Assistant);
            // Inspect physical rows too: a normalized reader must not hide duplicated history.
            await using var database = new Microsoft.Data.Sqlite.SqliteConnection(connection);
            await database.OpenAsync();
            await using var query = database.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM session_history WHERE session_id=$session AND message_kind='tool-result'";
            query.Parameters.AddWithValue("$session", active.Session.SessionId.Value);
            Convert.ToInt64(await query.ExecuteScalarAsync()).ShouldBe(2);
        }
        finally
        {
            if (fixture is not null) await fixture.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public ScriptedProvider Provider { get; } = new();
        public ReadTool Tool { get; }
        public BotNexus.Agent.Core.Agent Agent { get; }
        public InProcessAgentHandle Handle { get; }
        public ToolAuditWriteAhead Audit { get; }
        public GatewaySession Session { get; } = new()
        {
            AgentId = AgentId.From("agent-4101"), SessionId = SessionId.From("session-4101"),
            ConversationId = ConversationId.From("conversation-4101")
        };

        public Fixture(bool blockTool = true, GatewaySession? session = null, ISessionStore? sessionStore = null)
        {
            if (session is not null) Session = session;
            Tool = new ReadTool(blockTool);
            var model = new LlmModel("test-model", "test-model", "test-api", "test-provider", "http://localhost",
                false, ["text"], new ModelCost(0, 0, 0, 0), 8192, 1024);
            var models = new ModelRegistry();
            models.Register(model.Provider, model);
            var providers = new ApiProviderRegistry();
            providers.Register(Provider);
            var store = new Mock<ISessionStore>();
            store.Setup(s => s.GetAsync(Session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(Session);
            store.Setup(s => s.SaveAsync(Session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            store.Setup(s => s.AppendEntriesAsync(Session.SessionId, It.IsAny<IReadOnlyList<SessionEntry>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SessionId _, IReadOnlyList<SessionEntry> rows, CancellationToken _) =>
                {
                    Session.AddEntries(rows);
                    return new SessionAppendMutationResult(SessionMutationOutcome.Applied, rows.Count);
                });
            var audit = Audit = new ToolAuditWriteAhead(sessionStore ?? store.Object, DefaultToolAuditSink.Instance, new SecretRedactor(),
                Session.AgentId, Session.SessionId, NullLogger.Instance);
            IAgentTool[] tools = [Tool];
            AgentMessage[] history = [
                new AssistantAgentMessage("historical", [Call("historical-4101")], StopReason.ToolUse,
                    new AgentUsage(InputTokens: 900, OutputTokens: 800)),
                new ToolResultAgentMessage("historical-4101", "read", Result())];
            var options = new AgentOptions(
                new AgentInitialState(SystemPrompt: "test", Model: model, Tools: tools, Messages: history), model,
                new LlmClient(providers, models), null, null,
                (_, _) => Task.FromResult<ProviderExecutionOptions?>(null), null, null,
                ToolExecutionMode.Parallel, null,
                (ctx, _) =>
                {
                    audit.RecordCompleted(ctx.ToolCallRequest.Id);
                    return Task.FromResult<BotNexus.Agent.Core.ExtensionPoints.ToolResults.ToolResultTransformResult?>(null);
                }, new GenerationOptions(), QueueMode.All, QueueMode.All, SessionId: Session.SessionId.Value,
                ToolAuditGate: async (ctx, ct) =>
                {
                    await audit.PersistStartAsync(ctx.ToolCallRequest.Id, ctx.ToolCallRequest.Name, ctx.ValidatedArgs, ct);
                    return null;
                });
            Agent = new BotNexus.Agent.Core.Agent(options);
            Handle = new InProcessAgentHandle(Agent, Session.AgentId, Session.SessionId, NullLogger.Instance,
                tools: tools, toolWriteAhead: audit);
        }

        public ValueTask DisposeAsync() => Handle.DisposeAsync();
    }

    private static ToolCallContent Call(string id)
        => new(id, "read", new Dictionary<string, object?> { ["path"] = id + ".txt" });
    private static AgentToolResult Result()
        => new([new AgentToolContent(AgentToolContentType.Text, "harmless content")]);

    internal sealed class ScriptedProvider : IApiProvider
    {
        public string Api => "test-api";
        public int Calls { get; private set; }
        public bool TextOnly { get; set; }
        public string Text { get; set; } = "finished";
        public bool AbortFirst { get; set; }
        public bool FailAfterTool { get; set; }
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context);
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var index = Calls++;
            if (FailAfterTool && index > 0) throw new InvalidOperationException("provider failure 4101");
            var content = TextOnly ? new ContentBlock[] { new TextContent(Text) } : index switch
            {
                0 => new ContentBlock[] { new TextContent("first check"), Call("completed-4101") },
                1 => new ContentBlock[] { new TextContent("second check"), Call("interrupted-4101") },
                2 => new ContentBlock[] { new TextContent("finished") },
                _ => throw new InvalidOperationException("Unexpected provider invocation in #4101 fixture")
            };
            var usage = index switch
            {
                0 => new Usage { Input = 11, Output = 7 },
                1 => new Usage { Input = 17, Output = 13 },
                _ => new Usage { Input = 8, Output = 2 }
            };
            var reason = AbortFirst ? StopReason.Aborted : !TextOnly && index < 2 ? StopReason.ToolUse : StopReason.Stop;
            var message = new AssistantMessage(content, model.Api, model.Provider, model.Id,
                usage, reason, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var stream = new LlmStream();
            stream.Push(new StartEvent(message with { Content = [] }));
            stream.Push(new DoneEvent(reason, message));
            return stream;
        }
    }

    internal sealed class ReadTool(bool block) : IAgentTool
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "read";
        public string Label => "read";
        public Tool Definition => new(Name, "harmless test read", JsonSerializer.SerializeToElement(new { type = "object" }));
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);
        public async Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            if (block && toolCallId == "interrupted-4101")
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Result();
        }
    }
}
