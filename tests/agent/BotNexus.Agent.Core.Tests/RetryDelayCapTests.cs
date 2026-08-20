using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Core.Types;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Agent.Core.Tests;

/// <summary>
/// Covers the transient-retry delay ceiling (<c>MaxRetryDelayMs</c>).
/// <para>
/// #3434: this file previously asserted <c>stopwatch.Elapsed &lt; 2s</c> around a full
/// <c>PromptAsync</c>. That literal was a proxy for "the backoff was capped", but what it actually
/// measured was the runner's scheduling: with a 100ms ceiling the intended sleep total is ~300ms, so
/// the remaining 1.7s of the budget was pure headroom for CI noise - and a contended hosted runner
/// still overshot it (observed 00:00:02.2228126). The property under test is the value the retry path
/// computes, not the wall-clock the machine happens to deliver, so it is now asserted directly against
/// the production <see cref="AgentLoopRunner.ComputeRetryDelayMs"/> at both pinned bounds of the
/// randomness source. That is strictly stronger than the old bound: it discriminates a capped schedule
/// from an uncapped one per-attempt rather than in aggregate, and it cannot flake.
/// </para>
/// </summary>
public sealed class RetryDelayCapTests
{
    /// <summary>The exponential backoff sequence the retry lane walks: 500 -> 1000 -> 2000ms.</summary>
    private static readonly int[] BackoffSequenceMs = [500, 1000, 2000];

    private const int CapMs = 100;

    /// <summary>
    /// The cap is a hard per-attempt bound. Every delay the loop would apply, at every step of the
    /// backoff sequence and at both extremes of the jitter source, must be at or under the configured
    /// ceiling. Pinning the randomness both ways matters: the ceiling is applied <em>after</em> jitter,
    /// so a max-pinned source is the case that could overshoot it.
    /// </summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    public void ComputeRetryDelayMs_WhenCapConfigured_EveryDelayInTheBackoffSequenceIsAtOrUnderTheCap(double random)
    {
        var config = CreateConfig("retry-cap-api", CapMs) with { RetryRandomSource = () => random };

        foreach (var backoffMs in BackoffSequenceMs)
        {
            AgentLoopRunner.ComputeRetryDelayMs(backoffMs, retryAfter: null, config)
                .ShouldBeLessThanOrEqualTo(CapMs);
        }
    }

    /// <summary>
    /// Non-vacuity guard. The assertion above would also hold if the cap were unreachable noise, so this
    /// pins the contrast: the SAME backoff sequence without the tight ceiling produces delays strictly
    /// larger than the cap. If the clamp stopped being applied, the capped and uncapped schedules would
    /// converge and this test goes red.
    /// </summary>
    [Fact]
    public void ComputeRetryDelayMs_WithoutTheCap_ProducesDelaysStrictlyLargerThanTheCappedSchedule()
    {
        var capped = CreateConfig("retry-cap-capped", CapMs) with { RetryRandomSource = () => 0d };
        var uncapped = CreateConfig("retry-cap-uncapped", AgentLoopConfig.DefaultMaxRetryDelayMs)
            with
            {
                RetryRandomSource = () => 0d
            };

        foreach (var backoffMs in BackoffSequenceMs)
        {
            var cappedDelay = AgentLoopRunner.ComputeRetryDelayMs(backoffMs, retryAfter: null, capped);
            var uncappedDelay = AgentLoopRunner.ComputeRetryDelayMs(backoffMs, retryAfter: null, uncapped);

            cappedDelay.ShouldBe(CapMs);
            uncappedDelay.ShouldBeGreaterThan(CapMs);
        }
    }

    /// <summary>
    /// A server-supplied <c>Retry-After</c> is clamped by the same ceiling. Without this the cap would be
    /// bypassable by the provider, which is the pathological case the ceiling exists to bound.
    /// </summary>
    [Fact]
    public void ComputeRetryDelayMs_WhenCapConfigured_ClampsAServerSuppliedRetryAfter()
    {
        var config = CreateConfig("retry-cap-retry-after", CapMs);

        AgentLoopRunner.ComputeRetryDelayMs(500, TimeSpan.FromMinutes(5), config).ShouldBe(CapMs);
    }

    /// <summary>
    /// The end-to-end half: a capped ceiling must not suppress the retries themselves. Four attempts
    /// (three transient failures then a success) proves the loop still walked the full retry lane while
    /// the cap was in force. No wall-clock assertion lives here any more - the delay values are the
    /// subject and they are asserted deterministically above.
    /// </summary>
    [Fact]
    public async Task PromptAsync_WhenRetryDelayCapConfigured_StillExhaustsTheRetryLane()
    {
        const string api = "retry-cap-e2e-api";
        var attempts = 0;
        using var provider = TestHelpers.RegisterProvider(
            new TestApiProvider(
                api,
                simpleStreamFactory: (_, _, _) =>
                {
                    attempts++;
                    if (attempts < 4)
                    {
                        throw new InvalidOperationException("429 rate limit");
                    }

                    return TestStreamFactory.CreateTextResponse("assistant");
                }));
        var options = TestHelpers.CreateTestOptions(model: TestHelpers.CreateTestModel(api))
            with
            {
                MaxRetryDelayMs = CapMs
            };
        var agent = new BotNexus.Agent.Core.Agent(options);

        _ = await agent.PromptAsync("retry please");

        attempts.ShouldBe(4);
    }

    private static AgentLoopConfig CreateConfig(string apiId, int? maxRetryDelayMs)
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
}
