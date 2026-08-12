using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Resilience;

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

    // ----- #3035: bounded jitter + a default (non-null) retry-delay ceiling -----

    /// <summary>
    /// AC1 - the deterministic bound. With the randomness source pinned to <c>0</c> the loop must
    /// reproduce the pre-existing 500/1000/2000ms sequence exactly, which is what makes adding jitter
    /// a safe change rather than a schedule rewrite.
    /// </summary>
    [Fact]
    public void ComputeRetryDelayMs_RandomPinnedToZero_ReproducesTheHistoricalBackoffSequence()
    {
        var config = CreateConfig("jitter-zero", maxRetryDelayMs: null) with { RetryRandomSource = () => 0d };

        AgentLoopRunner.ComputeRetryDelayMs(500, retryAfter: null, config).ShouldBe(500);
        AgentLoopRunner.ComputeRetryDelayMs(1000, retryAfter: null, config).ShouldBe(1000);
        AgentLoopRunner.ComputeRetryDelayMs(2000, retryAfter: null, config).ShouldBe(2000);
    }

    /// <summary>
    /// AC2 - the jittered bound. Pinned to the maximum, every delay must be strictly longer than the
    /// un-jittered value (proving the term is actually applied - a range assertion could not) and no
    /// more than <c>(1 + jitterFactor)</c> times it (proving it is bounded).
    /// </summary>
    [Theory]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(2000)]
    public void ComputeRetryDelayMs_RandomPinnedToMax_IsStrictlyLongerAndBoundedByTheJitterFactor(int backoffMs)
    {
        var config = CreateConfig("jitter-max", maxRetryDelayMs: null) with { RetryRandomSource = () => 1d };

        var delay = AgentLoopRunner.ComputeRetryDelayMs(backoffMs, retryAfter: null, config);

        delay.ShouldBeGreaterThan(backoffMs);
        delay.ShouldBeLessThanOrEqualTo((int)(backoffMs * (1 + RetryJitter.DefaultJitterFactor)));
    }

    /// <summary>
    /// AC4 - an unconfigured <see cref="AgentLoopConfig"/> must produce a <em>bounded</em> delay. Before
    /// #3035 <c>MaxRetryDelayMs</c> defaulted to null, documented as uncapped, so this assertion had no
    /// ceiling to hold it up.
    /// </summary>
    [Fact]
    public void AgentLoopConfig_Unconfigured_HasANonNullCeilingAndYieldsABoundedDelay()
    {
        var config = CreateDefaultCeilingConfig("default-ceiling");

        config.MaxRetryDelayMs.ShouldNotBeNull();
        config.MaxRetryDelayMs.Value.ShouldBe(AgentLoopConfig.DefaultMaxRetryDelayMs);
        config.EffectiveMaxRetryDelayMs.ShouldBe(AgentLoopConfig.DefaultMaxRetryDelayMs);

        AgentLoopRunner.ComputeRetryDelayMs(int.MaxValue, retryAfter: null, config)
            .ShouldBeLessThanOrEqualTo(AgentLoopConfig.DefaultMaxRetryDelayMs);
    }

    /// <summary>
    /// AC4 (sad path) - a caller that explicitly passes the old <c>null</c> "uncapped" value must still be
    /// bounded. If null silently restored the uncapped path the default would be cosmetic.
    /// </summary>
    [Fact]
    public void ComputeRetryDelayMs_ExplicitNullCeiling_StillFallsBackToTheDefaultCeiling()
    {
        var config = CreateConfig("null-ceiling", maxRetryDelayMs: null);

        config.EffectiveMaxRetryDelayMs.ShouldBe(AgentLoopConfig.DefaultMaxRetryDelayMs);
        AgentLoopRunner.ComputeRetryDelayMs(int.MaxValue, retryAfter: null, config)
            .ShouldBeLessThanOrEqualTo(AgentLoopConfig.DefaultMaxRetryDelayMs);
    }

    /// <summary>
    /// AC5 - an absurd server-supplied <c>Retry-After</c> is clamped to the ceiling instead of being
    /// honoured verbatim. This is the hostile-header case: previously a single upstream value could park
    /// the turn for as long as it asked.
    /// </summary>
    [Fact]
    public void ComputeRetryDelayMs_AbsurdRetryAfter_IsClampedToTheCeiling()
    {
        var config = CreateDefaultCeilingConfig("absurd-retry-after");

        var delay = AgentLoopRunner.ComputeRetryDelayMs(
            500, retryAfter: TimeSpan.FromHours(9), config);

        delay.ShouldBe(AgentLoopConfig.DefaultMaxRetryDelayMs);
    }

    /// <summary>
    /// AC5 (happy path) - a <c>Retry-After</c> comfortably under the ceiling is still honoured exactly.
    /// The clamp must bound the pathological case without overriding the provider's normal instruction.
    /// </summary>
    [Fact]
    public void ComputeRetryDelayMs_ReasonableRetryAfter_IsHonouredVerbatim()
    {
        var config = CreateDefaultCeilingConfig("reasonable-retry-after") with { RetryRandomSource = () => 1d };

        AgentLoopRunner.ComputeRetryDelayMs(500, TimeSpan.FromSeconds(5), config).ShouldBe(5000);
    }

    /// <summary>
    /// A negative <c>Retry-After</c> must not become a negative delay - <c>Task.Delay</c> would throw and
    /// turn a transient provider hiccup into a hard turn failure.
    /// </summary>
    [Fact]
    public void ComputeRetryDelayMs_NegativeRetryAfter_IsFlooredAtZero()
    {
        var config = CreateDefaultCeilingConfig("negative-retry-after");

        AgentLoopRunner.ComputeRetryDelayMs(500, TimeSpan.FromSeconds(-30), config).ShouldBe(0);
    }

    /// <summary>
    /// A tight explicitly-configured ceiling still wins over the jittered backoff, so the ceiling is a
    /// hard bound applied after jitter rather than one the jitter can overshoot.
    /// </summary>
    [Fact]
    public void ComputeRetryDelayMs_JitterNeverOvershootsAnExplicitCeiling()
    {
        var config = CreateConfig("tight-ceiling", maxRetryDelayMs: 600) with { RetryRandomSource = () => 1d };

        AgentLoopRunner.ComputeRetryDelayMs(2000, retryAfter: null, config).ShouldBe(600);
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

    /// <summary>
    /// Builds a config that leaves <c>MaxRetryDelayMs</c> at its record default, so the test observes the
    /// shipped default rather than a value the test itself supplied. <see cref="CreateConfig"/> passes an
    /// explicit ceiling and therefore cannot prove anything about the default.
    /// </summary>
    private static AgentLoopConfig CreateDefaultCeilingConfig(string apiId)
    {
        return new AgentLoopConfig(
            Model: TestHelpers.CreateTestModel(apiId),
            LlmClient: TestHelpers.CreateLlmClient(),
            ConvertToLlm: (messages, _) => Task.FromResult<IReadOnlyList<Message>>([]),
            TransformContext: (messages, _) => Task.FromResult(messages),
            GetApiKey: (_, _) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions());
    }

    private static IDisposable RegisterProvider(string apiId,
        Func<LlmModel, Context, SimpleStreamOptions?, BotNexus.Agent.Providers.Core.Streaming.LlmStream> factory)
    {
        var provider = new TestApiProvider(apiId, simpleStreamFactory: factory);
        return TestHelpers.RegisterProvider(provider);
    }

    #endregion
}
