using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Tests.TestUtils;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Streaming;
using FluentAssertions;
using System.Reflection;

namespace BotNexus.AgentCore.Tests;

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

        var agent = new Agent(options);

        agent.Should().NotBeNull();
        agent.Status.Should().Be(AgentStatus.Idle);
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
        var agent = new Agent(TestHelpers.CreateTestOptions(initialState, model));

        agent.State.SystemPrompt.Should().Be("System prompt");
        agent.State.Model.Should().Be(model);
        agent.State.Tools.Should().ContainSingle(tool => tool.Name == "calculate");
        agent.State.Messages.OfType<UserMessage>().Should().ContainSingle(message => message.Content == "history");
    }

    [Fact]
    public void Reset_ClearsRuntimeState()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        agent.State.Messages = [new UserMessage("history")];
        agent.State.SetErrorMessage("error");
        agent.State.SetStreamingMessage(new AssistantAgentMessage("streaming"));
        agent.State.SetPendingToolCalls(["tool-1"]);

        agent.Reset();

        agent.State.Messages.Should().BeEmpty();
        agent.State.ErrorMessage.Should().BeNull();
        agent.State.StreamingMessage.Should().BeNull();
        agent.State.PendingToolCalls.Should().BeEmpty();
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task Subscribe_ReturnsDisposableThatUnsubscribes()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        var callbackCount = 0;
        var subscription = agent.Subscribe((_, _) =>
        {
            Interlocked.Increment(ref callbackCount);
            return Task.CompletedTask;
        });

        await agent.PromptAsync("first");
        callbackCount.Should().BeGreaterThan(0);

        var firstRunCount = callbackCount;
        subscription.Dispose();
        await agent.PromptAsync("second");

        callbackCount.Should().Be(firstRunCount);
    }

    [Fact]
    public async Task PromptAsync_WhenAlreadyRunning_ThrowsInvalidOperationException()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(release);
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        var firstRun = agent.PromptAsync("first");
        var started = SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(2));
        started.Should().BeTrue();

        var secondPrompt = () => agent.PromptAsync("second");
        await secondPrompt.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Agent is already running.");

        release.TrySetResult();
        await firstRun;
    }

    [Fact]
    public async Task SteerAndFollowUp_EnqueueMessagesThatAreConsumedByRun()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        agent.Steer(new UserMessage("steer message"));
        agent.FollowUp(new UserMessage("follow-up message"));

        var produced = await agent.PromptAsync("base prompt");

        produced.OfType<UserMessage>().Select(message => message.Content)
            .Should().Contain(["base prompt", "steer message", "follow-up message"]);
    }

    [Fact]
    public async Task ContinueAsync_WhenSteeringAndFollowUpQueued_DrainsBothInSameRun()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        await agent.PromptAsync("seed");

        agent.Steer(new UserMessage("steer message"));
        agent.FollowUp(new UserMessage("follow-up message"));

        var firstContinue = await agent.ContinueAsync();
        firstContinue.OfType<UserMessage>().Select(message => message.Content)
            .Should().Contain("steer message")
            .And.Contain("follow-up message");
    }

    [Fact]
    public async Task ClearAllQueues_RemovesPendingMessages()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        agent.Steer(new UserMessage("steer message"));
        agent.FollowUp(new UserMessage("follow-up message"));
        agent.ClearAllQueues();

        var produced = await agent.PromptAsync("base prompt");

        produced.OfType<UserMessage>().Select(message => message.Content)
            .Should().BeEquivalentTo(["base prompt"]);
    }

    [Fact]
    public async Task HasQueuedMessages_WhenQueuedAndThenDrained_ReflectsCurrentQueueState()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        agent.Steer(new UserMessage("steer message"));

        agent.HasQueuedMessages.Should().BeTrue();

        _ = await agent.PromptAsync("base prompt");
        agent.HasQueuedMessages.Should().BeFalse();
    }

    [Fact]
    public void SteeringMode_WhenChangedAtRuntime_ChangesDrainBehavior()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions(
            model: TestHelpers.CreateTestModel("test-api"),
            steeringMode: QueueMode.All));
        agent.Steer(new UserMessage("steer one"));
        agent.Steer(new UserMessage("steer two"));
        agent.SteeringMode = QueueMode.OneAtATime;

        var drainMethod = typeof(Agent).GetMethod("DrainQueuedMessages", BindingFlags.NonPublic | BindingFlags.Instance);
        drainMethod.Should().NotBeNull();
        var firstDrain = (IReadOnlyList<AgentMessage>)drainMethod!.Invoke(agent, null)!;
        firstDrain.Should().ContainSingle();

        agent.Steer(new UserMessage("steer three"));
        agent.Steer(new UserMessage("steer four"));

        agent.SteeringMode = QueueMode.All;
        var secondDrain = (IReadOnlyList<AgentMessage>)drainMethod.Invoke(agent, null)!;
        secondDrain.Should().HaveCount(3);
    }

    [Fact]
    public async Task PromptAsync_WhenRunFails_AddsSyntheticErrorAssistantMessageAndEmitsAgentEnd()
    {
        using var provider = TestHelpers.RegisterProvider(
            new TestApiProvider(
                "test-api",
                simpleStreamFactory: (_, _, _) => throw new InvalidOperationException("provider exploded")));
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
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

        var failure = agent.State.Messages.Last().Should().BeOfType<AssistantAgentMessage>().Subject;
        failure.Content.Should().BeEmpty();
        failure.FinishReason.Should().Be(BotNexus.Providers.Core.Models.StopReason.Error);
        failure.ErrorMessage.Should().Be("provider exploded");
        runResult.Should().ContainSingle().Which.Should().BeEquivalentTo(failure);
        agent.State.ErrorMessage.Should().Be("provider exploded");
        agentEnd.Should().NotBeNull();
        agentEnd!.Messages.Should().ContainSingle()
            .Which.Should().BeOfType<AssistantAgentMessage>()
            .Which.ErrorMessage.Should().Be("provider exploded");
    }

    [Fact]
    public async Task PromptAsync_WhenCancelled_EmitsAbortedAgentEndAndDoesNotThrow()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(release);
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
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
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(2)).Should().BeTrue();
        await agent.AbortAsync();
        release.TrySetResult();

        var result = await runTask;
        result.Should().ContainSingle();
        var aborted = result[0].Should().BeOfType<AssistantAgentMessage>().Subject;
        aborted.FinishReason.Should().Be(BotNexus.Providers.Core.Models.StopReason.Aborted);
        agentEnd.Should().NotBeNull();
        agentEnd!.Messages.Should().ContainSingle().Which.Should().BeOfType<AssistantAgentMessage>()
            .Which.FinishReason.Should().Be(BotNexus.Providers.Core.Models.StopReason.Aborted);
    }

    [Fact]
    public async Task ContinueAsync_WhenLastMessageIsNotAssistant_DoesNotDrainQueuesBeforeRun()
    {
        using var provider = RegisterDefaultProvider();
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));
        await agent.PromptAsync("seed");

        agent.State.Messages = [new UserMessage("manual user")];
        agent.Steer(new UserMessage("steer message"));
        agent.FollowUp(new UserMessage("follow-up message"));

        var continued = await agent.ContinueAsync();
        continued.OfType<UserMessage>().Select(message => message.Content)
            .Should().Contain("steer message");
    }

    [Fact]
    public async Task IsRunning_IsTrueForEntireRunLifecycle()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(release);
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        var runTask = agent.PromptAsync("run");
        SpinWait.SpinUntil(() => agent.State.IsRunning, TimeSpan.FromSeconds(2)).Should().BeTrue();
        agent.State.IsStreaming.Should().BeFalse();

        release.TrySetResult();
        await runTask;
        agent.State.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task PromptAsync_DefersAssistantMessageAddUntilMessageEnd()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterProviderWithDelayedCompletion(release);
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        var runTask = agent.PromptAsync("stream");
        SpinWait.SpinUntil(() => agent.State.StreamingMessage is not null, TimeSpan.FromSeconds(2)).Should().BeTrue();
        agent.State.Messages.Last().Should().BeOfType<UserMessage>();

        release.TrySetResult();
        await runTask;
        agent.State.Messages.Last().Should().BeOfType<AssistantAgentMessage>();
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
        var agent = new Agent(options);
        using var _ = agent.Subscribe((@event, _) =>
        {
            if (@event is AgentEndEvent)
            {
                throw new InvalidOperationException("listener failed");
            }

            return Task.CompletedTask;
        });

        var runTask = agent.PromptAsync("cancel me");
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(2)).Should().BeTrue();
        await agent.AbortAsync();
        release.TrySetResult();
        await runTask;

        diagnostics.Should().ContainSingle(message => message.Contains("Listener error during agent_end", StringComparison.Ordinal));
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
        var agent = new Agent(options);

        var result = await agent.PromptAsync("hello");

        result.OfType<AssistantAgentMessage>().Should().ContainSingle();
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
        var agent = new Agent(options);

        var result = await agent.PromptAsync("hello");

        result.OfType<AssistantAgentMessage>().Should().ContainSingle();
    }

    [Fact]
    public async Task Reset_DoesNotCancelActiveRun()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterBlockingProvider(release);
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel("test-api")));

        var runTask = agent.PromptAsync("running");
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(2)).Should().BeTrue();

        agent.Reset();
        runTask.IsCompleted.Should().BeFalse();

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
                var message = new BotNexus.Providers.Core.Models.AssistantMessage(
                    Content: [new BotNexus.Providers.Core.Models.TextContent("assistant")],
                    Api: "test-api",
                    Provider: "test-provider",
                    ModelId: "test-model",
                    Usage: BotNexus.Providers.Core.Models.Usage.Empty(),
                    StopReason: BotNexus.Providers.Core.Models.StopReason.Stop,
                    ErrorMessage: null,
                    ResponseId: "resp-start",
                    Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                _ = Task.Run(async () =>
                {
                    stream.Push(new StartEvent(message));
                    await release.Task.ConfigureAwait(false);
                    stream.Push(new DoneEvent(BotNexus.Providers.Core.Models.StopReason.Stop, message));
                    stream.End(message);
                });

                return stream;
            });

        return TestHelpers.RegisterProvider(provider);
    }
}
