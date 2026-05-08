using BotNexus.Agent.Core.Tests.TestUtils;
using System.Diagnostics;

namespace BotNexus.Agent.Core.Tests;

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

        attempts.ShouldBe(4);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }
}
