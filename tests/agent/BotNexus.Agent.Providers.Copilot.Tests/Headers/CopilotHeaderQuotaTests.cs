using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Headers;

namespace BotNexus.Agent.Providers.Copilot.Tests.Headers;

public sealed class CopilotHeaderQuotaTests
{
    [Fact]
    public void Parse_DecimalAllowlist_SeparatesPercentAndCounts()
    {
        var value = CopilotHeaderQuotaParser.Parse("ent=300&rem=12.75&totRem=38.25&ov=1.5&ovPerm=true&rst=2026-07-01T00%3A00%3A00Z&secret=credential");
        value.Entitlement.ShouldBe(300m);
        value.RemainingPercent.ShouldBe(12.75m);
        value.TotalRemainingCount.ShouldBe(38.25m);
        value.OverageCount.ShouldBe(1.5m);
        value.OveragePermitted.ShouldBe(true);
        value.ResetAt.ShouldBe(DateTimeOffset.Parse("2026-07-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        JsonSerializer.Serialize(value).ShouldNotContain("credential");
        JsonSerializer.Serialize(value).ShouldNotContain("ApiRemaining");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("unlimited")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("%GG")]
    [InlineData("")]
    public void Parse_InvalidNumericOrUnlimited_IsUnknown(string raw)
    {
        var value = CopilotHeaderQuotaParser.Parse($"ent={raw}&rem={raw}&totRem={raw}&ov={raw}");
        value.Entitlement.ShouldBeNull();
        value.RemainingPercent.ShouldBeNull();
        value.TotalRemainingCount.ShouldBeNull();
        value.OverageCount.ShouldBeNull();
    }

    [Theory]
    [InlineData("rem=101")]
    [InlineData("rem=1&rem=2")]
    [InlineData("rem=%ZZ")]
    [InlineData("rem=12%")]
    public void Parse_InvalidOrDuplicatePercent_IsUnknown(string raw)
        => CopilotHeaderQuotaParser.Parse(raw).RemainingPercent.ShouldBeNull();

    [Fact]
    public void Parse_AbsentMalformedAndSizeBounds_AreUnknown()
    {
        foreach (var raw in new[] { "", "junk", new string('x', CopilotHeaderQuotaParser.MaxHeaderChars + 1), string.Join('&', Enumerable.Repeat("x=1", 65)) })
            CopilotHeaderQuotaParser.Parse(raw).ShouldBe(CopilotHeaderQuota.Unknown);
        var malformed = CopilotHeaderQuotaParser.Parse("rst=invalid&ovPerm=maybe&rem=42");
        malformed.ResetAt.ShouldBeNull();
        malformed.OveragePermitted.ShouldBeNull();
        malformed.RemainingPercent.ShouldBe(42m);
    }

    [Fact]
    public void Store_InstancesGenerationsAndDimensions_AreIndependent()
    {
        var store = new CopilotHeaderQuotaStore();
        var a = new CopilotHeaderScope("work-a", "generation-a");
        var b = new CopilotHeaderScope("work-b", "generation-b");
        var rotated = new CopilotHeaderScope("work-a", "generation-new");
        Observe(store, a, CopilotQuotaDimension.Chat, 1, 10);
        Observe(store, b, CopilotQuotaDimension.Chat, 2, 20);
        Observe(store, rotated, CopilotQuotaDimension.Chat, 3, 30);
        Observe(store, a, CopilotQuotaDimension.PremiumInteractions, 4, 40);
        store.GetLatest(a, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(10m);
        store.GetLatest(b, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(20m);
        store.GetLatest(rotated, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(30m);
        store.GetLatest(a, CopilotQuotaDimension.PremiumInteractions)?.Quota.RemainingPercent.ShouldBe(40m);
        store.GetLatest(new CopilotHeaderScope("unknown", "generation-a"), CopilotQuotaDimension.Chat).ShouldBeNull();
    }

    [Fact]
    public void Store_OutOfOrderCompletion_CannotOverwriteLaterRequest()
    {
        var store = new CopilotHeaderQuotaStore();
        var scope = new CopilotHeaderScope("work", "generation");
        Observe(store, scope, CopilotQuotaDimension.Chat, 2, 20);
        Observe(store, scope, CopilotQuotaDimension.Chat, 1, 80);
        Observe(store, scope, CopilotQuotaDimension.Chat, 2, 90);
        var latest = store.GetLatest(scope, CopilotQuotaDimension.Chat);
        latest.ShouldNotBeNull();
        latest.Quota.RemainingPercent.ShouldBe(20m);
        latest.Source.ShouldBe(CopilotQuotaSource.ResponseHeaders);
    }

    [Fact]
    public void Store_BoundedLatestOnly_DoesNotKeepLedger()
    {
        var store = new CopilotHeaderQuotaStore(2);
        Observe(store, new("a", "one"), CopilotQuotaDimension.Chat, 1, 1);
        Observe(store, new("b", "one"), CopilotQuotaDimension.Chat, 2, 2);
        Observe(store, new("c", "one"), CopilotQuotaDimension.Chat, 3, 3);
        store.Count.ShouldBe(2);
        store.GetLatest(new("a", "one"), CopilotQuotaDimension.Chat).ShouldBeNull();
        Observe(store, new("c", "one"), CopilotQuotaDimension.Chat, 4, 4);
        store.Count.ShouldBe(2);
    }

    [Fact]
    public void Scope_RejectsUnboundedOrBlankIdentifiers_AndIsImmutable()
    {
        Should.Throw<ArgumentException>(() => new CopilotHeaderScope("", "generation"));
        Should.Throw<ArgumentException>(() => new CopilotHeaderScope("work", new string('x', 129)));
        new CopilotHeaderScope("COPILOT", "generation").Instance.ShouldBe("github-copilot");
        typeof(CopilotHeaderScope).GetProperties().ShouldAllBe(p => p.SetMethod == null);
    }

    private static void Observe(CopilotHeaderQuotaStore store, CopilotHeaderScope scope, CopilotQuotaDimension dimension, long order, decimal remaining)
        => store.Observe(new(scope, dimension, order, DateTimeOffset.UnixEpoch, new(RemainingPercent: remaining)));
}
