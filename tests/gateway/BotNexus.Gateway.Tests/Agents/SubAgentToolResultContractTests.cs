using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// #4793: child results belong to the requesting tool call, not a synthetic inbound turn.
/// All tests use the existing tool dictionary contract; no future manager API is required.
/// </summary>
public sealed class SubAgentToolResultContractTests
{
    private const string Summary = "4793-terminal-result-only-once";
    private static readonly SessionId ParentSession = SessionId.From("parent-session");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpawnTool_DefaultOrExplicitForeground_ReturnsTerminalResultAndExpandableIdentity(bool explicitForeground)
    {
        await using var harness = new Harness();
        var args = new Dictionary<string, object?> { ["task"] = "investigate" };
        if (explicitForeground)
            args["background"] = false;

        var execution = harness.SpawnTool.ExecuteAsync("spawn-4793", args);
        await harness.ChildStarted.Task.WaitAsync(Deadline);
        await harness.SpawnPublished.Task.WaitAsync(Deadline);
        try
        {
            execution.IsCompleted.ShouldBeFalse("foreground spawn must remain attached to the running child");
        }
        finally
        {
            harness.CompleteChild();
        }

        var result = await execution.WaitAsync(Deadline);
        var info = (await harness.Manager.ListAsync(ParentSession)).ShouldHaveSingleItem();
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        ReadText(result).ShouldContain(Summary);
        using var json = JsonDocument.Parse(ReadText(result));
        json.RootElement.GetProperty("subAgentId").GetString().ShouldBe(info.SubAgentId);
        json.RootElement.GetProperty("sessionId").GetString().ShouldBe(info.ChildSessionId.Value);
        json.RootElement.GetProperty("conversationId").GetString().ShouldBe(info.ChildConversationId?.Value);
        ReadStatus(json.RootElement).ShouldBe(SubAgentStatus.Completed);

        var conversationId = info.ChildConversationId ?? throw new InvalidOperationException("Missing child conversation.");
        var child = await harness.Conversations.GetAsync(conversationId);
        child.ShouldNotBeNull();
        child.SpawningToolCallId.ShouldBe("spawn-4793");
        harness.Dispatcher.Verify(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SpawnTool_ExplicitBackground_ReturnsRunningIdentityBeforeChildCompletes()
    {
        await using var harness = new Harness();
        var result = await harness.SpawnTool.ExecuteAsync("spawn-4793", new Dictionary<string, object?>
        {
            ["task"] = "investigate",
            ["background"] = true,
            ["maxTurns"] = 7,
            ["timeoutSeconds"] = 120
        }).WaitAsync(Deadline);
        await harness.ChildStarted.Task.WaitAsync(Deadline);

        var info = (await harness.Manager.ListAsync(ParentSession)).ShouldHaveSingleItem();
        harness.ChildResponse.Task.IsCompleted.ShouldBeFalse();
        info.Status.ShouldBe(SubAgentStatus.Running);
        info.EffectiveMaxTurns.ShouldBe(7);
        info.EffectiveTimeoutSeconds.ShouldBe(120);
        using var json = JsonDocument.Parse(ReadText(result));
        json.RootElement.GetProperty("subAgentId").GetString().ShouldBe(info.SubAgentId);
        json.RootElement.GetProperty("sessionId").GetString().ShouldBe(info.ChildSessionId.Value);
        json.RootElement.GetProperty("conversationId").GetString().ShouldBe(info.ChildConversationId?.Value);
        ReadStatus(json.RootElement).ShouldBe(SubAgentStatus.Running);
        ReadText(result).ShouldNotContain(Summary);
    }

    [Theory]
    [InlineData("wait")]
    [InlineData("WAIT")]
    public async Task ManageTool_PrepareArguments_AcceptsWait(string action)
    {
        var tool = new SubAgentManageTool(Mock.Of<ISubAgentManager>(), ParentSession);
        var args = new Dictionary<string, object?> { ["subAgentId"] = "sub-4793", ["action"] = action };
        var prepared = await tool.PrepareArgumentsAsync(args);
        prepared["action"].ShouldBe(action);
    }

    [Fact]
    public void ManageTool_Definition_AdvertisesWait()
    {
        var tool = new SubAgentManageTool(Mock.Of<ISubAgentManager>(), ParentSession);
        var actions = tool.Definition.Parameters.GetProperty("properties").GetProperty("action")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray();
        actions.ShouldContain("wait");
    }

    [Fact]
    public async Task ManageTool_Wait_JoinsRunningChildAndReturnsTerminalResult()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        var execution = harness.ManageTool.ExecuteAsync("wait-4793", ManageArgs(info, "wait"));
        try
        {
            execution.IsCompleted.ShouldBeFalse("wait is a join, not a snapshot of Running");
        }
        finally
        {
            harness.CompleteChild();
        }

        var result = await execution.WaitAsync(Deadline);
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        ReadText(result).ShouldContain(Summary);
        using var json = JsonDocument.Parse(ReadText(result));
        ReadStatus(json.RootElement).ShouldBe(SubAgentStatus.Completed);
        json.RootElement.GetProperty("subAgentId").GetString().ShouldBe(info.SubAgentId);
        harness.Dispatcher.Verify(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManageTool_Wait_RejectsAnotherParentsChild()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        var foreignTool = new SubAgentManageTool(harness.Manager, SessionId.From("foreign-session"));
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            foreignTool.ExecuteAsync("foreign-wait", ManageArgs(info, "wait")));
        harness.ChildResponse.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task ManageTool_Wait_RejectsUnknownChild()
    {
        await using var harness = new Harness();
        await Should.ThrowAsync<KeyNotFoundException>(() => harness.ManageTool.ExecuteAsync("missing-wait",
            new Dictionary<string, object?> { ["subAgentId"] = "missing", ["action"] = "wait" }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletedChild_NeverDispatchesInboundTurn_AndRetainsLifecycleAndSummary(bool succeeds)
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        harness.ChildResponse.TrySetResult(new AgentResponse { Content = succeeds ? Summary : string.Empty });
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);

        var terminal = await harness.Manager.GetAsync(info.SubAgentId);
        terminal.ShouldNotBeNull();
        terminal.Status.ShouldBe(succeeds ? SubAgentStatus.Completed : SubAgentStatus.Failed);
        terminal.ResultSummary.ShouldNotBeNullOrWhiteSpace();
        var activity = await harness.TerminalPublished.Task.WaitAsync(Deadline);
        activity.Type.ShouldBe(succeeds ? GatewayActivityType.SubAgentCompleted : GatewayActivityType.SubAgentFailed);
        harness.Supervisor.Verify(s => s.StopAsync(It.IsAny<AgentId>(), info.ChildSessionId, It.IsAny<CancellationToken>()), Times.Once);
        // Assert after the entire run frame drains: absence is not inferred from a delay.
        harness.Dispatcher.Verify(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        var child = await harness.Store.GetAsync(info.ChildSessionId);
        child.ShouldNotBeNull();
        child.GetHistorySnapshot().ShouldContain(entry => entry.Role == MessageRole.Assistant);
    }

    [Theory]
    [InlineData("wait", "status")]
    [InlineData("status", "wait")]
    [InlineData("wait", "wait")]
    [InlineData("status", "status")]
    public async Task ManageTool_TerminalResult_IsConsumedOnceAcrossWaitAndStatus(string firstAction, string secondAction)
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        harness.CompleteChild();
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);

        var first = await harness.ManageTool.ExecuteAsync("consume-first", ManageArgs(info, firstAction)).WaitAsync(Deadline);
        ReadText(first).ShouldContain(Summary);
        var second = await harness.ManageTool.ExecuteAsync("consume-second", ManageArgs(info, secondAction)).WaitAsync(Deadline);
        ReadText(second).Contains(Summary, StringComparison.Ordinal)
            .ShouldBeFalse("a terminal result must not be delivered to the parent twice");
        using var json = JsonDocument.Parse(ReadText(second));
        json.RootElement.GetProperty("subAgentId").GetString().ShouldBe(info.SubAgentId);
        ReadStatus(json.RootElement).ShouldBe(SubAgentStatus.Completed);
        // Consumption removes delivery, not the child's auditable transcript.
        var child = await harness.Store.GetAsync(info.ChildSessionId);
        child.ShouldNotBeNull();
        child.GetHistorySnapshot().ShouldContain(entry => entry.Role == MessageRole.Assistant && entry.Content == Summary);
    }

    [Fact]
    public async Task SpawnTool_AwaitedResult_IsNotRedeliveredByStatus()
    {
        await using var harness = new Harness();
        var execution = harness.SpawnTool.ExecuteAsync("await-consume", new Dictionary<string, object?> { ["task"] = "investigate" });
        await harness.ChildStarted.Task.WaitAsync(Deadline);
        harness.CompleteChild();
        ReadText(await execution.WaitAsync(Deadline)).ShouldContain(Summary);
        var info = (await harness.Manager.ListAsync(ParentSession)).ShouldHaveSingleItem();
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        var status = await harness.ManageTool.ExecuteAsync("status-after-await", ManageArgs(info, "status"));
        ReadText(status).ShouldNotContain(Summary);
    }

    [Fact]
    public async Task ManageTool_CancelledWait_DoesNotConsumeEventualTerminalResult()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = harness.ManageTool.ExecuteAsync("cancelled-wait", ManageArgs(info, "wait"), cancellation.Token);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting.WaitAsync(Deadline));
        harness.CompleteChild();
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        var result = await harness.ManageTool.ExecuteAsync("wait-after-cancel", ManageArgs(info, "wait")).WaitAsync(Deadline);
        ReadText(result).ShouldContain(Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManageTool_ManagerRecreation_PreservesPendingOrConsumedTerminalResult(bool consumed)
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        harness.CompleteChild();
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        if (consumed)
        {
            var first = await harness.ManageTool.ExecuteAsync("consume-before-recreate", ManageArgs(info, "wait"));
            ReadText(first).ShouldContain(Summary);
        }

        // Fresh manager, same session store, no live record dictionary transferred.
        var recreated = harness.CreateManager();
        var tool = new SubAgentManageTool(recreated, ParentSession);
        var result = await tool.ExecuteAsync("wait-after-recreate", ManageArgs(info, "wait")).WaitAsync(Deadline);
        if (consumed)
            ReadText(result).ShouldNotContain(Summary);
        else
            ReadText(result).ShouldContain(Summary);
        using var json = JsonDocument.Parse(ReadText(result));
        json.RootElement.GetProperty("subAgentId").GetString().ShouldBe(info.SubAgentId);
        ReadStatus(json.RootElement).ShouldBe(SubAgentStatus.Completed);
    }

    [Fact]
    public async Task SameToolCallRetry_RetainsPayloadAndOneDurableParentResult()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        harness.CompleteChild();
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        var first = await harness.ManageTool.ExecuteAsync("same-call", ManageArgs(info, "wait"));
        var retry = await harness.ManageTool.ExecuteAsync("same-call", ManageArgs(info, "wait"));
        ReadText(retry).ShouldBe(ReadText(first));
        var parent = await harness.Store.GetAsync(ParentSession);
        parent.ShouldNotBeNull();
        parent.GetHistorySnapshot().Count(e => e.ToolCallId == "same-call" && e.Kind == MessageKind.ToolResult).ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentWaiters_OnlyOneReceivesSummary()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        var first = harness.ManageTool.ExecuteAsync("wait-one", ManageArgs(info, "wait"));
        var second = harness.ManageTool.ExecuteAsync("wait-two", ManageArgs(info, "wait"));
        harness.CompleteChild();
        var results = await Task.WhenAll(first, second).WaitAsync(Deadline);
        results.Count(r => ReadText(r).Contains(Summary, StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task ColdRunningRun_ReturnsInterruptedNotSuccessOrHang()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        var tool = new SubAgentManageTool(harness.CreateManager(), ParentSession);
        var result = await tool.ExecuteAsync("cold-wait", ManageArgs(info, "wait")).WaitAsync(Deadline);
        using var json = JsonDocument.Parse(ReadText(result));
        ReadStatus(json.RootElement).ShouldBe(SubAgentStatus.Failed);
        ReadText(result).ShouldContain("interrupted");
    }

    [Fact]
    public async Task ForegroundSpawn_RetryAfterManagerRecreation_ReturnsOriginalPayloadWithoutSecondChild()
    {
        await using var harness = new Harness();
        var args = new Dictionary<string, object?> { ["task"] = "investigate" };
        var first = harness.SpawnTool.ExecuteAsync("retry-spawn", args);
        await harness.ChildStarted.Task.WaitAsync(Deadline);
        harness.CompleteChild();
        var payload = ReadText(await first.WaitAsync(Deadline));
        var recreatedTool = new SubAgentSpawnTool(harness.CreateManager(), AgentId.From("parent-agent"), ParentSession,
            ConversationId.From("parent-conversation"));
        ReadText(await recreatedTool.ExecuteAsync("retry-spawn", args)).ShouldBe(payload);
        harness.Supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ObservationAndList_DoNotConsumeResult()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        harness.CompleteChild();
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        (await harness.Manager.GetAsync(info.SubAgentId)).ShouldNotBeNull().ResultSummary.ShouldBe(Summary);
        (await harness.Manager.ListAsync(ParentSession)).ShouldHaveSingleItem().ResultSummary.ShouldBe(Summary);
        var list = await new SubAgentListTool(harness.Manager, ParentSession).ExecuteAsync("list", new Dictionary<string, object?>());
        ReadText(list).ShouldNotContain(Summary);
        ReadText(await harness.ManageTool.ExecuteAsync("wait-after-list", ManageArgs(info, "wait"))).ShouldContain(Summary);
    }

    [Fact]
    public async Task DuplicateCompletion_RetainsFirstResultAndOneCleanup()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        await harness.Manager.OnCompletedAsync(info.SubAgentId, Summary);
        await harness.Manager.OnCompletedAsync(info.SubAgentId, "duplicate-result");
        harness.CompleteChild();
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        var result = ReadText(await harness.ManageTool.ExecuteAsync("duplicate-wait", ManageArgs(info, "wait")));
        result.ShouldContain(Summary);
        result.ShouldNotContain("duplicate-result");
        harness.Supervisor.Verify(s => s.StopAsync(It.IsAny<AgentId>(), info.ChildSessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrKilledRun_ReturnsUnsuccessfulTerminalResultOnce(bool kill)
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        if (kill) await harness.Manager.KillAsync(info.SubAgentId, ParentSession);
        else harness.ChildResponse.TrySetResult(new AgentResponse { Content = string.Empty });
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        var first = await harness.ManageTool.ExecuteAsync("terminal-first", ManageArgs(info, "wait"));
        using var json = JsonDocument.Parse(ReadText(first));
        ReadStatus(json.RootElement).ShouldBe(kill ? SubAgentStatus.Killed : SubAgentStatus.Failed);
        json.RootElement.GetProperty("resultSummary").GetString().ShouldNotBeNullOrWhiteSpace();
        var again = await harness.ManageTool.ExecuteAsync("terminal-second", ManageArgs(info, "status"));
        using var secondJson = JsonDocument.Parse(ReadText(again));
        secondJson.RootElement.GetProperty("alreadyConsumed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task TimeoutRun_WaitReturnsTimedOutDiagnosticAndRetainsDurableReceipt()
    {
        await using var harness = new Harness(timeoutSeconds: 1);
        var info = await harness.SpawnBackgroundAsync();
        var result = await harness.ManageTool.ExecuteAsync("timeout-wait", ManageArgs(info, "wait")).WaitAsync(Deadline);
        using var json = JsonDocument.Parse(ReadText(result));
        ReadStatus(json.RootElement).ShouldBe(SubAgentStatus.TimedOut);
        ReadText(result).ShouldContain("timed out");
        var parent = await harness.Store.GetAsync(ParentSession);
        parent.ShouldNotBeNull();
        parent.GetHistorySnapshot().ShouldContain(e => e.ToolCallId == "timeout-wait" && e.Kind == MessageKind.ToolResult);
    }

    [Fact]
    public async Task BoundedProjection_DoesNotTruncateChildTranscript()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        var text = new string('x', 5000);
        harness.ChildResponse.TrySetResult(new AgentResponse { Content = text });
        await harness.Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        var result = await harness.ManageTool.ExecuteAsync("bounded-wait", ManageArgs(info, "wait"));
        using var json = JsonDocument.Parse(ReadText(result));
        json.RootElement.GetProperty("resultSummary").GetString().ShouldNotBeNull().Length.ShouldBe(SubAgentRunDetail.MaxLongTextLength);
        var child = await harness.Store.GetAsync(info.ChildSessionId);
        child.ShouldNotBeNull();
        child.GetHistorySnapshot().ShouldContain(e => e.Role == MessageRole.Assistant && e.Content == text);
    }

    [Fact]
    public async Task MultipleChildren_EachHasIndependentOnceOnlyResult()
    {
        await using var harness = new Harness();
        var first = await harness.SpawnBackgroundAsync();
        await harness.SpawnTool.ExecuteAsync("spawn-second", new Dictionary<string, object?>
            { ["task"] = "second worker", ["background"] = true });
        var second = (await harness.Manager.ListAsync(ParentSession)).Single(i => i.SubAgentId != first.SubAgentId);
        harness.CompleteChild();
        var results = await Task.WhenAll(
            harness.ManageTool.ExecuteAsync("wait-first", ManageArgs(first, "wait")),
            harness.ManageTool.ExecuteAsync("wait-second", ManageArgs(second, "wait"))).WaitAsync(Deadline);
        results.ShouldAllBe(result => ReadText(result).Contains(Summary, StringComparison.Ordinal));
        ReadText(await harness.ManageTool.ExecuteAsync("again-first", ManageArgs(first, "wait"))).ShouldNotContain(Summary);
        ReadText(await harness.ManageTool.ExecuteAsync("again-second", ManageArgs(second, "wait"))).ShouldNotContain(Summary);
    }

    [Fact]
    public async Task Wait_PreparedTimeoutHint_ComesFromChildEffectiveBudget()
    {
        await using var harness = new Harness();
        var info = await harness.SpawnBackgroundAsync();
        var args = ManageArgs(info, "wait");
        args["subAgentWaitTimeoutSeconds"] = int.MaxValue;
        var prepared = await harness.ManageTool.PrepareArgumentsAsync(args);
        prepared["subAgentWaitTimeoutSeconds"].ShouldBe(info.EffectiveTimeoutSeconds ?? 600);
        harness.ManageTool.TimeoutArgument.ShouldNotBeNull();
        harness.SpawnTool.DefaultTimeout.ShouldBe(TimeSpan.FromSeconds(610));
        harness.SpawnTool.TimeoutArgument.ShouldNotBeNull();
    }

    private static Dictionary<string, object?> ManageArgs(SubAgentInfo info, string action)
        => new() { ["subAgentId"] = info.SubAgentId, ["action"] = action };

    private static string ReadText(AgentToolResult result)
        => result.Content[0].Value ?? throw new InvalidOperationException("Expected text tool result.");

    // Existing spawn and manage tools differ in enum serialization. Both encodings describe the
    // same contract; do not impose an unrelated serialization migration on #4793.
    private static SubAgentStatus ReadStatus(JsonElement element)
    {
        var status = element.GetProperty("status");
        return status.ValueKind == JsonValueKind.Number
            ? (SubAgentStatus)status.GetInt32()
            : Enum.Parse<SubAgentStatus>(status.GetString() ?? throw new InvalidOperationException("Missing status."), true);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public TaskCompletionSource<bool> ChildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SpawnPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AgentResponse> ChildResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<GatewayActivity> TerminalPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InMemorySessionStore Store { get; } = new();
        public InMemoryConversationStore Conversations { get; } = new();
        public Mock<IChannelDispatcher> Dispatcher { get; } = new();
        public Mock<IAgentSupervisor> Supervisor { get; } = new();
        private readonly Mock<IAgentRegistry> _registry = new();
        private readonly Mock<IActivityBroadcaster> _activity = new();
        public DefaultSubAgentManager Manager { get; }
        public SubAgentSpawnTool SpawnTool { get; }
        public SubAgentManageTool ManageTool { get; }

        private readonly int _timeoutSeconds;

        public Harness(int timeoutSeconds = 600)
        {
            _timeoutSeconds = timeoutSeconds;
            // A receipt must attach to an existing, owned parent session.
            var parent = Store.GetOrCreateAsync(ParentSession, AgentId.From("parent-agent")).GetAwaiter().GetResult();
            parent.ConversationId = ConversationId.From("parent-conversation");
            var handle = new Mock<IAgentHandle>();
            handle.SetupGet(h => h.AgentId).Returns(AgentId.From("child-agent"));
            handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
            handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((_, ct) =>
                {
                    ChildStarted.TrySetResult(true);
                    return ChildResponse.Task.WaitAsync(ct);
                });
            Supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(handle.Object);
            Supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"), DisplayName = "Parent", ModelId = "gpt-5-mini", ApiProvider = "copilot"
            });
            Dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _activity.Setup(a => a.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
                .Callback<GatewayActivity, CancellationToken>((activity, _) =>
                {
                    if (activity.Type == GatewayActivityType.SubAgentSpawned)
                        SpawnPublished.TrySetResult(true);
                    if (activity.Type is GatewayActivityType.SubAgentCompleted or GatewayActivityType.SubAgentFailed)
                        TerminalPublished.TrySetResult(activity);
                }).Returns(ValueTask.CompletedTask);
            Manager = CreateManager();
            SpawnTool = new SubAgentSpawnTool(Manager, AgentId.From("parent-agent"), ParentSession, ConversationId.From("parent-conversation"));
            ManageTool = new SubAgentManageTool(Manager, ParentSession);
        }

        public DefaultSubAgentManager CreateManager() => new(
            Supervisor.Object, _registry.Object, _activity.Object, Dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions { SubAgents = new SubAgentOptions { MaxTimeoutSeconds = _timeoutSeconds, DefaultTimeoutSeconds = _timeoutSeconds } }), NullLogger<DefaultSubAgentManager>.Instance,
            sessionStore: Store, conversationStore: Conversations);

        public async Task<SubAgentInfo> SpawnBackgroundAsync()
        {
            await SpawnTool.ExecuteAsync("spawn-4793", new Dictionary<string, object?>
            {
                ["task"] = "investigate", ["background"] = true
            }).WaitAsync(Deadline);
            await ChildStarted.Task.WaitAsync(Deadline);
            return (await Manager.ListAsync(ParentSession)).ShouldHaveSingleItem();
        }

        public void CompleteChild() => ChildResponse.TrySetResult(new AgentResponse { Content = Summary });

        public async ValueTask DisposeAsync()
        {
            CompleteChild();
            foreach (var info in await Manager.ListAsync(ParentSession))
                await Manager.WaitForRunCompletionForTestAsync(info.SubAgentId).WaitAsync(Deadline);
        }
    }
}
