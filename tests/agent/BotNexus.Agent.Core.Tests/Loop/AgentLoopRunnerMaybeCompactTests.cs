using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Tests for the optional mid-loop auto-compaction hook (#1710). A single long
/// dispatch (cron/autonomous follow-up loop) used to grow unbounded because
/// ShouldCompact ran only pre-turn at the gateway; the agent loop never re-checked
/// between provider turns. The optional best-effort
/// <see cref="AgentLoopConfig.MaybeCompactAsync"/> is awaited after completed tool
/// results and before every provider call, so inner tool chains and outer follow-ups
/// share one safe compaction boundary and the loop continues if the hook throws.
/// </summary>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerMaybeCompactTests
{
    private sealed class LargeResultTool : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{ "type": "object", "properties": {} }""").RootElement.Clone();

        public string Name => "large_result";
        public string Label => "Large result";
        public Tool Definition => new(Name, "Returns a large bounded synthetic result", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, new string('x', 32_000))]));
    }

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
            "the loop must re-check compaction before its provider turn");
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

        // First iteration + the follow-up-driven second iteration each reach the shared
        // pre-provider boundary, so a long multi-turn dispatch cannot blow past the threshold.
        compactCalls.ShouldBeGreaterThanOrEqualTo(2,
            "each outer-loop iteration must re-check compaction so a long dispatch is bounded");
    }

    [Fact]
    public async Task RunAsync_ToolResultCrossesThreshold_CompactsBeforeNextProviderTurn()
    {
        const string apiId = "maybe-compact-inner-tool-turn";
        var providerContexts = new List<Context>();
        var providerCall = 0;
        using var provider = RegisterProvider(apiId, (_, context, _) =>
        {
            providerContexts.Add(context);
            return Interlocked.Increment(ref providerCall) == 1
                ? TestStreamFactory.CreateToolCallResponse(("call-1", "large_result", new Dictionary<string, object?>()))
                : TestStreamFactory.CreateTextResponse("done");
        });

        var tool = new LargeResultTool();
        var compactChecks = 0;
        var refreshed = new AgentContext(
            "durable compaction summary",
            [new AgentUserMessage("retained tail")],
            [tool]);
        var config = CreateConfig(apiId, _ => Task.FromResult<AgentContext?>(
            Interlocked.Increment(ref compactChecks) == 1 ? null : refreshed));

        _ = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("start below threshold")],
            new AgentContext("original prompt", [], [tool]),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        compactChecks.ShouldBe(2,
            "the loop must re-check after completed tool results and before the provider continuation");
        providerContexts.Count.ShouldBe(2);
        providerContexts[1].SystemPrompt.ShouldBe("durable compaction summary");
        providerContexts[1].Messages.OfType<BotNexus.Agent.Providers.Core.Models.UserMessage>()
            .Select(message => message.Content.Text)
            .ShouldBe(["retained tail"]);
        providerContexts[1].Messages.OfType<ToolResultMessage>().ShouldBeEmpty(
            "the next provider request must use the refreshed compacted snapshot, not stale tool output");
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
    public async Task RunAsync_WhenMaybeCompactThrows_DiagnosesAndContinues()
    {
        using var provider = RegisterProvider("maybe-compact-throws", (_, _, _) =>
            TestStreamFactory.CreateTextResponse("survived"));

        var diagnostics = new List<string>();
        var config = CreateConfig("maybe-compact-throws",
            _ => Task.FromException<AgentContext?>(new InvalidOperationException("compactor boom"))) with
        {
            OnDiagnostic = diagnostics.Add
        };
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
        diagnostics.ShouldContain(message =>
            message.Contains("Proactive durable compaction failed", StringComparison.Ordinal)
            && message.Contains("compactor boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WhenRequiredProactiveCompactionFails_DoesNotCallProvider()
    {
        var providerCalls = 0;
        using var provider = RegisterProvider("maybe-compact-required-failure", (_, _, _) =>
        {
            Interlocked.Increment(ref providerCalls);
            return TestStreamFactory.CreateTextResponse("must not run");
        });

        var config = CreateConfig(
            "maybe-compact-required-failure",
            _ => Task.FromException<AgentContext?>(new ProactiveCompactionException(
                "Required proactive compaction failed.",
                retryable: true)));

        Func<Task> act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("oversized context")],
            new AgentContext(null, [], []),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        var exception = await act.ShouldThrowAsync<ProactiveCompactionException>();
        exception.Retryable.ShouldBeTrue();
        providerCalls.ShouldBe(0, "a required compaction failure must fail closed before provider invocation");
    }

    [Fact]
    public async Task RunAsync_WhenTerminalProviderMessageReportsOverflow_CompactsOnceAndRetries()
    {
        const string apiId = "in-band-context-overflow";
        var providerCalls = 0;
        var observedContextCounts = new List<int>();
        using var provider = RegisterProvider(apiId, (_, context, _) =>
        {
            observedContextCounts.Add(context.Messages.Count);
            return Interlocked.Increment(ref providerCalls) == 1
                ? TestStreamFactory.CreateErrorResponse("input is too long for requested model")
                : TestStreamFactory.CreateTextResponse("recovered");
        });

        var history = Enumerable.Range(0, 15)
            .Select(index => (AgentMessage)new AgentUserMessage($"message-{index}"))
            .ToList();
        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("latest")],
            new AgentContext(null, history, []),
            CreateConfig(apiId, _ => Task.FromResult<AgentContext?>(null)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        providerCalls.ShouldBe(2, "an in-band overflow gets exactly one reactive recovery attempt");
        observedContextCounts[1].ShouldBeLessThan(observedContextCounts[0]);
        result.OfType<AssistantAgentMessage>().ShouldContain(message => message.Content == "recovered");
        result.OfType<AssistantAgentMessage>().ShouldNotContain(message =>
            message.ErrorMessage != null
            && message.ErrorMessage.Contains("input is too long", StringComparison.Ordinal));
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
