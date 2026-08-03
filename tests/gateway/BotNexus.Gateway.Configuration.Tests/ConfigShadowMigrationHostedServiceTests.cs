using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Shadow;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Tests for the shadow hosted service and the feature gate (#2766 AC1, AC2, AC5, AC6, AC8).
/// </summary>
public sealed class ConfigShadowMigrationHostedServiceTests
{
    private static JsonObject Obj(string raw) => JsonNode.Parse(raw)!.AsObject();

    private sealed class StubSource(JsonObject? doc) : IConfigShadowSource
    {
        public int ReadCount { get; private set; }

        public Task<JsonObject?> ReadRawDocumentAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(doc);
        }
    }

    private sealed class StubGate(bool enabled) : IConfigShadowGate
    {
        public Task<bool> IsShadowEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(enabled);
    }

    private sealed class StubRoundTrip(Func<JsonObject, JsonObject?> transform) : IConfigStoreRoundTrip
    {
        public int CallCount { get; private set; }

        public Task<JsonObject?> MigrateAndReadBackAsync(JsonObject source, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(transform(source));
        }
    }

    private sealed class ThrowingRoundTrip : IConfigStoreRoundTrip
    {
        public Task<JsonObject?> MigrateAndReadBackAsync(JsonObject source, CancellationToken cancellationToken)
            => throw new InvalidOperationException("store exploded");
    }

    private static ConfigShadowMigrationHostedService Build(
        IConfigShadowSource source,
        IConfigStoreRoundTrip roundTrip,
        IConfigShadowReportSink sink,
        bool shadowEnabled = true)
        => new(
            source,
            roundTrip,
            sink,
            new StubGate(shadowEnabled),
            NullLogger<ConfigShadowMigrationHostedService>.Instance);

    /// <summary>
    /// AC5, and the clause that makes this a safety mechanism rather than a new outage vector: a
    /// migration that throws must not fail startup.
    ///
    /// <para>
    /// #2731 records the gateway dying outright on a startup fault - <c>BackgroundServiceExceptionBehavior</c>
    /// is <c>StopHost</c>, so one background service's exception terminates cron, portal, SignalR and
    /// every agent surface. A diagnostic capable of taking the host down would be worse than no
    /// diagnostic at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ThrowingMigration_DoesNotFailStartup()
    {
        var sink = new ConfigShadowReportSink();
        var service = Build(new StubSource(Obj("""{ "a": 1 }""")), new ThrowingRoundTrip(), sink);

        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));

        sink.LastFailure.ShouldNotBeNull();
        sink.LastFailure.ShouldContain("store exploded");
        sink.Latest.ShouldBeNull("a failed run must not leave behind a report that reads as a clean comparison");
    }

    /// <summary>AC5: a non-empty diff is a finding, not a startup failure.</summary>
    [Fact]
    public async Task NonEmptyDiff_DoesNotFailStartup()
    {
        var sink = new ConfigShadowReportSink();
        var service = Build(
            new StubSource(Obj("""{ "a": 1, "b": 2 }""")),
            new StubRoundTrip(_ => Obj("""{ "a": 1 }""")),
            sink);

        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));

        sink.Latest.ShouldNotBeNull();
        sink.Latest!.IsClean.ShouldBeFalse();
        sink.Latest.Differences.ShouldContain(d => d.Path == "b");
    }

    /// <summary>
    /// AC6: with the flag off, the shadow path does not run at all - not even a read of the source
    /// document. "Both flags off means today's behaviour, zero new code paths active" has to be
    /// literally true, or the default state is not actually the old state.
    /// </summary>
    [Fact]
    public async Task ShadowDisabled_DoesNotEvenReadTheSourceDocument()
    {
        var source = new StubSource(Obj("""{ "a": 1 }"""));
        var roundTrip = new StubRoundTrip(o => o);
        var sink = new ConfigShadowReportSink();

        var service = Build(source, roundTrip, sink, shadowEnabled: false);
        await service.StartAsync(CancellationToken.None);

        source.ReadCount.ShouldBe(0);
        roundTrip.CallCount.ShouldBe(0);
        sink.Latest.ShouldBeNull();
        sink.LastFailure.ShouldBeNull();
    }

    /// <summary>AC8: the most recent result is retrievable without scraping startup logs.</summary>
    [Fact]
    public async Task LatestReport_IsRetrievableAfterTheRun()
    {
        var sink = new ConfigShadowReportSink();
        var service = Build(
            new StubSource(Obj("""{ "a": 1 }""")),
            new StubRoundTrip(o => o.DeepClone().AsObject()),
            sink);

        await service.StartAsync(CancellationToken.None);

        sink.Latest.ShouldNotBeNull();
        sink.Latest!.IsClean.ShouldBeTrue();
        sink.Latest.Summary.ShouldContain("1 source keys");
    }

    /// <summary>
    /// A missing document is recorded as a failure, not as a clean comparison.
    ///
    /// <para>
    /// Reporting "0 differences" when there was nothing to compare is the vacuous-instrument failure: a
    /// sweep whose input was empty renders identically to one that verified something.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MissingDocument_IsRecordedAsFailure_NotAsACleanDiff()
    {
        var sink = new ConfigShadowReportSink();
        var service = Build(new StubSource(null), new StubRoundTrip(o => o), sink);

        await service.StartAsync(CancellationToken.None);

        sink.Latest.ShouldBeNull();
        sink.LastFailure.ShouldNotBeNull();
    }

    /// <summary>
    /// AC1/AC2: authoritative-without-shadow is refused at startup.
    /// </summary>
    [Fact]
    public void AuthoritativeWithoutShadow_IsRejected()
    {
        var ex = Should.Throw<ConfigStoreFeatureStateException>(
            () => ConfigStoreFeatureGate.EnsureValid(shadowEnabled: false, authoritativeEnabled: true));

        ex.Message.ShouldContain(ConfigStoreFeatures.ShadowMigration);
        ex.Message.ShouldContain(ConfigStoreFeatures.Authoritative);
    }

    /// <summary>AC1: the three legitimate flag states are all accepted.</summary>
    [Theory]
    [InlineData(false, false)] // today's behaviour
    [InlineData(true, false)]  // shadow only
    [InlineData(true, true)]   // cutover, with shadow still guarding
    public void LegitimateFlagCombinations_AreAccepted(bool shadow, bool authoritative)
    {
        Should.NotThrow(() => ConfigStoreFeatureGate.EnsureValid(shadow, authoritative));
    }
}
