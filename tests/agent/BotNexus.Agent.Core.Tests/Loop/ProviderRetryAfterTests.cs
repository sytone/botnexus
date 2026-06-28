using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Tests for ProviderRateLimitException and Retry-After header handling in the agent loop.
/// </summary>
[Collection(ApiProviderRegistryCollection.Name)]
public class ProviderRetryAfterTests
{
    [Fact]
    public async Task RunAsync_ProviderRateLimitException_IsRetried()
    {
        var attempts = 0;
        using var _ = RegisterProvider("ratelimit-test", (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new ProviderRateLimitException("429: rate limited", 429, TimeSpan.FromMilliseconds(10));

            return TestStreamFactory.CreateTextResponse("recovered");
        });

        var config = CreateConfig("ratelimit-test");
        var context = new AgentContext(null, [], []);

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            context,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        attempts.ShouldBeGreaterThan(1, "ProviderRateLimitException should trigger retry");
        result.OfType<AssistantAgentMessage>().ShouldContain(m => m.Content == "recovered");
    }

    [Fact]
    public async Task RunAsync_ProviderRateLimitWithRetryAfter_UsesSpecifiedDelay()
    {
        var attempts = 0;
        var timestamps = new List<DateTimeOffset>();
        using var _ = RegisterProvider("delay-test", (_, _, _) =>
        {
            timestamps.Add(DateTimeOffset.UtcNow);
            if (Interlocked.Increment(ref attempts) == 1)
                throw new ProviderRateLimitException("429: rate limited", 429, TimeSpan.FromMilliseconds(50));

            return TestStreamFactory.CreateTextResponse("ok");
        });

        // Don't cap retry delay -- let the RetryAfter value be used
        var config = CreateConfig("delay-test", maxRetryDelayMs: null);

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        attempts.ShouldBe(2);
        result.OfType<AssistantAgentMessage>().ShouldContain(m => m.Content == "ok");
        // The gap between attempts should be at least ~50ms (the RetryAfter value)
        var gap = timestamps[1] - timestamps[0];
        gap.TotalMilliseconds.ShouldBeGreaterThan(40); // Allow some timing slack
    }

    [Fact]
    public async Task RunAsync_ProviderRateLimitWithNullRetryAfter_FallsBackToExponentialBackoff()
    {
        var attempts = 0;
        using var _ = RegisterProvider("null-retry-test", (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
                throw new ProviderRateLimitException("429: rate limited", 429, retryAfter: null);

            return TestStreamFactory.CreateTextResponse("recovered");
        });

        var config = CreateConfig("null-retry-test");

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        attempts.ShouldBe(3);
        result.OfType<AssistantAgentMessage>().ShouldContain(m => m.Content == "recovered");
    }

    [Fact]
    public void ProviderRateLimitException_InheritsFromHttpRequestException()
    {
        var ex = new ProviderRateLimitException("test", 429, TimeSpan.FromSeconds(5));
        ex.ShouldBeAssignableTo<HttpRequestException>();
        ex.StatusCode.ShouldBe(System.Net.HttpStatusCode.TooManyRequests);
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_ProviderAuthenticationException_IsNotRetried_AndPropagatesActionableMessage()
    {
        // A 401/auth failure is terminal -- retrying with the same bad key is pointless.
        // Unlike a rate-limit, it is NOT classified as transient, so the loop attempts exactly
        // once and propagates the (actionable) exception for the surfacing layer (Agent.cs) to
        // turn into a StopReason.Error message. This is the inverse of the rate-limit retry test.
        var attempts = 0;
        using var _ = RegisterProvider("auth-fail-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new ProviderAuthenticationException(
                "Authentication failed for provider 'auth-fail-test' (HTTP 401): the provider rejected your credentials. Check or rotate the API key.",
                401,
                "auth-fail-test");
        });

        var config = CreateConfig("auth-fail-test");

        var ex = await Should.ThrowAsync<ProviderAuthenticationException>(async () =>
            await AgentLoopRunner.RunAsync(
                [new AgentUserMessage("test")],
                new AgentContext(null, [], []),
                config,
                _ => Task.CompletedTask,
                CancellationToken.None));

        attempts.ShouldBe(1, "a 401 auth failure must not be retried");
        ex.ProviderName.ShouldBe("auth-fail-test");
        ex.Message.ShouldContain("auth-fail-test");
        ex.Message.ShouldContain("API key");
    }

    [Fact]
    public void ProviderAuthenticationException_InheritsFromHttpRequestException()
    {
        var ex = new ProviderAuthenticationException("bad creds", 401, "OpenAI");
        ex.ShouldBeAssignableTo<HttpRequestException>();
        ex.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        ex.ProviderName.ShouldBe("OpenAI");
    }

    [Theory]
    [InlineData("5", 5000)]
    [InlineData("30", 30000)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("999", 120000)] // Capped at 2 minutes
    public void ParseRetryAfterHeader_DeltaSeconds_ReturnsExpected(string? headerValue, int? expectedMs)
    {
        var result = ProviderRateLimitException.ParseRetryAfterHeader(headerValue);
        if (expectedMs is null)
            result.ShouldBeNull();
        else
            result!.Value.TotalMilliseconds.ShouldBe(expectedMs.Value);
    }

    #region Helpers

    private static AgentLoopConfig CreateConfig(string apiId, int? maxRetryDelayMs = 1)
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
            MaxRetryDelayMs: maxRetryDelayMs);
    }

    private static IDisposable RegisterProvider(string apiId,
        Func<LlmModel, Context, SimpleStreamOptions?, BotNexus.Agent.Providers.Core.Streaming.LlmStream> factory)
    {
        var provider = new TestApiProvider(apiId, simpleStreamFactory: factory);
        return TestHelpers.RegisterProvider(provider);
    }

    #endregion
}
