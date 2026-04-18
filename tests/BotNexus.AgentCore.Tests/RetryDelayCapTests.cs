using BotNexus.AgentCore.Tests.TestUtils;
using FluentAssertions;
using System.Diagnostics;

namespace BotNexus.AgentCore.Tests;

public sealed class RetryDelayCapTests
{
    [Fact]
    public async Task PromptAsync_WhenRetryDelayCapConfigured_DoesNotExceedConfiguredCap()
    {
        const string api = "retry-cap-api";
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
                MaxRetryDelayMs = 100
            };
        var agent = new BotNexus.Agent.Core.Agent(options);
        var stopwatch = Stopwatch.StartNew();

        _ = await agent.PromptAsync("retry please");
        stopwatch.Stop();

        attempts.Should().Be(4);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }
}
