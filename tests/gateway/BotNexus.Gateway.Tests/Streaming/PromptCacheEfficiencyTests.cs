using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Streaming;

namespace BotNexus.Gateway.Tests.Streaming;

/// <summary>
/// Pins the cache-efficiency accounting that makes every other prompt-cache change measurable.
///
/// The point of the ratio is to answer one question: is the prompt prefix staying stable across
/// turns? A number that silently resets on reload, or that counts a cache write as if it were a
/// read, would answer it wrongly and confidently -- worse than not measuring at all.
/// </summary>
public sealed class PromptCacheEfficiencyTests
{
    [Fact]
    public void TurnHitRatio_IsCacheReadOverTheWholePrompt()
    {
        var usage = new AgentResponseUsage(InputTokens: 100, OutputTokens: 50, CacheRead: 800, CacheWrite: 100);

        // Output tokens are deliberately excluded: this measures the prompt, and output is never
        // cacheable on any of these providers.
        PromptCacheEfficiency.TurnHitRatio(usage)!.Value.ShouldBe(0.8, 0.0001);
    }

    [Fact]
    public void TurnHitRatio_FirstTurnReadsZero_WhichIsAMeasurementNotAnAbsence()
    {
        var usage = new AgentResponseUsage(InputTokens: 0, OutputTokens: 20, CacheRead: 0, CacheWrite: 5000);

        PromptCacheEfficiency.TurnHitRatio(usage).ShouldBe(0.0);
    }

    [Fact]
    public void TurnHitRatio_IsNullWhenTheProviderReportedNoPrompt()
    {
        PromptCacheEfficiency.TurnHitRatio(null).ShouldBeNull();
        PromptCacheEfficiency.TurnHitRatio(new AgentResponseUsage(OutputTokens: 20)).ShouldBeNull();
    }

    [Fact]
    public void Accumulate_SumsAcrossTurns()
    {
        var session = NewSession();

        PromptCacheEfficiency.Accumulate(session, new AgentResponseUsage(InputTokens: 1000, CacheRead: 0, CacheWrite: 1000)).ShouldBeTrue();
        PromptCacheEfficiency.Accumulate(session, new AgentResponseUsage(InputTokens: 100, CacheRead: 1900, CacheWrite: 100)).ShouldBeTrue();

        var (input, cacheRead, cacheWrite) = PromptCacheEfficiency.ReadTotals(session);
        input.ShouldBe(1100);
        cacheRead.ShouldBe(1900);
        cacheWrite.ShouldBe(1100);

        PromptCacheEfficiency.SessionHitRatio(session)!.Value.ShouldBe(1900d / 4100d, 0.0001);
    }

    [Fact]
    public void Accumulate_IgnoresATurnThatReportedNoPrompt()
    {
        var session = NewSession();

        PromptCacheEfficiency.Accumulate(session, null).ShouldBeFalse();
        PromptCacheEfficiency.Accumulate(session, new AgentResponseUsage(OutputTokens: 40)).ShouldBeFalse();

        session.Metadata.ShouldNotContainKey(PromptCacheEfficiency.CumulativeInputMetadataKey);
        PromptCacheEfficiency.SessionHitRatio(session).ShouldBeNull();
    }

    [Fact]
    public void ReadTotals_SurviveARoundTripThroughPersistence()
    {
        // A metadata bag holds boxed longs in memory and JsonElements once it has come back from
        // the store. A counter that read as zero after a reload would restart the accounting on
        // every gateway restart and make long sessions look permanently cold.
        var session = NewSession();
        session.Metadata[PromptCacheEfficiency.CumulativeInputMetadataKey] = JsonDocument.Parse("500").RootElement;
        session.Metadata[PromptCacheEfficiency.CumulativeCacheReadMetadataKey] = "1500";
        session.Metadata[PromptCacheEfficiency.CumulativeCacheWriteMetadataKey] = 0;

        var (input, cacheRead, cacheWrite) = PromptCacheEfficiency.ReadTotals(session);
        input.ShouldBe(500);
        cacheRead.ShouldBe(1500);
        cacheWrite.ShouldBe(0);

        PromptCacheEfficiency.Accumulate(session, new AgentResponseUsage(InputTokens: 500));
        PromptCacheEfficiency.ReadTotals(session).Input.ShouldBe(1000);
    }

    [Fact]
    public void ReadTotals_TreatUnreadableEntriesAsZeroRatherThanThrowing()
    {
        var session = NewSession();
        session.Metadata[PromptCacheEfficiency.CumulativeInputMetadataKey] = "not a number";

        PromptCacheEfficiency.ReadTotals(session).Input.ShouldBe(0);
    }

    private static GatewaySession NewSession() => new() { Metadata = [] };
}
