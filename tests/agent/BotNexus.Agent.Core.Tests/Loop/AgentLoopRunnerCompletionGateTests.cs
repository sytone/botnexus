using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

[Collection(ApiProviderRegistryCollection.Name)]
public sealed class AgentLoopRunnerCompletionGateTests
{
    [Fact]
    public async Task OpenChecklist_ContinuesSameRunUntilEvaluatorReportsComplete()
    {
        var providerCalls = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "completion-gate-continues",
            simpleStreamFactory: (_, _, _) => TestStreamFactory.CreateTextResponse(
                Interlocked.Increment(ref providerCalls) == 1 ? "progress" : "complete")));
        var evaluations = 0;
        var events = new List<AgentEvent>();
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel("completion-gate-continues")) with
        {
            EvaluateRunCompletion = _ => Task.FromResult(
                Interlocked.Increment(ref evaluations) == 1
                    ? RunCompletionDecision.Continue(["publish"], "Publication is still actionable.")
                    : RunCompletionDecision.Completed),
            MaxCompletionContinuations = 2,
        };

        var produced = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("deliver")],
            new AgentContext(null, [], []),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        providerCalls.ShouldBe(2);
        produced.OfType<AssistantAgentMessage>().Select(message => message.Content)
            .ShouldBe(["progress", "complete"]);
        produced.OfType<AgentUserMessage>().ShouldContain(message =>
            message.Content.Contains("publish", StringComparison.Ordinal));
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status
            .ShouldBe(RunCompletionStatus.Completed);
    }

    [Fact]
    public async Task OpenChecklist_AtContinuationBoundEndsAsIncompleteNotCompleted()
    {
        var providerCalls = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "completion-gate-bounded",
            simpleStreamFactory: (_, _, _) =>
            {
                Interlocked.Increment(ref providerCalls);
                return TestStreamFactory.CreateTextResponse("still only progress");
            }));
        var events = new List<AgentEvent>();
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel("completion-gate-bounded")) with
        {
            EvaluateRunCompletion = _ => Task.FromResult(
                RunCompletionDecision.Continue(["implement", "validate"], "Work remains actionable.")),
            MaxCompletionContinuations = 2,
        };

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("deliver")],
            new AgentContext(null, [], []),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        providerCalls.ShouldBe(3);
        var completion = events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion;
        completion.Status.ShouldBe(RunCompletionStatus.IncompleteWithoutStopReason);
        completion.OpenItemIds.ShouldBe(["implement", "validate"]);
        completion.ContinuationAttempts.ShouldBe(2);
    }

    [Fact]
    public async Task ParkedChecklist_EndsWithoutAutomaticContinuation()
    {
        var providerCalls = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "completion-gate-parked",
            simpleStreamFactory: (_, _, _) =>
            {
                Interlocked.Increment(ref providerCalls);
                return TestStreamFactory.CreateTextResponse("waiting");
            }));
        var events = new List<AgentEvent>();
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel("completion-gate-parked")) with
        {
            EvaluateRunCompletion = _ => Task.FromResult(RunCompletionDecision.Parked(
                RunStopReason.UserInput,
                ["decision"],
                "ask_user request ask-1 is persisted",
                "user",
                "ask_user response")),
        };

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("deliver")],
            new AgentContext(null, [], []),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        providerCalls.ShouldBe(1);
        var completion = events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion;
        completion.Status.ShouldBe(RunCompletionStatus.Parked);
        completion.StopReason.ShouldBe(RunStopReason.UserInput);
        completion.OpenItemIds.ShouldBe(["decision"]);
    }

    [Theory]
    [InlineData(null, "persisted approval", "user", "user approves")]
    [InlineData(RunStopReason.Approval, "", "user", "user approves")]
    [InlineData(RunStopReason.Approval, "persisted approval", "", "user approves")]
    [InlineData(RunStopReason.Approval, "persisted approval", "user", "")]
    public async Task MalformedParkedDisposition_ContinuesInsteadOfEndingParked(
        RunStopReason? stopReason,
        string evidence,
        string continuationOwner,
        string wakeCondition)
    {
        var providerCalls = 0;
        var providerName = $"completion-gate-invalid-park-{Guid.NewGuid():N}";
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            providerName,
            simpleStreamFactory: (_, _, _) =>
            {
                Interlocked.Increment(ref providerCalls);
                return TestStreamFactory.CreateTextResponse("claimed blocker");
            }));
        var events = new List<AgentEvent>();
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(providerName)) with
        {
            EvaluateRunCompletion = _ => Task.FromResult(new RunCompletionDecision(
                RunCompletionStatus.Parked,
                ["publish"],
                stopReason,
                Evidence: evidence,
                ContinuationOwner: continuationOwner,
                WakeCondition: wakeCondition)),
            MaxCompletionContinuations = 1,
        };

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("deliver")],
            new AgentContext(null, [], []),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        providerCalls.ShouldBe(2);
        var completion = events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion;
        completion.Status.ShouldBe(RunCompletionStatus.IncompleteWithoutStopReason);
        completion.OpenItemIds.ShouldBe(["publish"]);
        completion.ContinuationAttempts.ShouldBe(1);
    }

    [Theory]
    [InlineData(RunStopReason.UserInput)]
    [InlineData(RunStopReason.Approval)]
    [InlineData(RunStopReason.ExternalBlocker)]
    [InlineData(RunStopReason.Cancellation)]
    [InlineData(RunStopReason.SafetyBoundary)]
    [InlineData(RunStopReason.DurableAsyncWait)]
    public async Task StructuredParkedDisposition_AcceptsEveryBoundedStopReason(RunStopReason stopReason)
    {
        var providerName = $"completion-gate-valid-park-{Guid.NewGuid():N}";
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            providerName,
            simpleStreamFactory: (_, _, _) => TestStreamFactory.CreateTextResponse("waiting")));
        var events = new List<AgentEvent>();
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(providerName)) with
        {
            EvaluateRunCompletion = _ => Task.FromResult(RunCompletionDecision.Parked(
                stopReason,
                ["publish"],
                "authoritative persisted evidence",
                "runtime",
                "durable wake signal")),
        };

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("deliver")],
            new AgentContext(null, [], []),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        var completion = events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion;
        completion.Status.ShouldBe(RunCompletionStatus.Parked);
        completion.StopReason.ShouldBe(stopReason);
    }

    [Fact]
    public async Task ProviderError_BypassesChecklistContinuationAndEndsFailed()
    {
        var providerCalls = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "completion-gate-error",
            simpleStreamFactory: (_, _, _) =>
            {
                Interlocked.Increment(ref providerCalls);
                return TestStreamFactory.CreateErrorResponse("provider failed");
            }));
        var events = new List<AgentEvent>();
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel("completion-gate-error")) with
        {
            EvaluateRunCompletion = _ => Task.FromResult(
                RunCompletionDecision.Continue(["publish"], "Work remains actionable.")),
        };

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("deliver")],
            new AgentContext(null, [], []),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        providerCalls.ShouldBe(1);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status
            .ShouldBe(RunCompletionStatus.Failed);
    }

    [Fact]
    public async Task NoCompletionEvaluator_RetainsSimpleRunBehavior()
    {
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "completion-gate-absent",
            simpleStreamFactory: (_, _, _) => TestStreamFactory.CreateTextResponse("done")));
        var events = new List<AgentEvent>();
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel("completion-gate-absent"));

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("simple")],
            new AgentContext(null, [], []),
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        events.OfType<AgentEndEvent>().ShouldHaveSingleItem().Completion.Status
            .ShouldBe(RunCompletionStatus.Completed);
    }
}
