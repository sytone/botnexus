using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Streaming;
using System.Reflection;

namespace BotNexus.Agent.Core.Tests;

public class AgentTests
{
    [Fact]
    public void Constructor_InitializesFromAgentOptions()
    {
        var model = TestHelpers.CreateTestModel();
        var initialState = new AgentInitialState(
            SystemPrompt: "Be concise",
            Model: model,
            Tools: [new CalculateTool()],
            Messages: [new UserMessage("existing")]);
        var options = TestHelpers.CreateTestOptions(initialState, model);

        var agent = new BotNexus.Agent.Core.Agent(options);

        agent.ShouldNotBeNull();
        agent.Status.ShouldBe(AgentStatus.Idle);
    }

    [Fact]
    public void State_ExposesInitialValues()
    {
        var model = TestHelpers.CreateTestModel();
        var initialState = new AgentInitialState(
            SystemPrompt: "System prompt",
            Model: model,
            Tools: [new CalculateTool()],
            Messages: [new UserMessage("history")]);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(initialState, model));

        agent.State.SystemPrompt.ShouldBe("System prompt");
        agent.State.Model.ShouldBe(model);
        agent.State.Tools.ShouldHaveSingleItem().Name.ShouldBe("calculate");
        agent.State.Messages.OfType<UserMessage>().ShouldHaveSingleItem().Content.ShouldBe("history");
    }

    [Fact]
    public void Reset_ClearsRuntimeState()
    {
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions());
        agent.State.Messages = [new UserMessage("history")];
        agent.State.SetErrorMessage("error");
        agent.State.SetStreamingMessage(new AssistantAgentMessage("streaming"));
        agent.State.SetPendingToolCalls(["tool-1"]);

        agent.Reset();

        agent.State.Messages.ShouldBeEmpty();
        agent.State.ErrorMessage.ShouldBeNull();
        agent.State.StreamingMessage.ShouldBeNull();
        agent.State.PendingToolCalls.ShouldBeEmpty();
        agent.Status.ShouldBe(AgentStatus.Idle);
    }

    [Fact]
    public async Task Subscribe_ReturnsDisposableThatUnsubscribes()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        var callbackCount = 0;
        var subscription = agent.Subscribe((_, _) =>
        {
            Interlocked.Increment(ref callbackCount);
            return Task.CompletedTask;
        });

        await agent.PromptAsync("first");
        callbackCount.ShouldBeGreaterThan(0);

        var firstRunCount = callbackCount;
        subscription.Dispose();
        await agent.PromptAsync("second");

        callbackCount.ShouldBe(firstRunCount);
    }

    [Fact]
    public async Task PromptAsync_WhenAlreadyRunning_ThrowsInvalidOperationException()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(release);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        var firstRun = agent.PromptAsync("first");
        var started = SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(10));
        started.ShouldBeTrue();

        Func<Task> secondPrompt = () => agent.PromptAsync("second");
        var ex = await secondPrompt.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldBe("Agent is already running.");

        release.TrySetResult();
        await firstRun;
    }

    [Fact]
    public async Task SteerAndFollowUp_EnqueueMessagesThatAreConsumedByRun()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        agent.Steer(new UserMessage("steer message"));
        agent.FollowUp(new UserMessage("follow-up message"));

        var produced = await agent.PromptAsync("base prompt");

        var contents = produced.OfType<UserMessage>().Select(message => message.Content);
        contents.ShouldContain("base prompt");
        contents.ShouldContain("steer message");
        contents.ShouldContain("follow-up message");
    }

    [Fact]
    public async Task ContinueAsync_WhenSteeringAndFollowUpQueued_DrainsBothInSameRun()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        await agent.PromptAsync("seed");

        agent.Steer(new UserMessage("steer message"));
        agent.FollowUp(new UserMessage("follow-up message"));

        var firstContinue = await agent.ContinueAsync();
        var firstContents = firstContinue.OfType<UserMessage>().Select(message => message.Content);
        firstContents.ShouldContain("steer message");
        firstContents.ShouldContain("follow-up message");
    }

    [Fact]
    public async Task ClearAllQueues_RemovesPendingMessages()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        agent.Steer(new UserMessage("steer message"));
        agent.FollowUp(new UserMessage("follow-up message"));
        agent.ClearAllQueues();

        var produced = await agent.PromptAsync("base prompt");

        produced.OfType<UserMessage>().Select(message => message.Content)
            .ShouldBe(new[] { "base prompt" });
    }

    [Fact]
    public async Task HasQueuedMessages_WhenQueuedAndThenDrained_ReflectsCurrentQueueState()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        agent.Steer(new UserMessage("steer message"));

        agent.HasQueuedMessages.ShouldBeTrue();

        _ = await agent.PromptAsync("base prompt");
        agent.HasQueuedMessages.ShouldBeFalse();
    }

    [Fact]
    public void SteeringMode_WhenChangedAtRuntime_ChangesDrainBehavior()
    {
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(
            model: TestHelpers.CreateTestModel("test-api"),
            steeringMode: QueueMode.All));
        agent.Steer(new UserMessage("steer one"));
        agent.Steer(new UserMessage("steer two"));
        agent.SteeringMode = QueueMode.OneAtATime;

        var drainMethod = typeof(BotNexus.Agent.Core.Agent).GetMethod("DrainQueuedMessages", BindingFlags.NonPublic | BindingFlags.Instance);
        drainMethod.ShouldNotBeNull();
        var firstDrain = (IReadOnlyList<AgentMessage>)drainMethod!.Invoke(agent, null)!;
        firstDrain.ShouldHaveSingleItem();

        agent.Steer(new UserMessage("steer three"));
        agent.Steer(new UserMessage("steer four"));

        agent.SteeringMode = QueueMode.All;
        var secondDrain = (IReadOnlyList<AgentMessage>)drainMethod.Invoke(agent, null)!;
        secondDrain.Count().ShouldBe(3);
    }

    [Fact]
    public async Task PromptAsync_WhenRunFails_AddsSyntheticErrorAssistantMessageAndEmitsAgentEnd()
    {
        using var provider = TestHelpers.RegisterProvider(
            new TestApiProvider(
                "test-api",
                simpleStreamFactory: (_, _, _) => throw new InvalidOperationException("provider exploded")));
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        AgentEndEvent? agentEnd = null;
        using var subscription = agent.Subscribe((@event, _) =>
        {
            if (@event is AgentEndEvent endEvent)
            {
                agentEnd = endEvent;
            }

            return Task.CompletedTask;
        });

        var runResult = await agent.PromptAsync("boom");

        var failure = agent.State.Messages.Last().ShouldBeOfType<AssistantAgentMessage>();
        failure.Content.ShouldBeEmpty();
        failure.FinishReason.ShouldBe(BotNexus.Agent.Providers.Core.Models.StopReason.Error);
        failure.ErrorMessage.ShouldBe("provider exploded");
        runResult.ShouldHaveSingleItem().ShouldBe(failure);
        agent.State.ErrorMessage.ShouldBe("provider exploded");
        agentEnd.ShouldNotBeNull();
        agentEnd!.Messages.ShouldHaveSingleItem()
            .ShouldBeOfType<AssistantAgentMessage>()
            .ErrorMessage.ShouldBe("provider exploded");
    }

    [Fact]
    public async Task PromptAsync_WhenCancelled_EmitsAbortedAgentEndAndDoesNotThrow()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(release);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        AgentEndEvent? agentEnd = null;
        using var _ = agent.Subscribe((@event, _) =>
        {
            if (@event is AgentEndEvent endEvent)
            {
                agentEnd = endEvent;
            }

            return Task.CompletedTask;
        });

        var runTask = agent.PromptAsync("cancel me");
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(10)).ShouldBeTrue();
        await agent.AbortAsync();
        release.TrySetResult();

        var result = await runTask;
        result.ShouldHaveSingleItem();
        var aborted = result[0].ShouldBeOfType<AssistantAgentMessage>();
        aborted.FinishReason.ShouldBe(BotNexus.Agent.Providers.Core.Models.StopReason.Aborted);
        agentEnd.ShouldNotBeNull();
        agentEnd!.Messages.ShouldHaveSingleItem().ShouldBeOfType<AssistantAgentMessage>()
            .FinishReason.ShouldBe(BotNexus.Agent.Providers.Core.Models.StopReason.Aborted);
    }

    [Fact]
    public async Task ContinueAsync_WhenLastMessageIsNotAssistant_DoesNotDrainQueuesBeforeRun()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        await agent.PromptAsync("seed");

        agent.State.Messages = [new UserMessage("manual user")];
        agent.Steer(new UserMessage("steer message"));
        agent.FollowUp(new UserMessage("follow-up message"));

        var continued = await agent.ContinueAsync();
        continued.OfType<UserMessage>().Select(message => message.Content)
            .ShouldContain("steer message");
    }

    [Fact]
    public async Task ContinueAsync_WhenFollowUpQueued_DoesNotSkipInitialSteeringPoll()
    {
        using var provider = RegisterDefaultProvider();
        var steeringPollCount = 0;
        var options = TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")) with
        {
            GetSteeringMessages = _ => Task.FromResult<IReadOnlyList<AgentMessage>>(
                Interlocked.Increment(ref steeringPollCount) == 1
                    ? [new UserMessage("steer from delegate")]
                    : [])
        };
        var agent = new BotNexus.Agent.Core.Agent(options);
        agent.State.Messages =
        [
            new AssistantAgentMessage(
                Content: "seed assistant",
                FinishReason: BotNexus.Agent.Providers.Core.Models.StopReason.Stop,
                Timestamp: DateTimeOffset.UtcNow)
        ];
        agent.FollowUp(new UserMessage("follow-up message"));

        var continued = await agent.ContinueAsync();

        var continuedContents = continued.OfType<UserMessage>().Select(message => message.Content);
        continuedContents.ShouldContain("follow-up message");
        continuedContents.ShouldContain("steer from delegate");
        continued.OfType<AssistantAgentMessage>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task IsRunning_IsTrueForEntireRunLifecycle()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(release);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        var runTask = agent.PromptAsync("run");
        SpinWait.SpinUntil(() => agent.State.IsRunning, TimeSpan.FromSeconds(10)).ShouldBeTrue();
        agent.State.IsStreaming.ShouldBeFalse();

        release.TrySetResult();
        await runTask;
        agent.State.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task PromptAsync_DefersAssistantMessageAddUntilMessageEnd()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterProviderWithDelayedCompletion(release);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        var runTask = agent.PromptAsync("stream");
        SpinWait.SpinUntil(() => agent.State.StreamingMessage is not null, TimeSpan.FromSeconds(10)).ShouldBeTrue();
        agent.State.Messages.Last().ShouldBeOfType<UserMessage>();

        release.TrySetResult();
        await runTask;
        agent.State.Messages.Last().ShouldBeOfType<AssistantAgentMessage>();
    }

    [Fact]
    public async Task PromptAsync_WhenListenerThrows_ContinuesRunAndReportsDiagnostic()
    {
        var diagnostics = new List<string>();
        using var provider = RegisterDefaultProvider();
        var options = TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api"))
            with
            {
                OnDiagnostic = message => diagnostics.Add(message)
            };
        var agent = new BotNexus.Agent.Core.Agent(options);
        using var _ = agent.Subscribe((@event, _) =>
        {
            if (@event is MessageEndEvent)
            {
                throw new InvalidOperationException("listener failed");
            }

            return Task.CompletedTask;
        });

        var result = await agent.PromptAsync("hello");

        result.OfType<AssistantAgentMessage>().ShouldHaveSingleItem();
        diagnostics.ShouldContain(message => message.Contains("Listener threw: listener failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbortAsync_WhenAgentEndListenerThrows_ReportsDiagnostic()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new List<string>();
        using var provider = RegisterBlockingProvider(release);
        var options = TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api"))
            with
            {
                OnDiagnostic = message => diagnostics.Add(message)
            };
        var agent = new BotNexus.Agent.Core.Agent(options);
        using var _ = agent.Subscribe((@event, _) =>
        {
            if (@event is AgentEndEvent)
            {
                throw new InvalidOperationException("listener failed");
            }

            return Task.CompletedTask;
        });

        var runTask = agent.PromptAsync("cancel me");
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(10)).ShouldBeTrue();
        await agent.AbortAsync();
        release.TrySetResult();
        await runTask;

        diagnostics.ShouldHaveSingleItem().ShouldContain("Listener threw: listener failed");
    }

    [Fact]
    public async Task PromptAsync_WhenTransformContextIsNull_DoesNotCrash()
    {
        using var provider = RegisterDefaultProvider();
        var options = TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api"))
            with
            {
                TransformContext = null
            };
        var agent = new BotNexus.Agent.Core.Agent(options);

        var result = await agent.PromptAsync("hello");

        result.OfType<AssistantAgentMessage>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task PromptAsync_WhenConvertToLlmIsNull_UsesDefaultMessageConverter()
    {
        using var provider = RegisterDefaultProvider();
        var options = TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api"))
            with
            {
                ConvertToLlm = null
            };
        var agent = new BotNexus.Agent.Core.Agent(options);

        var result = await agent.PromptAsync("hello");

        result.OfType<AssistantAgentMessage>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Reset_DoesNotCancelActiveRun()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(release);
        var agent = new BotNexus.Agent.Core.Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        var runTask = agent.PromptAsync("running");
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(10)).ShouldBeTrue();

        agent.Reset();
        runTask.IsCompleted.ShouldBeFalse();

        release.TrySetResult();
        await runTask;
    }

    private static IDisposable RegisterDefaultProvider()
    {
        var provider = new TestApiProvider(
            "test-api",
            simpleStreamFactory: (_, _, _) => TestStreamFactory.CreateTextResponse("assistant"));
        return TestHelpers.RegisterProvider(provider);
    }

    private static IDisposable RegisterBlockingProvider(TaskCompletionSource release)
    {
        var provider = new TestApiProvider(
            "test-api",
            simpleStreamFactory: (_, _, _) =>
            {
                var stream = new LlmStream();
                _ = Task.Run(async () =>
                {
                    await release.Task.ConfigureAwait(false);
                    var completion = TestStreamFactory.CreateTextResponse("assistant");
                    await foreach (var evt in completion)
                    {
                        stream.Push(evt);
                    }

                    stream.End(await completion.GetResultAsync().ConfigureAwait(false));
                });
                return stream;
            });

        return TestHelpers.RegisterProvider(provider);
    }

    private static IDisposable RegisterProviderWithDelayedCompletion(TaskCompletionSource release)
    {
        var provider = new TestApiProvider(
            "test-api",
            simpleStreamFactory: (_, _, _) =>
            {
                var stream = new LlmStream();
                var message = new BotNexus.Agent.Providers.Core.Models.AssistantMessage(
                    Content: [new BotNexus.Agent.Providers.Core.Models.TextContent("assistant")],
                    Api: "test-api",
                    Provider: "test-provider",
                    ModelId: "test-model",
                    Usage: BotNexus.Agent.Providers.Core.Models.Usage.Empty(),
                    StopReason: BotNexus.Agent.Providers.Core.Models.StopReason.Stop,
                    ErrorMessage: null,
                    ResponseId: "resp-start",
                    Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                _ = Task.Run(async () =>
                {
                    stream.Push(new StartEvent(message));
                    await release.Task.ConfigureAwait(false);
                    stream.Push(new DoneEvent(BotNexus.Agent.Providers.Core.Models.StopReason.Stop, message));
                    stream.End(message);
                });

                return stream;
            });

        return TestHelpers.RegisterProvider(provider);
    }
}
