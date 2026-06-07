using System.Diagnostics;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Telemetry;

namespace BotNexus.Agent.Providers.Copilot.Tests.Telemetry;

/// <summary>
/// Pins <see cref="CopilotUsageActivity"/>: emits the right Activity tags
/// for each token_type and total_nano_aiu, no-ops cleanly on null inputs,
/// and silently ignores chunks that do not carry a <c>copilot_usage</c> field.
/// </summary>
public class CopilotUsageActivityTests
{
    private static (ActivityListener Listener, ActivitySource Source) NewListener(string name)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, new ActivitySource(name));
    }

    [Fact]
    public void Emit_PopulatesPerTokenTypeAndTotalTags()
    {
        var (listener, source) = NewListener("BotNexus.Test.UsageEmit");
        using var _l = listener;
        using var _s = source;
        using var activity = source.StartActivity("test", ActivityKind.Client)!;
        activity.ShouldNotBeNull();

        var usage = new CopilotUsage(
            TotalNanoAiu: 6_390_000,
            TokenDetails:
            [
                new CopilotTokenDetail("input",  157, 1_000_000, 30_000_000_000),
                new CopilotTokenDetail("output",  14, 1_000_000, 120_000_000_000),
                new CopilotTokenDetail("cache_read", 0, 1_000_000, 15_000_000_000),
            ]);

        CopilotUsageActivity.Emit(usage, activity);

        var tags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value!);
        tags["botnexus.copilot.usage.total_nano_aiu"].ShouldBe(6_390_000L);

        tags["botnexus.copilot.usage.tokens.input"].ShouldBe(157L);
        tags["botnexus.copilot.usage.tokens.output"].ShouldBe(14L);
        tags["botnexus.copilot.usage.tokens.cache_read"].ShouldBe(0L);

        tags["botnexus.copilot.usage.cost_per_batch.input"].ShouldBe(30_000_000_000L);
        tags["botnexus.copilot.usage.cost_per_batch.output"].ShouldBe(120_000_000_000L);

        tags["botnexus.copilot.usage.batch_size.input"].ShouldBe(1_000_000L);
    }

    [Fact]
    public void Emit_NullUsage_DoesNotThrow()
    {
        var (listener, source) = NewListener("BotNexus.Test.UsageEmit.Null");
        using var _l = listener;
        using var _s = source;
        using var activity = source.StartActivity("t", ActivityKind.Client)!;

        Should.NotThrow(() => CopilotUsageActivity.Emit(null, activity));
        activity.TagObjects.ShouldBeEmpty();
    }

    [Fact]
    public void Emit_NullActivity_DoesNotThrow()
    {
        var usage = new CopilotUsage(1, []);
        Should.NotThrow(() => CopilotUsageActivity.Emit(usage, null));
    }

    [Fact]
    public void TryParseAndEmit_ChunkWithoutCopilotUsage_NoTags()
    {
        var (listener, source) = NewListener("BotNexus.Test.UsageEmit.Skip");
        using var _l = listener;
        using var _s = source;
        using var activity = source.StartActivity("t", ActivityKind.Client)!;

        using var doc = JsonDocument.Parse("""{ "id": "x", "choices": [] }""");
        CopilotUsageActivity.TryParseAndEmit(doc.RootElement, activity);

        activity.TagObjects.ShouldBeEmpty();
    }

    [Fact]
    public void TryParseAndEmit_ChunkWithCopilotUsage_EmitsTags()
    {
        var (listener, source) = NewListener("BotNexus.Test.UsageEmit.Parse");
        using var _l = listener;
        using var _s = source;
        using var activity = source.StartActivity("t", ActivityKind.Client)!;

        using var doc = JsonDocument.Parse("""
            {
              "copilot_usage": {
                "token_details": [{ "batch_size": 1000000, "cost_per_batch": 1, "token_count": 5, "token_type": "input" }],
                "total_nano_aiu": 5
              }
            }
            """);
        CopilotUsageActivity.TryParseAndEmit(doc.RootElement, activity);

        var tags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value!);
        tags["botnexus.copilot.usage.total_nano_aiu"].ShouldBe(5L);
        tags["botnexus.copilot.usage.tokens.input"].ShouldBe(5L);
    }
}
