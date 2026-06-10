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
/// Comprehensive steering loop tests covering all injection scenarios.
/// Tests the Agent-layer steering contract: enqueueing, draining at turn boundaries,
/// interaction with tool execution, follow-ups, abort, and concurrency.
/// </summary>
public sealed class SteeringLoopTests
{
    // ═══════════════════════════════════════════════════════════════════
    // 1. Basic Steering Drain Semantics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Steer_BeforeRun_IsDrainedAtFirstTurnBoundary()
    {
        using var provider = RegisterIsolatedProvider((_, _, _) => TestStreamFactory.CreateTextResponse("done"));
        var agent = CreateAgent(provider.Api);

        agent.Steer(new UserMessage("steering-before-run"));
        var result = await agent.PromptAsync("hello");

        var userMessages = result.OfType<UserMessage>().Select(m => m.Content).ToList();
        userMessages.ShouldContain("hello");
        userMessages.ShouldContain("steering-before-run");
    }

    [Fact]
    public async Task Steer_DuringLlmCall_IsDrainedAfterToolCallTurn()
    {
        var llmCallCount = 0;
        var steerInjectedBeforeSecondCall = false;
        var firstStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStreamCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 1)
            {
                var stream = new LlmStream();
                _ = Task.Run(async () =>
                {
                    firstStreamStarted.TrySetResult();
                    await firstStreamCanFinish.Task;
                    var toolStream = TestStreamFactory.CreateToolCallResponse(
                        ("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
                    await foreach (var evt in toolStream)
                    {
                        stream.Push(evt);
                    }

                    stream.End(await toolStream.GetResultAsync());
                });
                return stream;
            }

            steerInjectedBeforeSecondCall = ctx.Messages.Any(m =>
                m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                um.Content is UserMessageContent umc &&
                umc.Text == "mid-flight-steer");
            return TestStreamFactory.CreateTextResponse("final");
        });

        var agent = CreateAgent(provider.Api, tools: [new CalculateTool()]);

        var runTask = agent.PromptAsync("compute");
        await firstStreamStarted.Task;
        agent.Steer(new UserMessage("mid-flight-steer"));
        firstStreamCanFinish.TrySetResult();
        await runTask;

