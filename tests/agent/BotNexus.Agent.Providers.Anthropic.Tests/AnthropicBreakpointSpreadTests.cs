using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Pins where the message breakpoints go, and why.
///
/// <para>
/// A cache read requires a breakpoint that either sits exactly where an earlier request wrote an
/// entry, or within <see cref="AnthropicMessageConverter.LookbackBlocks"/> blocks after one.
/// Stamping the last few messages satisfies that for ordinary turn-taking and fails for a wide
/// agent turn: once a single turn appends more blocks than the lookback, breakpoints bunched
/// inside that turn's new content are all stranded together and the whole conversation re-bills.
/// </para>
///
/// <para>
/// These tests model two consecutive requests and assert the chain survives, rather than
/// asserting a shape -- a shape assertion would have passed for the placement that had the bug.
/// </para>
/// </summary>
public class AnthropicBreakpointSpreadTests
{
    private const int MaxMessageBreakpoints = 3;

    [Fact]
    public void WideTurn_StillLeavesABreakpointWithinReachOfAnEarlierEntry()
    {
        var before = Messages(40);
        var after = Messages(65); // one turn appended 25 blocks -- more than the lookback

        var previousEntries = BlockPositionsOf(before, Select(before));
        var currentBreakpoints = BlockPositionsOf(after, Select(after));

        ClosestReachBack(currentBreakpoints, previousEntries)
            .ShouldBeLessThanOrEqualTo(
                AnthropicMessageConverter.LookbackBlocks,
                "an anchor sits at a fixed distance from the start of the conversation, so it is " +
                "chosen again on the next request and its entry is still there to be read");
    }

    [Fact]
    public void WideTurn_WouldHaveStrandedTheOldTailOnlyPlacement()
    {
        // The regression this placement exists to prevent. Kept as an executable statement of the
        // bug so the test above cannot quietly stop meaning anything.
        var before = Messages(40);
        var after = Messages(65);

        var previousEntries = BlockPositionsOf(before, LastThreeIndices(before));
        var currentBreakpoints = BlockPositionsOf(after, LastThreeIndices(after));

        ClosestReachBack(currentBreakpoints, previousEntries)
            .ShouldBeGreaterThan(AnthropicMessageConverter.LookbackBlocks);
    }

    [Fact]
    public void AnchorsDoNotMoveWhileTheConversationGrowsWithinOneStride()
    {
        // The property the whole scheme rests on: an anchor chosen now must be chosen again next
        // turn, or it never matches an entry and buys nothing.
        var anchorsAt40 = Select(Messages(40)).Skip(1).ToArray();
        var anchorsAt45 = Select(Messages(45)).Skip(1).ToArray();

        anchorsAt45.ShouldBe(anchorsAt40);
    }

    [Fact]
    public void ANewAnchorLandsWithinLookbackOfTheOneItFollows()
    {
        // Anchors advance a stride at a time. The stride is under the lookback precisely so the
        // first request that reaches a new anchor can still chain back to the previous one.
        var messages = Messages(200);
        var anchors = Select(messages).Skip(1).Select(index => BlockEnd(messages, index)).ToArray();

        anchors.Length.ShouldBeGreaterThan(1);
        for (var i = 0; i < anchors.Length - 1; i++)
        {
            (anchors[i] - anchors[i + 1])
                .ShouldBeLessThanOrEqualTo(AnthropicMessageConverter.LookbackBlocks);
        }
    }

    [Fact]
    public void TailAlwaysCarriesABreakpoint()
    {
        // It writes the entry the next request reads. Losing it would break the ordinary case in
        // order to protect the rare one.
        foreach (var count in new[] { 1, 5, 40, 200 })
        {
            var messages = Messages(count);
            Select(messages)[0].ShouldBe(count - 1);
        }
    }

    [Fact]
    public void ShortConversations_KeepTheOriginalTailPlacement()
    {
        // Under one stride there is no anchor position to use and no turn wide enough to strand
        // anything, so this placement leaves short conversations exactly as they were.
        var messages = Messages(7);

        Select(messages).ShouldBe(LastThreeIndices(messages));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NeverPlacesMoreBreakpointsThanTheBudgetAllows(int budget)
    {
        var messages = Messages(200);

        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages, CacheRetention.Short, "https://api.anthropic.com", budget);

        CountStamped(messages).ShouldBe(budget);
    }

    // ---------- helpers ----------

    private static IReadOnlyList<int> Select(List<Dictionary<string, object?>> messages)
        => AnthropicMessageConverter.SelectBreakpointIndices(messages, MaxMessageBreakpoints);

    private static int[] LastThreeIndices(List<Dictionary<string, object?>> messages)
        => [messages.Count - 1, messages.Count - 2, messages.Count - 3];

    /// <summary>
    /// Smallest backward distance, in blocks, from any current breakpoint to an entry an earlier
    /// request wrote at or before it. A value inside the lookback means at least one breakpoint
    /// reads cache; a larger one means the request re-processes the conversation from scratch.
    /// </summary>
    private static int ClosestReachBack(IReadOnlyList<int> currentBlocks, IReadOnlyList<int> previousBlocks)
    {
        var best = int.MaxValue;

        foreach (var current in currentBlocks)
        {
            foreach (var previous in previousBlocks)
            {
                var distance = current - previous;
                if (distance >= 0 && distance < best)
                    best = distance;
            }
        }

        return best;
    }

    private static int[] BlockPositionsOf(List<Dictionary<string, object?>> messages, IEnumerable<int> indices)
        => indices.Select(index => BlockEnd(messages, index)).ToArray();

    /// <summary>One block per message here, so a message's block position is its index plus one.</summary>
    private static int BlockEnd(List<Dictionary<string, object?>> messages, int index) => index + 1;

    private static List<Dictionary<string, object?>> Messages(int count)
        => Enumerable.Range(0, count).Select(i => new Dictionary<string, object?>
        {
            ["role"] = i % 2 == 0 ? "user" : "assistant",
            ["content"] = new List<object>
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = $"message {i}" }
            }
        }).ToList();

    private static int CountStamped(List<Dictionary<string, object?>> messages)
        => messages.Count(message =>
            message["content"] is List<object> { Count: > 0 } blocks &&
            blocks[^1] is Dictionary<string, object?> last &&
            last.ContainsKey("cache_control"));
}
