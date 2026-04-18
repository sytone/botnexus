using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.AgentCore.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.AgentCore.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Tests for retry, overflow compaction, and transient error handling in AgentLoopRunner.
/// </summary>
public class AgentLoopRunnerEdgeCaseTests
{
    // --- Transient error retry tests ---

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("too many requests")]
    [InlineData("timeout")]
    [InlineData("temporarily unavailable")]
    [InlineData("service unavailable")]
    [InlineData("429")]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    [InlineData("Rate Limit Exceeded")] // case insensitive
    [InlineData("HTTP error 429: Too Many Requests")]
    [InlineData("upstream server returned 502")]
    public async Task RunAsync_TransientErrors_AreRetried(string errorMessage)
    {
        var attempts = 0;
        using var _ = RegisterProvider("transient-test", (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
                throw new InvalidOperationException(errorMessage);

            return TestStreamFactory.CreateTextResponse("recovered");
        });

        var config = CreateConfig("transient-test");
        var context = new AgentContext(null, [], []);
        var events = new List<AgentEvent>();

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            context,
            config,
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        attempts.Should().BeGreaterThan(1, $"transient error '{errorMessage}' should trigger retry");
        result.OfType<AssistantAgentMessage>().Should().Contain(m => m.Content == "recovered");
    }

    [Theory]
    [InlineData("invalid request")]
    [InlineData("authentication failed")]
    [InlineData("model not found")]
    [InlineData("")] // empty message
    public async Task RunAsync_NonTransientErrors_AreNotRetried(string errorMessage)
    {
        var attempts = 0;
        using var _ = RegisterProvider("non-transient-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException(errorMessage);
        });

        var config = CreateConfig("non-transient-test");
        var context = new AgentContext(null, [], []);

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            context,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1, $"non-transient error '{errorMessage}' should not retry");
    }

    [Fact]
    public async Task RunAsync_MaxRetryAttempts_ExhaustedAfter4Tries()
    {
        var attempts = 0;
        using var _ = RegisterProvider("max-retry-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("rate limit exceeded");
        });

        var config = CreateConfig("max-retry-test");
        var context = new AgentContext(null, [], []);

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            context,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(4, "should exhaust all 4 retry attempts");
    }

    // --- Context overflow compaction tests ---

    [Fact]
    public async Task RunAsync_ContextOverflow_CompactsAndRetries()
    {
        var attempts = 0;
        using var _ = RegisterProvider("overflow-test", (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("context length exceeded");

            return TestStreamFactory.CreateTextResponse("after-compaction");
        });

        var config = CreateConfig("overflow-test");
        // Pre-fill context with many messages to exercise compaction logic
        var messages = new List<AgentMessage>();
        for (int i = 0; i < 20; i++)
            messages.Add(new AgentUserMessage($"message-{i}"));
        var context = new AgentContext(null, messages, []);

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("trigger")],
            context,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        attempts.Should().Be(2, "should retry after overflow compaction");
        result.OfType<AssistantAgentMessage>().Should().Contain(m => m.Content == "after-compaction");
    }

    [Fact]
    public async Task RunAsync_ContextOverflow_OnlyRecoversOnce()
    {
        var attempts = 0;
        using var _ = RegisterProvider("double-overflow-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("context length exceeded");
        });

        var config = CreateConfig("double-overflow-test");
        var context = new AgentContext(null, [], []);

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            context,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        // First attempt triggers overflow, second attempt also overflows but recovery already used
        attempts.Should().Be(2, "overflow recovery only happens once");
    }

    // --- ContinueAsync validation tests ---

    [Fact]
    public async Task ContinueAsync_WithEmptyContext_ThrowsInvalidOperation()
    {
        var config = CreateConfig("continue-empty-test");
        var context = new AgentContext(null, [], []);

        var act = () => AgentLoopRunner.ContinueAsync(context, config, _ => Task.CompletedTask, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no messages*");
    }

    [Fact]
    public async Task ContinueAsync_WithLastMessageFromAssistant_ThrowsInvalidOperation()
    {
        var config = CreateConfig("continue-assistant-test");
        var messages = new List<AgentMessage>
        {
            new AgentUserMessage("hello"),
            new AssistantAgentMessage("hi there", FinishReason: StopReason.Stop)
        };
        var context = new AgentContext(null, messages, []);

        var act = () => AgentLoopRunner.ContinueAsync(context, config, _ => Task.CompletedTask, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*last message is from the assistant*");
    }

    // --- Cancellation tests ---

    [Fact]
    public async Task RunAsync_WhenCancelled_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        using var _ = RegisterProvider("cancel-test", (_, _, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException();
        });

        var config = CreateConfig("cancel-test");
        var context = new AgentContext(null, [], []);

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            context,
            config,
            _ => Task.CompletedTask,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #region Helpers

    private static AgentLoopConfig CreateConfig(string apiId)
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
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions(),
            MaxRetryDelayMs: 1); // Fast retries for tests
    }

    private static IDisposable RegisterProvider(string apiId,
        Func<LlmModel, Context, SimpleStreamOptions?, BotNexus.Agent.Providers.Core.Streaming.LlmStream> factory)
    {
        var provider = new TestApiProvider(apiId, simpleStreamFactory: factory);
        return TestHelpers.RegisterProvider(provider);
    }

    #endregion
}