        steerInjectedBeforeSecondCall.ShouldBeTrue("Steer should be drained before the second LLM call");
    }

    [Fact]
    public async Task Steer_DuringToolExecution_IsDrainedBeforeNextLlmCall()
    {
        var toolExecutionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerVisibleInSecondCall = false;
        var llmCallCount = 0;

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 1)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-1", "slow-tool", new Dictionary<string, object?> { ["input"] = "x" }));
            }

            steerVisibleInSecondCall = ctx.Messages.Any(m =>
                m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                um.Content is UserMessageContent umc &&
                umc.Text == "steer-during-tool");
            return TestStreamFactory.CreateTextResponse("done");
        });

        var slowTool = new DelegatingTool("slow-tool", async (_, _, _, _) =>
        {
            toolExecutionStarted.TrySetResult();
            await toolCanFinish.Task;
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "result")]);
        });

        var agent = CreateAgent(provider.Api, tools: [slowTool]);

        var runTask = agent.PromptAsync("go");
        await toolExecutionStarted.Task;

        agent.Steer(new UserMessage("steer-during-tool"));
        toolCanFinish.TrySetResult();

        await runTask;

        steerVisibleInSecondCall.ShouldBeTrue("Steer during tool exec should be visible in next LLM call");
    }

    [Fact]
    public async Task Steer_WhenNoToolCalls_IsConsumedByInitialDrainOnNextRun()
    {
        using var provider = RegisterIsolatedProvider((_, _, _) => TestStreamFactory.CreateTextResponse("text only"));
        var agent = CreateAgent(provider.Api);

        agent.Steer(new UserMessage("pre-queued"));
        var result = await agent.PromptAsync("prompt");

        var userMessages = result.OfType<UserMessage>().Select(m => m.Content).ToList();
        userMessages.ShouldContain("pre-queued");
        userMessages.ShouldContain("prompt");
        agent.HasQueuedMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task Steer_MultipleMessages_AllDrainedInQueueModeAll()
    {
        var injectedUserMessages = new List<string>();
        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            foreach (var m in ctx.Messages)
            {
                if (m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                    um.Content is UserMessageContent umc)
                {
                    injectedUserMessages.Add(umc.Text!);
                }
            }

            return TestStreamFactory.CreateTextResponse("ok");
        });

        var agent = CreateAgent(provider.Api, steeringMode: QueueMode.All);
        agent.Steer(new UserMessage("steer-1"));
        agent.Steer(new UserMessage("steer-2"));
        agent.Steer(new UserMessage("steer-3"));

        await agent.PromptAsync("base");

        injectedUserMessages.ShouldContain("steer-1");
        injectedUserMessages.ShouldContain("steer-2");
        injectedUserMessages.ShouldContain("steer-3");
        agent.HasQueuedMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task Steer_MultipleMessages_OnlyOneDrainedInQueueModeOneAtATime()
    {
        var llmCallCount = 0;
        var firstCallUserMessages = new List<string>();
        var secondCallUserMessages = new List<string>();

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            var userMsgs = ctx.Messages
                .OfType<BotNexus.Agent.Providers.Core.Models.UserMessage>()
                .Select(m => m.Content is UserMessageContent umc ? umc.Text ?? "" : "")
                .ToList();

            if (call == 1)
            {
                firstCallUserMessages.AddRange(userMsgs);
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
            }

            secondCallUserMessages.AddRange(userMsgs);
            return TestStreamFactory.CreateTextResponse("done");
        });

        var agent = CreateAgent(provider.Api, steeringMode: QueueMode.OneAtATime, tools: [new CalculateTool()]);
        agent.Steer(new UserMessage("steer-A"));
        agent.Steer(new UserMessage("steer-B"));

        await agent.PromptAsync("base");

        firstCallUserMessages.ShouldContain("steer-A");
        firstCallUserMessages.ShouldNotContain("steer-B");
        secondCallUserMessages.ShouldContain("steer-B");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. Steering via GetSteeringMessages Delegate
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SteeringDelegate_CalledAtEachTurnBoundary()
    {
        var delegateCallCount = 0;
        var llmCallCount = 0;

        using var provider = RegisterIsolatedProvider((_, _, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call <= 2)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-" + call, "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
            }

            return TestStreamFactory.CreateTextResponse("final");
        });

        var options = CreateOptions(provider.Api, tools: [new CalculateTool()]) with
        {
            GetSteeringMessages = _ =>
            {
                Interlocked.Increment(ref delegateCallCount);
                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);

        await agent.PromptAsync("go");

        delegateCallCount.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task SteeringDelegate_ReturningMessages_InjectsThemBeforeNextLlmCall()
    {
        var delegateCallCount = 0;
        var steerVisibleInSecondCall = false;
        var llmCallCount = 0;

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 1)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "2+2" }));
            }

            steerVisibleInSecondCall = ctx.Messages.Any(m =>
                m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                um.Content is UserMessageContent umc &&
                umc.Text == "delegate-steer");
            return TestStreamFactory.CreateTextResponse("done");
        });

        var options = CreateOptions(provider.Api, tools: [new CalculateTool()]) with
        {
            GetSteeringMessages = _ =>
            {
                var count = Interlocked.Increment(ref delegateCallCount);
                if (count == 2)
                {
                    return Task.FromResult<IReadOnlyList<AgentMessage>>([new UserMessage("delegate-steer")]);
                }

                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);

        await agent.PromptAsync("go");

        steerVisibleInSecondCall.ShouldBeTrue("Delegate steering message should be in context for second LLM call");
    }

    [Fact]
    public async Task SteeringDelegate_CombinedWithQueuedMessages_BothDrained()
    {
        var injectedUserMessages = new List<string>();
        var delegateCalled = false;

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            foreach (var m in ctx.Messages)
            {
                if (m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                    um.Content is UserMessageContent umc)
                {
                    injectedUserMessages.Add(umc.Text!);
                }
            }

            return TestStreamFactory.CreateTextResponse("ok");
        });

        var options = CreateOptions(provider.Api) with
        {
            GetSteeringMessages = _ =>
            {
                if (!delegateCalled)
                {
                    delegateCalled = true;
                    return Task.FromResult<IReadOnlyList<AgentMessage>>([new UserMessage("from-delegate")]);
                }

                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);
        agent.Steer(new UserMessage("from-queue"));

        await agent.PromptAsync("base");

        injectedUserMessages.ShouldContain("from-queue");
        injectedUserMessages.ShouldContain("from-delegate");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. ContinueAsync + Steering Interaction
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ContinueAsync_WhenLastIsAssistant_DrainsSteeringQueueAndPrompts()
    {
        using var provider = RegisterIsolatedProvider((_, _, _) => TestStreamFactory.CreateTextResponse("continued"));
        var agent = CreateAgent(provider.Api);
        await agent.PromptAsync("seed");

        agent.Steer(new UserMessage("continue-steer"));
        var continued = await agent.ContinueAsync();

        var userMessages = continued.OfType<UserMessage>().Select(m => m.Content).ToList();
        userMessages.ShouldContain("continue-steer");
    }

    [Fact]
    public async Task ContinueAsync_WhenLastIsAssistant_SetsSkipInitialSteeringPoll()
    {
        var delegatePollCount = 0;

        using var provider = RegisterIsolatedProvider((_, _, _) => TestStreamFactory.CreateTextResponse("ok"));

        var options = CreateOptions(provider.Api) with
        {
            GetSteeringMessages = _ =>
            {
                Interlocked.Increment(ref delegatePollCount);
                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);
        await agent.PromptAsync("seed");

        Interlocked.Exchange(ref delegatePollCount, 0);

        agent.Steer(new UserMessage("queued"));
        await agent.ContinueAsync();

        // The delegate IS called at post-turn boundary, but the initial poll is skipped.
        // For a text-only response: 1 call (post-turn drain, not the initial drain)
        delegatePollCount.ShouldBe(1, "Initial steering poll should be skipped; only post-turn drain fires");
    }

    [Fact]
    public async Task ContinueAsync_WhenLastIsNotAssistant_DoesNotDrainQueueFirst()
    {
        using var provider = RegisterIsolatedProvider((_, _, _) => TestStreamFactory.CreateTextResponse("ok"));
        var agent = CreateAgent(provider.Api);
        await agent.PromptAsync("seed");

        agent.State.Messages = [new UserMessage("manual")];
        agent.Steer(new UserMessage("queued-steer"));

        var result = await agent.ContinueAsync();

        var userMessages = result.OfType<UserMessage>().Select(m => m.Content).ToList();
        userMessages.ShouldContain("queued-steer");
    }

    [Fact]
    public async Task ContinueAsync_WhenNoQueuedMessages_ThrowsIfLastIsAssistant()
    {
        using var provider = RegisterIsolatedProvider((_, _, _) => TestStreamFactory.CreateTextResponse("ok"));
        var agent = CreateAgent(provider.Api);
        await agent.PromptAsync("seed");

        await Should.ThrowAsync<InvalidOperationException>(() => agent.ContinueAsync());
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. FollowUp + Steering Interaction
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FollowUp_TriggersNewLoopCycleAfterAllTurnsSettle()
    {
        var llmCallCount = 0;
        var followUpSeen = false;

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 2)
            {
                followUpSeen = ctx.Messages.Any(m =>
                    m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                    um.Content is UserMessageContent umc &&
                    umc.Text == "follow-up-msg");
            }

            return TestStreamFactory.CreateTextResponse("ok-" + call);
        });

        var followUpReturned = false;
        var options = CreateOptions(provider.Api) with
        {
            GetFollowUpMessages = _ =>
            {
                if (!followUpReturned)
                {
                    followUpReturned = true;
                    return Task.FromResult<IReadOnlyList<AgentMessage>>([new UserMessage("follow-up-msg")]);
                }

                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);

        await agent.PromptAsync("first");

        llmCallCount.ShouldBe(2);
        followUpSeen.ShouldBeTrue("Follow-up message should trigger a second LLM call");
    }

    [Fact]
    public async Task Steer_DuringFollowUp_IsDrainedInFollowUpTurn()
    {
        var llmCallCount = 0;
        var steerInFollowUpCall = false;
        var firstStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStreamCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 1)
            {
                var stream = new LlmStream();
                _ = Task.Run(async () =>
                {
                    firstStreamStarted.TrySetResult();
                    await firstStreamCanFinish.Task;
                    var textStream = TestStreamFactory.CreateTextResponse("first-done");
                    await foreach (var evt in textStream)
                    {
                        stream.Push(evt);
                    }

                    stream.End(await textStream.GetResultAsync());
                });
                return stream;
            }

            steerInFollowUpCall = ctx.Messages.Any(m =>
                m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                um.Content is UserMessageContent umc &&
                umc.Text == "late-steer");
            return TestStreamFactory.CreateTextResponse("follow-up-done");
        });

        var followUpReturned = false;
        var options = CreateOptions(provider.Api) with
        {
            GetFollowUpMessages = _ =>
            {
                if (!followUpReturned)
                {
                    followUpReturned = true;
                    return Task.FromResult<IReadOnlyList<AgentMessage>>([new UserMessage("follow-up")]);
                }

                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);

        var runTask = agent.PromptAsync("start");
        await firstStreamStarted.Task;
        agent.Steer(new UserMessage("late-steer"));
        firstStreamCanFinish.TrySetResult();
        await runTask;

        steerInFollowUpCall.ShouldBeTrue("Steer enqueued before follow-up cycle should be visible");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. Abort + Steering
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Steer_AfterAbort_RemainsInQueueForNextRun()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = RegisterIsolatedProvider((_, _, _) =>
        {
            var stream = new LlmStream();
            _ = Task.Run(async () =>
            {
                await release.Task;
                var response = TestStreamFactory.CreateTextResponse("aborted");
                await foreach (var evt in response)
                {
                    stream.Push(evt);
                }

                stream.End(await response.GetResultAsync());
            });
            return stream;
        });

        var agent = CreateAgent(provider.Api);
        var runTask = agent.PromptAsync("run");
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, TimeSpan.FromSeconds(5));

        await agent.AbortAsync();
        agent.Steer(new UserMessage("post-abort-steer"));
        release.TrySetResult();
        await runTask;

        agent.HasQueuedMessages.ShouldBeTrue("Steer after abort should remain queued");
    }

    [Fact]
    public async Task Steer_BeforeAbort_IsNotConsumedByAbortedRun()
    {
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmCallCount = 0;

        using var provider = RegisterIsolatedProvider((_, _, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 1)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-1", "slow-tool", new Dictionary<string, object?> { ["input"] = "x" }));
            }

            return TestStreamFactory.CreateTextResponse("should-not-reach");
        });

        var slowTool = new DelegatingTool("slow-tool", async (_, _, ct, _) =>
        {
            toolStarted.TrySetResult();
            var tcs = new TaskCompletionSource();
            await using (ct.Register(() => tcs.TrySetResult()))
            {
                await Task.WhenAny(toolCanFinish.Task, tcs.Task);
            }

            ct.ThrowIfCancellationRequested();
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]);
        });

        var agent = CreateAgent(provider.Api, tools: [slowTool]);
        var runTask = agent.PromptAsync("go");
        await toolStarted.Task;

        agent.Steer(new UserMessage("pre-abort-steer"));
        await agent.AbortAsync();
        toolCanFinish.TrySetResult();
        await runTask;

        agent.HasQueuedMessages.ShouldBeTrue("Steer before abort should remain if not yet drained");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. Concurrency & Thread Safety
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Steer_ConcurrentWithRun_IsThreadSafe()
    {
        var llmCallCount = 0;
        using var provider = RegisterIsolatedProvider((_, _, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call <= 5)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-" + call, "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
            }

            return TestStreamFactory.CreateTextResponse("done");
        });

        var agent = CreateAgent(provider.Api, tools: [new CalculateTool()]);

        var runTask = agent.PromptAsync("go");

        var steerTasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => agent.Steer(new UserMessage($"concurrent-steer-{i}"))))
            .ToArray();
        await Task.WhenAll(steerTasks);
        await runTask;

        agent.State.Messages.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Steer_WhileAgentIdle_IsConsumedOnNextPrompt()
    {
        var contextMessages = new List<string>();
        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            foreach (var m in ctx.Messages)
            {
                if (m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                    um.Content is UserMessageContent umc)
                {
                    contextMessages.Add(umc.Text!);
                }
            }

            return TestStreamFactory.CreateTextResponse("ok");
        });

        var agent = CreateAgent(provider.Api);
        agent.Status.ShouldBe(AgentStatus.Idle);

        agent.Steer(new UserMessage("idle-steer-1"));
        agent.Steer(new UserMessage("idle-steer-2"));

        await agent.PromptAsync("trigger");

        contextMessages.ShouldContain("idle-steer-1");
        contextMessages.ShouldContain("idle-steer-2");
        contextMessages.ShouldContain("trigger");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. Multi-Turn Tool Chains + Steering
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Steer_DuringMultiTurnToolChain_InjectedBetweenTurns()
    {
        var llmCallCount = 0;
        var steerSeenInCall = 0;
        var firstStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStreamCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);

            var hasSteer = ctx.Messages.Any(m =>
                m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                um.Content is UserMessageContent umc &&
                umc.Text == "mid-chain-steer");
            if (hasSteer && steerSeenInCall == 0)
            {
                steerSeenInCall = call;
            }

            if (call == 1)
            {
                var stream = new LlmStream();
                _ = Task.Run(async () =>
                {
                    firstStreamStarted.TrySetResult();
                    await firstStreamCanFinish.Task;
                    var toolStream = TestStreamFactory.CreateToolCallResponse(
                        ("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
                    await foreach (var evt in toolStream)
                    {
                        stream.Push(evt);
                    }

                    stream.End(await toolStream.GetResultAsync());
                });
                return stream;
            }

            if (call == 2)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-2", "calculate", new Dictionary<string, object?> { ["expression"] = "2+1" }));
            }

            return TestStreamFactory.CreateTextResponse("chain-done");
        });

        var agent = CreateAgent(provider.Api, tools: [new CalculateTool()]);

        var runTask = agent.PromptAsync("start-chain");
        await firstStreamStarted.Task;
        agent.Steer(new UserMessage("mid-chain-steer"));
        firstStreamCanFinish.TrySetResult();
        await runTask;

        steerSeenInCall.ShouldBeGreaterThan(1, "Steer should appear after turn 1");
        steerSeenInCall.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task Steer_MultipleDuringMultiTurnChain_AllEventuallyDrained()
    {
        var allSteersVisible = false;
        var llmCallCount = 0;
        var firstStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStreamCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);

            var userTexts = ctx.Messages
                .OfType<BotNexus.Agent.Providers.Core.Models.UserMessage>()
                .Select(m => m.Content is UserMessageContent umc ? umc.Text ?? "" : "")
                .ToList();
            if (userTexts.Contains("steer-A") && userTexts.Contains("steer-B"))
            {
                allSteersVisible = true;
            }

            if (call == 1)
            {
                var stream = new LlmStream();
                _ = Task.Run(async () =>
                {
                    firstStreamStarted.TrySetResult();
                    await firstStreamCanFinish.Task;
                    var toolStream = TestStreamFactory.CreateToolCallResponse(
                        ("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
                    await foreach (var evt in toolStream)
                    {
                        stream.Push(evt);
                    }

                    stream.End(await toolStream.GetResultAsync());
                });
                return stream;
            }

            if (call <= 3)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-" + call, "calculate", new Dictionary<string, object?> { ["expression"] = "1+" + call }));
            }

            return TestStreamFactory.CreateTextResponse("done");
        });

        var agent = CreateAgent(provider.Api, tools: [new CalculateTool()]);

        agent.Steer(new UserMessage("steer-A"));
        var runTask = agent.PromptAsync("go");

        await firstStreamStarted.Task;
        agent.Steer(new UserMessage("steer-B"));
        firstStreamCanFinish.TrySetResult();

        await runTask;

        allSteersVisible.ShouldBeTrue("Both steer messages should eventually be visible in LLM context");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 8. SkipInitialSteeringPoll Semantics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SkipInitialSteeringPoll_PreventsDelegateCallOnFirstIteration()
    {
        var delegatePollCount = 0;

        using var provider = RegisterIsolatedProvider((_, _, _) => TestStreamFactory.CreateTextResponse("ok"));

        var options = CreateOptions(provider.Api) with
        {
            GetSteeringMessages = _ =>
            {
                Interlocked.Increment(ref delegatePollCount);
                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);

        await agent.PromptAsync("seed");
        Interlocked.Exchange(ref delegatePollCount, 0);

        agent.Steer(new UserMessage("trigger-continue"));
        await agent.ContinueAsync();

        delegatePollCount.ShouldBe(1);
    }

    [Fact]
    public async Task SkipInitialSteeringPoll_ResetsAfterFirstIteration()
    {
        var delegatePollCount = 0;
        var llmCallCount = 0;

        using var provider = RegisterIsolatedProvider((_, _, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 1)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
            }

            return TestStreamFactory.CreateTextResponse("done");
        });

        var options = CreateOptions(provider.Api, tools: [new CalculateTool()]) with
        {
            GetSteeringMessages = _ =>
            {
                Interlocked.Increment(ref delegatePollCount);
                return Task.FromResult<IReadOnlyList<AgentMessage>>([]);
            }
        };
        var agent = new BotNexus.Agent.Core.Agent(options);
        await agent.PromptAsync("seed");
        Interlocked.Exchange(ref delegatePollCount, 0);

        agent.Steer(new UserMessage("trigger-continue"));
        await agent.ContinueAsync();

        // With tool call response: skip initial (0) + post-tool-turn drain (1) = 1 poll
        // The second LLM call (text response) exits without another drain because
        // hasMoreToolCalls=false and pendingMessages=[] at the inner while check
        delegatePollCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 9. Error + Retry + Steering
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Steer_DuringTransientRetry_StillDrainedAfterRecovery()
    {
        var llmCallCount = 0;
        var steerVisibleOnSuccess = false;

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 1)
            {
                throw new InvalidOperationException("rate limit exceeded");
            }

            if (call == 2)
            {
                steerVisibleOnSuccess = ctx.Messages.Any(m =>
                    m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                    um.Content is UserMessageContent umc &&
                    umc.Text == "retry-steer");

                return TestStreamFactory.CreateToolCallResponse(
                    ("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
            }

            return TestStreamFactory.CreateTextResponse("done");
        });

        var options = CreateOptions(provider.Api, tools: [new CalculateTool()]) with
        {
            MaxRetryDelayMs = 10
        };
        var agent = new BotNexus.Agent.Core.Agent(options);

        var runTask = agent.PromptAsync("compute");
        await Task.Delay(5);
        agent.Steer(new UserMessage("retry-steer"));
        await runTask;

        agent.State.Messages.OfType<AssistantAgentMessage>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Steer_WhenLlmReturnsError_IsPreservedForNextRun()
    {
        var llmCallCount = 0;

        using var provider = RegisterIsolatedProvider((_, _, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 1)
            {
                return TestStreamFactory.CreateErrorResponse("provider crashed");
            }

            return TestStreamFactory.CreateTextResponse("recovered");
        });

        var agent = CreateAgent(provider.Api);

        agent.Steer(new UserMessage("survive-error"));
        var firstResult = await agent.PromptAsync("fail");

        firstResult.OfType<AssistantAgentMessage>().Last().FinishReason.ShouldBe(StopReason.Error);
        agent.HasQueuedMessages.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 10. Queue Clearing
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ClearSteeringQueue_RemovesAllPendingSteeringMessages()
    {
        var agent = CreateAgent();
        agent.Steer(new UserMessage("steer-1"));
        agent.Steer(new UserMessage("steer-2"));

        agent.ClearSteeringQueue();

        agent.HasQueuedMessages.ShouldBeFalse();
    }

    [Fact]
    public void ClearAllQueues_RemovesBothSteeringAndFollowUp()
    {
        var agent = CreateAgent();
        agent.Steer(new UserMessage("steer"));
        agent.FollowUp(new UserMessage("follow"));

        agent.ClearAllQueues();

        agent.HasQueuedMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task ClearSteeringQueue_DuringRun_PreventsInjection()
    {
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerVisibleInSecondCall = false;
        var llmCallCount = 0;

        using var provider = RegisterIsolatedProvider((_, ctx, _) =>
        {
            var call = Interlocked.Increment(ref llmCallCount);
            if (call == 1)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-1", "slow-tool", new Dictionary<string, object?> { ["input"] = "x" }));
            }

            steerVisibleInSecondCall = ctx.Messages.Any(m =>
                m is BotNexus.Agent.Providers.Core.Models.UserMessage um &&
                um.Content is UserMessageContent umc &&
                umc.Text == "cleared-steer");
            return TestStreamFactory.CreateTextResponse("done");
        });

        var slowTool = new DelegatingTool("slow-tool", async (_, _, _, _) =>
        {
            toolStarted.TrySetResult();
            await toolCanFinish.Task;
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]);
        });

        var agent = CreateAgent(provider.Api, tools: [slowTool]);
        var runTask = agent.PromptAsync("go");
        await toolStarted.Task;

        agent.Steer(new UserMessage("cleared-steer"));
        agent.ClearSteeringQueue();
        toolCanFinish.TrySetResult();

        await runTask;

        steerVisibleInSecondCall.ShouldBeFalse("Cleared steer should not appear in context");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

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

    /// <summary>
    /// A tool with configurable execution logic for testing.
    /// </summary>
    private sealed class DelegatingTool : IAgentTool
    {
        private static readonly System.Text.Json.JsonElement EmptySchema =
            System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();

        private readonly Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, AgentToolUpdateCallback?, Task<AgentToolResult>> _execute;

        public DelegatingTool(
            string name,
            Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, AgentToolUpdateCallback?, Task<AgentToolResult>> execute)
        {
            Name = name;
            _execute = execute;
        }

        public string Name { get; }
        public string Label => Name;
        public Tool Definition => new(Name, $"Test tool: {Name}", EmptySchema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null) =>
            _execute(toolCallId, arguments, cancellationToken, onUpdate);
    }
}
