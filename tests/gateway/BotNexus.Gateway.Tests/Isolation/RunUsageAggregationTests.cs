using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Isolation;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// #2641: run-level token aggregation, the seam that turns per-turn provider usage into the single
/// figure cron records as a run's cost.
/// </summary>
/// <remarks>
/// <para>
/// The distinction these tests protect is the one the whole feature rests on: <c>null</c> means
/// "the provider reported nothing", a number means "this is what it cost". Collapsing the former
/// into <c>0</c> would present every unmeasured run as free and rank it as the cheapest job on the
/// platform - the exact inversion #2641 was filed to fix.
/// </para>
/// <para>
/// They also pin that aggregation SUMS across turns rather than reporting the last turn only.
/// <c>AgentResponse.Usage</c> deliberately stays last-turn because the session compactor compares
/// the most recent prompt against its estimate; a 12-turn run reported as one turn would
/// under-report its cost by roughly an order of magnitude.
/// </para>
/// </remarks>
public sealed class RunUsageAggregationTests
{
    [Fact]
    public void AggregateRunUsage_NoAssistantMessages_ReturnsNull_NotZero()
    {
        var result = InProcessAgentHandle.AggregateRunUsage([]);

        result.ShouldBeNull("an unmeasured run must not present as a free run");
    }

    [Fact]
    public void AggregateRunUsage_AssistantMessagesWithNoUsage_ReturnsNull_NotZero()
    {
        IReadOnlyList<AgentMessage> messages =
        [
            new AssistantAgentMessage("first"),
            new AssistantAgentMessage("second")
        ];

        var result = InProcessAgentHandle.AggregateRunUsage(messages);

        result.ShouldBeNull();
    }

    /// <summary>
    /// A provider that genuinely reports zero is a MEASUREMENT and must survive as one, distinct
    /// from the null above.
    /// </summary>
    [Fact]
    public void AggregateRunUsage_ProviderReportedZero_IsMeasured_NotNull()
    {
        IReadOnlyList<AgentMessage> messages =
        [
            new AssistantAgentMessage("only", Usage: new AgentUsage(InputTokens: 0, OutputTokens: 0))
        ];

        var result = InProcessAgentHandle.AggregateRunUsage(messages);

        result.ShouldNotBeNull();
        result!.InputTokens.ShouldBe(0);
        result.OutputTokens.ShouldBe(0);
    }

    [Fact]
    public void AggregateRunUsage_SumsAcrossEveryTurn_NotJustTheLast()
    {
        IReadOnlyList<AgentMessage> messages =
        [
            new AssistantAgentMessage("turn 1", Usage: new AgentUsage(InputTokens: 1_000, OutputTokens: 100)),
            new AssistantAgentMessage("turn 2", Usage: new AgentUsage(InputTokens: 2_000, OutputTokens: 200)),
            new AssistantAgentMessage("turn 3", Usage: new AgentUsage(InputTokens: 4_000, OutputTokens: 300))
        ];

        var result = InProcessAgentHandle.AggregateRunUsage(messages);

        result.ShouldNotBeNull();
        result!.InputTokens.ShouldBe(7_000, "the run cost the sum of its turns, not the cost of its last turn");
        result.OutputTokens.ShouldBe(600);
    }

    /// <summary>
    /// Cache reads/writes are billed parts of the prompt the model saw. Omitting them would
    /// under-report cache-heavy providers by most of the prompt.
    /// </summary>
    [Fact]
    public void AggregateRunUsage_FoldsCacheReadAndWriteIntoThePromptSide()
    {
        IReadOnlyList<AgentMessage> messages =
        [
            new AssistantAgentMessage(
                "cached turn",
                Usage: new AgentUsage(InputTokens: 500, OutputTokens: 50, CacheRead: 40_000, CacheWrite: 1_500))
        ];

        var result = InProcessAgentHandle.AggregateRunUsage(messages);

        result.ShouldNotBeNull();
        result!.InputTokens.ShouldBe(42_000);
        result.OutputTokens.ShouldBe(50);
    }

    /// <summary>
    /// A mixed run - some turns reported usage, some did not - is measured from the turns that did.
    /// Discarding the whole aggregate because one turn was silent would throw away real data.
    /// </summary>
    [Fact]
    public void AggregateRunUsage_MixedReporting_MeasuresFromTheTurnsThatReported()
    {
        IReadOnlyList<AgentMessage> messages =
        [
            new AssistantAgentMessage("silent"),
            new AssistantAgentMessage("reported", Usage: new AgentUsage(InputTokens: 900, OutputTokens: 90))
        ];

        var result = InProcessAgentHandle.AggregateRunUsage(messages);

        result.ShouldNotBeNull();
        result!.InputTokens.ShouldBe(900);
        result.OutputTokens.ShouldBe(90);
    }
}
