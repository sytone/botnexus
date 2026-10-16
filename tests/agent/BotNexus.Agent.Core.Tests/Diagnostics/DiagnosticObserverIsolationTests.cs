using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Diagnostics;

/// <summary>Diagnostic delivery cannot replace runtime outcomes or stop later subscribers.</summary>
[Collection(ApiProviderRegistryCollection.Name)]
public sealed class DiagnosticObserverIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAsync_ThrowingDiagnosticSubscriber_PreservesResultAndLaterListeners(bool cancellationException)
    {
        const string api = "diagnostic-listener-isolation";
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(api,
            simpleStreamFactory: (_, _, _) => TestStreamFactory.CreateTextResponse("survived")));
        var diagnostics = new List<string>();
        Action<string> observer = _ => ThrowDiagnosticFailure(cancellationException);
        observer += diagnostics.Add;
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel(api)) with
        {
            DiagnosticObserver = observer
        });
        using var failingListener = agent.Subscribe((_, _) => throw new InvalidOperationException("listener failure"));
        var completed = 0;
        using var healthyListener = agent.Subscribe((evt, _) =>
        {
            if (evt is AgentEndEvent)
                completed++;
            return Task.CompletedTask;
        });

        var result = await agent.PromptAsync("hello");

        result.OfType<AssistantAgentMessage>().ShouldHaveSingleItem().Content.ShouldBe("survived");
        completed.ShouldBe(1);
        agent.Status.ShouldBe(AgentStatus.Idle);
        agent.State.ErrorMessage.ShouldBeNull();
        diagnostics.ShouldNotBeEmpty();
        diagnostics.ShouldAllBe(message => message.Contains("Listener threw: listener failure", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("optional-failure")]
    [InlineData("terminal-overflow")]
    [InlineData("thrown-overflow")]
    public async Task RunAsync_DiagnosticSubscriberThrows_PreservesCompactionAndOverflowRecovery(string scenario)
    {
        var api = "diagnostic-recovery-" + scenario;
        var calls = 0;
        string? observedPrompt = null;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, context, _) =>
        {
            observedPrompt = context.SystemPrompt;
            calls++;
            if (calls == 1 && scenario == "terminal-overflow")
                return TestStreamFactory.CreateErrorResponse("input is too long for requested model");
            if (calls == 1 && scenario == "thrown-overflow")
                throw new InvalidOperationException("input is too long for requested model");
            return TestStreamFactory.CreateTextResponse("recovered");
        }));
        var diagnostics = new List<string>();
        Action<string> observer = _ => throw new OperationCanceledException("diagnostic cancellation is not run cancellation");
        observer += diagnostics.Add;
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            DiagnosticObserver = observer,
            ContextCompactionService = _ => scenario switch
            {
                "replacement" => Task.FromResult<AgentContext?>(new AgentContext("replacement", [], [])),
                "optional-failure" => Task.FromException<AgentContext?>(new InvalidOperationException("optional compactor failed")),
                _ => Task.FromResult<AgentContext?>(null)
            }
        };
        var history = Enumerable.Range(0, 15)
            .Select(index => (AgentMessage)TestHelpers.CreateUserMessage($"history-{index}")).ToArray();

        var result = await AgentLoopRunner.RunAsync([TestHelpers.CreateUserMessage("hello")],
            new AgentContext("original", history, []), config, _ => Task.CompletedTask, CancellationToken.None);

        result.OfType<AssistantAgentMessage>().ShouldContain(message => message.Content == "recovered");
        calls.ShouldBe(scenario.Contains("overflow", StringComparison.Ordinal) ? 2 : 1);
        observedPrompt.ShouldBe(scenario == "replacement" ? "replacement" : "original");
        diagnostics.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task RunAsync_DiagnosticSubscriberThrows_PreservesOriginalExhaustionAndSuspension()
    {
        const string api = "diagnostic-exhaustion-isolation";
        var failure = new InvalidOperationException("insufficient_quota: provider exhausted");
        var calls = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
        {
            calls++;
            throw failure;
        }));
        var diagnostics = new List<string>();
        Action<string> observer = _ => throw new InvalidOperationException("sink failed");
        observer += diagnostics.Add;
        var registry = new ProviderSuspensionRegistry();
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
        {
            DiagnosticObserver = observer,
            SuspensionRegistry = registry,
            AuthProfile = "profile"
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => AgentLoopRunner.RunAsync(
            [TestHelpers.CreateUserMessage("hello")], TestHelpers.CreateEmptyContext(), config,
            _ => Task.CompletedTask, CancellationToken.None));

        exception.ShouldBeSameAs(failure);
        calls.ShouldBe(1);
        registry.IsSuspended("test-provider", "profile").ShouldBeTrue();
        diagnostics.ShouldHaveSingleItem().ShouldContain("insufficient_quota");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAsync_EndListenerAndDiagnosticSubscriberThrow_PreservesTerminalOutcome(bool cancelRun)
    {
        const string api = "diagnostic-terminal-isolation";
        using var cancellation = new CancellationTokenSource();
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: (_, _, _) =>
        {
            if (cancelRun)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            throw new InvalidOperationException("original provider failure");
        }));
        var diagnostics = new List<string>();
        Action<string> observer = _ => throw new InvalidOperationException("sink failed");
        observer += diagnostics.Add;
        var agent = new Agent(TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel(api)) with
        {
            DiagnosticObserver = observer
        });
        using var listener = agent.Subscribe((evt, _) => evt is AgentEndEvent
            ? Task.FromException(new OperationCanceledException("end listener failed"))
            : Task.CompletedTask);

        var result = await agent.PromptAsync("hello", cancellationToken: cancellation.Token);

        var message = result.OfType<AssistantAgentMessage>().ShouldHaveSingleItem();
        message.FinishReason.ShouldBe(cancelRun ? StopReason.Aborted : StopReason.Error);
        message.ErrorMessage.ShouldBe(cancelRun ? "Operation aborted" : "original provider failure");
        agent.Status.ShouldBe(AgentStatus.Idle);
        diagnostics.ShouldHaveSingleItem().ShouldContain("Listener error during agent_end");
    }

    private static void ThrowDiagnosticFailure(bool cancellationException)
    {
        if (cancellationException)
            throw new OperationCanceledException("sink cancelled");
        throw new InvalidOperationException("sink failed");
    }
}