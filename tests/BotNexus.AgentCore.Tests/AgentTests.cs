using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Tests.TestUtils;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Streaming;
using FluentAssertions;

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
}
