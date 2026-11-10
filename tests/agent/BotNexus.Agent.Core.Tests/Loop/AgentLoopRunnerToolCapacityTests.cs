using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.ExtensionPoints.Messages;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using ProviderUserMessage = BotNexus.Agent.Providers.Core.Models.UserMessage;

namespace BotNexus.Agent.Core.Tests.Loop;

[Collection(ApiProviderRegistryCollection.Name)]
public sealed class AgentLoopRunnerToolCapacityTests
{
    private sealed class ReceiptTool : IAgentTool
    {
        public int Calls;
        public string Name => "capacity_receipt";
        public string Label => Name;
        public Tool Definition => new(Name, "receipt", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone());
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, toolCallId)]));
        }
    }

    // This assertion uses only baseline APIs and is the intended executable RED:
    // baseline requests contain no [Live tool capacity] section.
    [Fact]
    public async Task Request_DefaultCapacityAppearsOnceWithoutChangingSystemOrTranscript()
    {
        const string api = "capacity-default";
        var requests = new List<Context>();
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
        {
            requests.Add(context);
            return TestStreamFactory.CreateTextResponse("done");
        }));
        var messages = await AgentLoopRunner.RunAsync([new AgentUserMessage("work")], new AgentContext("stable prefix", [], []),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)), _ => Task.CompletedTask, CancellationToken.None);
        var request = requests.ShouldHaveSingleItem();
        request.SystemPrompt.ShouldBe("stable prefix");
        var section = request.Messages.OfType<ProviderUserMessage>().Where(m => m.Content.Text?.StartsWith("[Live tool capacity]", StringComparison.Ordinal) == true).ShouldHaveSingleItem();
        section.Content.Text.ShouldNotBeNull().ShouldContain("used=0; limit=128; remaining=128");
        messages.OfType<AgentUserMessage>().ShouldNotContain(m => m.Content.StartsWith("[Live tool capacity]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(7, 8)]
    [InlineData(20, 20)]
    public async Task Guard_ThresholdsAreOnceOnlyAndSnapshotSurvivesReset(int limit, int results)
    {
        var observations = new List<ToolCapacityObservation>();
        var id = AgentRunId.From("capacity-threshold");
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.Progress),
            maxToolResults: limit, agentRunId: id, capacityObserver: observations.Add);
        var initial = guard.Capacity;
        initial.Used.ShouldBe(0L);
        initial.Remaining.ShouldBe(limit);
        var result = new ToolResultAgentMessage("unmatched", "receipt", new AgentToolResult([]));
        for (var i = 0; i < results; i++)
        {
            await guard.ObserveAsync([], [result], CancellationToken.None);
            guard.Reset();
        }
        initial.Used.ShouldBe(0L);
        guard.Capacity.Used.ShouldBe((long)results);
        guard.Capacity.Remaining.ShouldBe(0L);
        guard.Capacity.AgentRunId.ShouldBe(id);
        observations.Select(o => o.Stage).ShouldBe(new[] { ToolCapacityStage.Start, ToolCapacityStage.SeventyFivePercent, ToolCapacityStage.NinetyPercent, ToolCapacityStage.Exhausted });
        observations[1].Capacity.Used.ShouldBe((limit * 75L + 99) / 100);
        observations[2].Capacity.Used.ShouldBe((limit * 90L + 99) / 100);
    }

    [Fact]
    public async Task Guard_OvershootPublishesAllCrossingsFromOneAuthoritativeBatch()
    {
        var observations = new List<ToolCapacityObservation>();
        var guard = new ToolNonProgressGuard((_, _) => throw new InvalidOperationException("hard edge must bypass policy"),
            maxToolResults: 2, capacityObserver: observations.Add);
        var result = new ToolResultAgentMessage("unmatched", "receipt", new AgentToolResult([]));
        await guard.ObserveAsync([], [result, result, result], CancellationToken.None);
        guard.Capacity.Used.ShouldBe(3L);
        guard.Capacity.Remaining.ShouldBe(0L);
        observations.Count.ShouldBe(4);
        observations.Skip(1).ShouldAllBe(o => o.Capacity.Used == 3L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requests_CompactionAndFinalizerProjectFreshCapacityAfterTransformations(bool compact)
    {
        var api = "capacity-pipeline-" + compact;
        var tool = new ReceiptTool();
        var requests = new List<Context>();
        var snapshots = new List<ToolCapacitySnapshot>();
        var transformerInputs = new List<AgentMessage>();
        var providerInputs = new List<AgentMessage>();
        var compactions = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
        {
            requests.Add(context);
            return (context.Tools?.Count ?? 0) == 0 ? TestStreamFactory.CreateTextResponse("summary")
                : TestStreamFactory.CreateToolCallResponse(($"call-{requests.Count}", tool.Name, new Dictionary<string, object?>()));
        }));
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            MaxToolResults = 2,
            AgentContextTransformer = (messages, _) =>
            {
                transformerInputs.AddRange(messages);
                return Task.FromResult<IReadOnlyList<AgentMessage>>(messages.ToArray());
            },
            ProviderMessageTransformer = async (messages, token) =>
            {
                providerInputs.AddRange(messages);
                return await DefaultProviderMessageTransformer.TransformAsync(messages, token);
            },
            ProviderRequestContextProvider = (context, _) =>
            {
                snapshots.Add(context.ToolCapacity);
                return Task.FromResult<string?>($"custom-capacity:{context.ToolCapacity.Used}:{context.ToolCapacity.Phase}");
            },
            ContextCompactionService = compact ? _ => Task.FromResult<AgentContext?>(++compactions == 2 ? new AgentContext("stable", [new AgentUserMessage("compacted")], [tool]) : null) : null
        };
        for (var run = 0; run < 2; run++)
        {
            var messages = await AgentLoopRunner.RunAsync([new AgentUserMessage("work")], new AgentContext("stable", [], [tool]), config, _ => Task.CompletedTask, CancellationToken.None);
            messages.OfType<AgentUserMessage>().ShouldNotContain(m => m.Content.StartsWith("custom-capacity:", StringComparison.Ordinal));
        }
        snapshots.Select(s => s.Used).ShouldBe(new long[] { 0, 1, 2, 0, 1, 2 });
        snapshots.Where(s => s.Used == 2).ShouldAllBe(s => s.Phase == ToolCapacityPhase.Finalizing);
        snapshots[0].AgentRunId.ShouldNotBe(snapshots[3].AgentRunId);
        requests.ShouldAllBe(r => r.SystemPrompt == "stable");
        foreach (var request in requests)
            request.Messages.OfType<ProviderUserMessage>().Count(m => m.Content.Text?.StartsWith("custom-capacity:", StringComparison.Ordinal) == true).ShouldBe(1);
        transformerInputs.Concat(providerInputs).OfType<AgentUserMessage>().ShouldNotContain(m => m.Content.StartsWith("custom-capacity:", StringComparison.Ordinal));
        tool.Calls.ShouldBe(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renderer_FailureOrCancellationPreventsNormalProviderInvocation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var config = TestHelpers.CreateTestConfig() with
        {
            ProviderRequestContextProvider = (_, _) =>
            {
                if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                throw new InvalidOperationException("renderer failed");
            }
        };
        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => AgentLoopRunner.RunAsync([new AgentUserMessage("work")], new AgentContext(null, [], []), config, _ => Task.CompletedTask, cancellation.Token));
        else
            await Should.ThrowAsync<InvalidOperationException>(() => AgentLoopRunner.RunAsync([new AgentUserMessage("work")], new AgentContext(null, [], []), config, _ => Task.CompletedTask, cancellation.Token));
    }

    [Fact]
    public async Task Guard_InformationalSubscriberFailuresDoNotStopLaterSubscribersOrCounting()
    {
        var seen = new List<ToolCapacityObservation>();
        ToolCapacityObserver observer = _ => throw new OperationCanceledException("subscriber only");
        observer += seen.Add;
        var guard = new ToolNonProgressGuard((_, _) => Task.FromResult<ToolProgressDecision?>(null), maxToolResults: 1, capacityObserver: observer);
        await guard.ObserveAsync([], [new ToolResultAgentMessage("one", "receipt", new AgentToolResult([]))], CancellationToken.None);
        seen.Count.ShouldBe(4);
        guard.Capacity.Used.ShouldBe(1L);
    }

    [Theory]
    [InlineData("overflow")]
    [InlineData("authentication")]
    [InlineData("transient")]
    public async Task Requests_RetryRefreshesOneSectionWithoutSpendingResults(string recovery)
    {
        var api = "capacity-retry-" + recovery;
        var tool = new ReceiptTool();
        var requests = new List<Context>();
        var snapshots = new List<ToolCapacitySnapshot>();
        var observations = new List<ToolCapacityObservation>();
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
        {
            requests.Add(context);
            if (requests.Count == 1)
                return TestStreamFactory.CreateToolCallResponse(("one", tool.Name, new Dictionary<string, object?>()));
            if (requests.Count == 2)
            {
                if (recovery == "overflow") return TestStreamFactory.CreateErrorResponse("input is too long for requested model");
                if (recovery == "authentication") throw new BotNexus.Agent.Providers.Core.ProviderAuthenticationException("rejected", 401, "test-provider");
                throw new BotNexus.Agent.Providers.Core.ProviderRateLimitException("retry", 429, TimeSpan.FromMilliseconds(1));
            }
            return TestStreamFactory.CreateTextResponse("done");
        }));
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            MaxToolResults = 4,
            CredentialInvalidationService = (_, _) => Task.CompletedTask,
            ToolCapacityObserver = observations.Add,
            ProviderRequestContextProvider = (context, token) =>
            {
                snapshots.Add(context.ToolCapacity);
                return DefaultProviderRequestContextProvider.RenderAsync(context, token);
            }
        };
        var messages = await AgentLoopRunner.RunAsync([new AgentUserMessage("work")], new AgentContext("stable", [], [tool]), config, _ => Task.CompletedTask, CancellationToken.None);
        snapshots.Select(s => s.Used).ShouldBe(new long[] { 0, 1, 1 });
        observations.ShouldHaveSingleItem().Stage.ShouldBe(ToolCapacityStage.Start);
        tool.Calls.ShouldBe(1);
        messages.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem();
        foreach (var request in requests)
        {
            request.SystemPrompt.ShouldBe("stable");
            request.Messages.OfType<ProviderUserMessage>().Count(m => m.Content.Text?.StartsWith("[Live tool capacity]", StringComparison.Ordinal) == true).ShouldBe(1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finalizer_RendererFailureOrCancellationCannotOverwriteBudgetParked(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var api = "capacity-finalizer-failure-" + cancel;
        var tool = new ReceiptTool();
        var requests = 0;
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
        {
            requests++;
            return TestStreamFactory.CreateToolCallResponse(("one", tool.Name, new Dictionary<string, object?>()));
        }));
        var events = new List<AgentEvent>();
        await AgentLoopRunner.RunAsync([new AgentUserMessage("work")], new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
            {
                MaxToolResults = 1,
                ProviderRequestContextProvider = (context, token) =>
                {
                    if (context.ToolCapacity.Phase != ToolCapacityPhase.Finalizing)
                        return DefaultProviderRequestContextProvider.RenderAsync(context, token);
                    if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                    throw new InvalidOperationException("finalizer renderer failed");
                }
            }, evt => { events.Add(evt); return Task.CompletedTask; }, cancellation.Token);
        requests.ShouldBe(1);
        tool.Calls.ShouldBe(1);
        var end = events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
        end.Completion.Status.ShouldBe(BotNexus.Agent.Core.ExtensionPoints.RunCompletion.RunCompletionStatus.Parked);
        end.Completion.StopReason.ShouldBe(BotNexus.Agent.Core.ExtensionPoints.RunCompletion.RunStopReason.ToolResultBudget);
        end.Completion.ToolResultBudget.ShouldNotBeNull().ExecutedResults.ShouldBe(1L);
    }

    [Fact]
    public async Task Runs_ConcurrentParentAndChildHaveSeparateAccountingAndObservationState()
    {
        const string api = "capacity-concurrent";
        var tool = new ReceiptTool();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var snapshots = new System.Collections.Concurrent.ConcurrentBag<ToolCapacitySnapshot>();
        var observations = new System.Collections.Concurrent.ConcurrentBag<ToolCapacityObservation>();
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
            (context.Tools?.Count ?? 0) == 0 ? TestStreamFactory.CreateTextResponse("summary")
                : TestStreamFactory.CreateToolCallResponse(("one", tool.Name, new Dictionary<string, object?>()))));
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            MaxToolResults = 1,
            ToolCapacityObserver = observations.Add,
            ProviderRequestContextProvider = async (context, token) =>
            {
                snapshots.Add(context.ToolCapacity);
                if (context.ToolCapacity.Used == 0)
                {
                    if (Interlocked.Increment(ref starts) == 2) arrived.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return await DefaultProviderRequestContextProvider.RenderAsync(context, token);
            }
        };
        var parentId = AgentRunId.From("capacity-parent");
        var childId = AgentRunId.From("capacity-child");
        var parent = AgentLoopRunner.RunAsync([new AgentUserMessage("parent")], new AgentContext(null, [], [tool]), config with { AgentRunId = parentId }, _ => Task.CompletedTask, CancellationToken.None);
        var child = AgentLoopRunner.RunAsync([new AgentUserMessage("child")], new AgentContext(null, [], [tool]), config with { AgentRunId = childId }, _ => Task.CompletedTask, CancellationToken.None);
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            snapshots.Count.ShouldBe(2);
            snapshots.ShouldAllBe(s => s.Used == 0);
            release.TrySetResult();
            await Task.WhenAll(parent, child).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var id in new[] { parentId, childId })
            {
                snapshots.Where(s => s.AgentRunId == id).Select(s => s.Used).Order().ShouldBe(new long[] { 0, 1 });
                observations.Where(o => o.Capacity.AgentRunId == id).Count().ShouldBe(4);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(0, "Use tools deliberately")]
    [InlineData(14, "Use tools deliberately")]
    [InlineData(15, "running low")]
    [InlineData(17, "running low")]
    [InlineData(18, "nearly exhausted")]
    [InlineData(20, "exhausted. Summarize")]
    [InlineData(23, "exhausted. Summarize")]
    public async Task Renderer_DefaultGuidanceChangesAtExactMilestones(long used, string guidance)
    {
        var snapshot = new ToolCapacitySnapshot(AgentRunId.From("renderer-guidance"), used, 20,
            used >= 20 ? ToolCapacityPhase.Finalizing : ToolCapacityPhase.Executing);
        var text = (await DefaultProviderRequestContextProvider.RenderAsync(new ProviderRequestContext(snapshot), CancellationToken.None)).ShouldNotBeNull();
        text.ShouldContain(guidance);
        text.ShouldContain($"used={used}; limit=20; remaining={Math.Max(0L, 20 - used)}");
    }

    [Fact]
    public async Task AgentOptions_ForwardsTypedCapacityCallbacksWithoutPersistingProjection()
    {
        const string api = "capacity-agent-options";
        var snapshots = new List<ToolCapacitySnapshot>();
        var observations = new List<ToolCapacityObservation>();
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
        {
            context.Messages.OfType<ProviderUserMessage>().Count(m => m.Content.Text?.StartsWith("options-capacity:", StringComparison.Ordinal) == true).ShouldBe(1);
            return TestStreamFactory.CreateTextResponse("done");
        }));
        var model = TestHelpers.CreateTestModel(api);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(new AgentInitialState(Model: model, Messages: [], Tools: []), model) with
        {
            MaxToolResults = 7,
            ToolCapacityObserver = observations.Add,
            ProviderRequestContextProvider = (context, _) =>
            {
                snapshots.Add(context.ToolCapacity);
                return Task.FromResult<string?>($"options-capacity:{context.ToolCapacity.Used}:{context.ToolCapacity.Limit}");
            }
        });
        await agent.PromptAsync("work");
        snapshots.ShouldHaveSingleItem().Limit.ShouldBe(7);
        observations.ShouldHaveSingleItem().Capacity.ShouldBe(snapshots[0]);
        agent.State.Messages.OfType<AgentUserMessage>().ShouldNotContain(m => m.Content.StartsWith("options-capacity:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requests_ParallelErrorsAndMilestonesUseFullBatchAndBoundedProjection()
    {
        const string api = "capacity-parallel-milestones";
        var requests = new List<Context>();
        var observations = new List<ToolCapacityObservation>();
        using var registration = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
        {
            requests.Add(context);
            if ((context.Tools?.Count ?? 0) == 0) return TestStreamFactory.CreateTextResponse("summary");
            var batch = requests.Count == 1 ? 15 : requests.Count == 2 ? 3 : 4;
            // Missing tools return errors but are still executed results for the existing guard.
            return TestStreamFactory.CreateToolCallResponse(Enumerable.Range(0, batch).Select(i => ($"batch-{requests.Count}-{i}", "missing_capacity_tool", new Dictionary<string, object?>())).ToArray());
        }));
        var messages = await AgentLoopRunner.RunAsync([new AgentUserMessage("work")], new AgentContext("stable", [], [new ReceiptTool()]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
            {
                MaxToolResults = 20,
                ToolExecutionMode = ToolExecutionMode.Parallel,
                ToolProgressPolicy = (_, _) => Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.Neutral),
                ToolCapacityObserver = observations.Add
            }, _ => Task.CompletedTask, CancellationToken.None);
        observations.Select(o => o.Stage).ShouldBe(new[] { ToolCapacityStage.Start, ToolCapacityStage.SeventyFivePercent, ToolCapacityStage.NinetyPercent, ToolCapacityStage.Exhausted });
        observations.Select(o => o.Capacity.Used).ShouldBe(new long[] { 0, 15, 18, 22 });
        messages.OfType<ToolResultAgentMessage>().Count().ShouldBe(22);
        messages.OfType<ToolResultAgentMessage>().ShouldAllBe(m => m.IsError);
        var sections = requests.Select(request => request.Messages.OfType<ProviderUserMessage>().Where(m => m.Content.Text?.StartsWith("[Live tool capacity]", StringComparison.Ordinal) == true).ShouldHaveSingleItem().Content.Text.ShouldNotBeNull()).ToArray();
        sections.Length.ShouldBe(4);
        sections[0].ShouldContain("used=0; limit=20; remaining=20");
        sections[1].ShouldContain("used=15; limit=20; remaining=5");
        sections[1].ShouldContain("running low");
        sections[2].ShouldContain("used=18; limit=20; remaining=2");
        sections[2].ShouldContain("nearly exhausted");
        sections[3].ShouldContain("used=22; limit=20; remaining=0");
        sections.ShouldAllBe(section => section.Length < 650);
    }
}
