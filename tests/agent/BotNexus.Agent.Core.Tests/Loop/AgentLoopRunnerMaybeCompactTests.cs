using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Tests for the optional mid-loop auto-compaction hook (#1710). A single long
/// dispatch (cron/autonomous follow-up loop) used to grow unbounded because
/// ShouldCompact ran only pre-turn at the gateway; the agent loop never re-checked
/// between outer iterations. The fix adds an optional best-effort
/// <see cref="AgentLoopConfig.MaybeCompactAsync"/> awaited at the top of the outer
/// loop, so a long dispatch gets a compaction opportunity between turns and the
/// loop continues even if the hook throws.
/// </summary>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerMaybeCompactTests
{
    [Fact]
    public async Task RunAsync_InvokesMaybeCompact_AtLeastOncePerRun()
    {
        var compactCalls = 0;
        using var provider = RegisterProvider("maybe-compact-once", (_, _, _) =>
            TestStreamFactory.CreateTextResponse("done"));

        var config = CreateConfig("maybe-compact-once", _ =>
        {
            Interlocked.Increment(ref compactCalls);
            return Task.FromResult<AgentContext?>(null);
        });
        var context = new AgentContext(null, [], []);

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("hello")],
            context,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        compactCalls.ShouldBeGreaterThanOrEqualTo(1,
            "the loop must re-check compaction at the top of the outer while(true)");
    }

    [Fact]
    public async Task RunAsync_WhenFollowUpDrivesSecondIteration_RechecksCompactionMidLoop()
    {
        var compactCalls = 0;
        using var provider = RegisterProvider("maybe-compact-followup", (_, _, _) =>
            TestStreamFactory.CreateTextResponse("ok"));

        var followUpReturned = false;
        var config = CreateConfig(
            "maybe-compact-followup",
            _ =>
            {
                Interlocked.Increment(ref compactCalls);
                return Task.FromResult<AgentContext?>(null);
            },
            getFollowUpMessages: _ =>
            {
                if (!followUpReturned)
                {
                    followUpReturned = true;
                    return Task.FromResult<IReadOnlyList<AgentMessage>>([new AgentUserMessage("follow-up")]);
                }

                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            });
        var context = new AgentContext(null, [], []);

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("first")],
            context,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        // First iteration + the follow-up-driven second iteration each re-check at the
        // top of the outer loop, so a long multi-turn dispatch cannot blow past the
        // threshold unchecked.
        compactCalls.ShouldBeGreaterThanOrEqualTo(2,
            "each outer-loop iteration must re-check compaction so a long dispatch is bounded");
    }

    [Fact]
    public async Task RunAsync_WhenMaybeCompactReturnsContext_UsesRefreshedSnapshotForNextProviderTurn()
    {
        Context? observed = null;
        using var provider = RegisterProvider("maybe-compact-refresh", (_, context, _) =>
        {
            observed = context;
            return TestStreamFactory.CreateTextResponse("done");
        });

        var refreshed = new AgentContext(
            "compacted system prompt",
            [new AgentUserMessage("compacted visible tail")],
            []);
        var config = CreateConfig("maybe-compact-refresh", _ => Task.FromResult<AgentContext?>(refreshed));

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("stale prompt")],
            new AgentContext("stale system prompt", [new AgentUserMessage("stale history")], []),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        observed.ShouldNotBeNull();
        observed.SystemPrompt.ShouldBe("compacted system prompt");
        observed.Messages.OfType<BotNexus.Agent.Providers.Core.Models.UserMessage>()
            .Select(message => message.Content.Text)
            .ShouldBe(["compacted visible tail"]);
    }

    [Fact]
    public async Task RunAsync_WhenMaybeCompactThrows_SwallowsAndContinues()
    {
        using var provider = RegisterProvider("maybe-compact-throws", (_, _, _) =>
            TestStreamFactory.CreateTextResponse("survived"));

        var config = CreateConfig("maybe-compact-throws",
            _ => Task.FromException<AgentContext?>(new InvalidOperationException("compactor boom")));
        var context = new AgentContext(null, [], []);

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            context,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        result.OfType<AssistantAgentMessage>()
            .ShouldContain(m => m.Content == "survived",
                "a compactor failure must be best-effort: the loop continues to a normal turn");
    }

    [Fact]
    public async Task RunAsync_WhenMaybeCompactIsCancelled_PropagatesCancellation()
    {
        using var provider = RegisterProvider("maybe-compact-cancelled", (_, _, _) =>
            TestStreamFactory.CreateTextResponse("must not run"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var config = CreateConfig(
            "maybe-compact-cancelled",
            cancellationToken => Task.FromCanceled<AgentContext?>(cancellationToken));

        Func<Task> act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            config,
            _ => Task.CompletedTask,
            cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(act);
    }

    #region Helpers

    private static AgentLoopConfig CreateConfig(
        string apiId,
        Func<CancellationToken, Task<AgentContext?>> maybeCompact,
        GetMessagesDelegate? getFollowUpMessages = null)
    {
        return new AgentLoopConfig(
            Model: TestHelpers.CreateTestModel(apiId),
            LlmClient: TestHelpers.CreateLlmClient(),
            ConvertToLlm: (messages, _) => Task.FromResult<IReadOnlyList<Message>>(
                messages.OfType<AgentUserMessage>()
                    .Select(m => (Message)new BotNexus.Agent.Providers.Core.Models.UserMessage(
                        new UserMessageContent(m.Content),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                    .ToList()),
            TransformContext: (messages, _) => Task.FromResult(messages),
            GetApiKey: (_, _) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: getFollowUpMessages,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions(),
            MaxRetryDelayMs: 1,
            MaybeCompactAsync: maybeCompact);
    }

    private static IDisposable RegisterProvider(string apiId,
        Func<LlmModel, Context, SimpleStreamOptions?, BotNexus.Agent.Providers.Core.Streaming.LlmStream> factory)
    {
        var provider = new TestApiProvider(apiId, simpleStreamFactory: factory);
        return TestHelpers.RegisterProvider(provider);
    }

    #endregion
}
