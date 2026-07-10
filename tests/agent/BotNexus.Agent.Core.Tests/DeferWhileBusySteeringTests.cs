using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

using UserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Agent.Core.Tests;

/// <summary>
/// Regression tests for #1845: a defer-while-busy steered message (e.g. a pre-compaction
/// memory flush) must be held aside while the run still has pending tool calls, and only
/// released at a genuine idle turn boundary — so the flush turn cannot consume the loop's
/// continuation and abandon the original in-flight task.
/// </summary>
public sealed class DeferWhileBusySteeringTests
{
    [Fact]
    public async Task DeferWhileBusy_InjectedMidFlight_DoesNotConsumeLoopContinuation()
    {
        var llmCallCount = 0;
        var flushVisibleInCall = new Dictionary<int, bool>();
        var delegateDrainCount = 0;

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);

            var flushSeen = ctx.Messages.Any(m =>
                m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                um.Content is UserMessageContent umc &&
                umc.Text == "memory-flush");
            flushVisibleInCall[call] = flushSeen;

            // Call 1: original work issues a tool call. Call 2: original work finishes (text)
            // => run reaches idle, at which point the deferred flush is released for call 3.
            if (call == 1)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
            }

            return TestStreamFactory.CreateTextResponse("done-" + call);
        });

        // Inject the defer-while-busy flush at the FIRST post-turn drain (delegate call #2:
        // #1 is the initial idle poll, #2 is the drain after turn 1 while a tool call is pending).
        var options = CreateOptions(provider.Api, tools: [new CalculateTool()]) with
        {
            GetSteeringMessages = _ =>
            {
                var count = Interlocked.Increment(ref delegateDrainCount);
                if (count == 2)
                {
                    return Task.FromResult<IReadOnlyList<AgentMessage>>(
                        [new UserMessage("memory-flush") { DeferWhileBusy = true }]);
                }

                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);

        await agent.PromptAsync("original-task");

        // The original in-flight work (call 2) must complete WITHOUT the flush consuming the loop.
        flushVisibleInCall.ShouldContainKey(2);
        flushVisibleInCall[2].ShouldBeFalse("Deferred flush must NOT be injected while the run is still busy");

        // The flush must be released and processed at the idle boundary (call 3) — the loop
        // continued rather than terminating on the flush turn.
        llmCallCount.ShouldBeGreaterThanOrEqualTo(3, "Loop should continue past original work to process the deferred flush");
        flushVisibleInCall.ShouldContainKey(3);
        flushVisibleInCall[3].ShouldBeTrue("Deferred flush should be released once the run reaches idle");
    }

    [Fact]
    public async Task DeferWhileBusy_WhenAgentIdleAtInject_BehavesLikeNormalSteer()
    {
        var flushVisible = false;
        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            flushVisible = ctx.Messages.Any(m =>
                m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                um.Content is UserMessageContent umc &&
                umc.Text == "idle-flush");
            return TestStreamFactory.CreateTextResponse("ok");
        });

        var agent = CreateAgent(provider.Api);
        agent.Status.ShouldBe(AgentStatus.Idle);

        // Queued while idle: the initial-drain path is unchanged, so it is consumed on next prompt.
        agent.Steer(new UserMessage("idle-flush") { DeferWhileBusy = true });
        await agent.PromptAsync("trigger");

        flushVisible.ShouldBeTrue("A defer-while-busy message queued while idle behaves like a normal steer");
    }

    private static BotNexus.Agent.Core.Agent CreateAgent(
        string api = "test-api",
        QueueMode steeringMode = QueueMode.All,
        QueueMode followUpMode = QueueMode.All,
        IAgentTool[]? tools = null)
    {
        return new BotNexus.Agent.Core.Agent(CreateOptions(api, steeringMode, followUpMode, tools));
    }

    private static AgentOptions CreateOptions(
        string api = "test-api",
        QueueMode steeringMode = QueueMode.All,
        QueueMode followUpMode = QueueMode.All,
        IAgentTool[]? tools = null)
    {
        var state = tools is { Length: > 0 }
            ? new AgentInitialState(Tools: tools)
            : null;

        return TestHelpers.CreateTestOptions(
            initialState: state,
            model: TestHelpers.CreateTestModel(api),
            steeringMode: steeringMode,
            followUpMode: followUpMode);
    }

    private static ProviderRegistration RegisterIsolatedProvider(
        Func<LlmModel, Context, SimpleStreamOptions?, LlmStream> factory)
    {
        var api = $"test-api-{Guid.NewGuid():N}";
        var scope = TestHelpers.RegisterProvider(new TestApiProvider(api, simpleStreamFactory: factory));
        return new ProviderRegistration(scope, api);
    }

    private sealed class ProviderRegistration(IDisposable scope, string api) : IDisposable
    {
        public string Api { get; } = api;
        public void Dispose() => scope.Dispose();
    }
}
