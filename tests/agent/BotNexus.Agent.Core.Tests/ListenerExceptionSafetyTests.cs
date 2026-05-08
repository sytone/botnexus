using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Tests;

public sealed class ListenerExceptionSafetyTests
{
    [Fact]
    public async Task PromptAsync_WhenListenerThrows_CompletesRunWithoutCrashing()
    {
        const string api = "listener-safety-api";
        using var provider = TestHelpers.RegisterProvider(
            new TestApiProvider(api, simpleStreamFactory: (_, _, _) => TestStreamFactory.CreateTextResponse("assistant")));
        var diagnostics = new List<string>();
        var options = TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel(api))
            with
            {
                OnDiagnostic = message => diagnostics.Add(message)
            };
        var agent = new BotNexus.Agent.Core.Agent(options);
        using var _ = agent.Subscribe((_, _) => throw new InvalidOperationException("listener exploded"));

        var result = await agent.PromptAsync("hello");

        result.OfType<AssistantAgentMessage>().ShouldHaveSingleItem();
        agent.Status.ShouldBe(AgentStatus.Idle);
        diagnostics.ShouldContain(message => message.Contains("Listener threw", StringComparison.Ordinal));
    }
}
