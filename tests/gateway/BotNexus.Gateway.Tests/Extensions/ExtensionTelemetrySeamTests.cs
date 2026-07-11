using System.Diagnostics.Metrics;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Telemetry;
using BotNexus.Persistence.Sqlite.Telemetry;
using Shouldly;

namespace BotNexus.Gateway.Tests.Extensions;

/// <summary>
/// Tests for the extension telemetry seam (#1852): extensions get the same telemetry seam the
/// platform core uses (metrics via <see cref="IMetrics"/>, durable usage via
/// <see cref="IUsageTelemetry"/>) through injected abstractions, but every instrument is
/// auto-prefixed to <c>botnexus.ext.&lt;id&gt;.*</c> and every durable row is namespaced to the
/// extension id. The guardrails prevent an extension from emitting under a platform namespace such
/// as <c>botnexus.turns.*</c>.
/// </summary>
public sealed class ExtensionTelemetrySeamTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ExtensionTelemetrySeamTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "botnexus-ext-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "usage.db");
    }

    // --- Namespace-prefix enforcement -------------------------------------------------------

    [Fact]
    public void InstrumentName_AutoPrefixes_LeafIntoExtensionNamespace()
    {
        ExtensionMeters.InstrumentName("skills", "loads").ShouldBe("botnexus.ext.skills.loads");
    }

    [Fact]
    public void InstrumentName_RejectsLeaf_ReachingIntoPlatformNamespace()
    {
        // Acceptance criterion: an extension cannot emit under botnexus.turns.*.
        var ex = Should.Throw<ArgumentException>(
            () => ExtensionMeters.InstrumentName("skills", "botnexus.turns.total"));
        ex.Message.ShouldContain("botnexus.turns");
    }

    [Fact]
    public void ExtensionMetrics_CannotEmitUnderPlatformNamespace()
    {
        using var meter = new Meter("BotNexus.Test.ExtGuard");
        var platform = new BotNexusMetrics(meter);
        IExtensionMetrics ext = new ExtensionMetrics("skills", platform);

        Should.Throw<ArgumentException>(() => ext.CreateCounter<long>("botnexus.turns.total"));
    }

    [Theory]
    [InlineData("Skills")]     // uppercase
    [InlineData("my.ext")]     // dot escapes the namespace
    [InlineData("has space")]
    public void ValidateExtensionId_RejectsUnsafeIds(string id)
    {
        Should.Throw<ArgumentException>(() => ExtensionMeters.ValidateExtensionId(id));
    }

    // --- Extension emits a counter through injected abstractions ---------------------------

    [Fact]
    public void Extension_EmitsCounter_UnderItsOwnNamespace_ObservableViaMeterListener()
    {
        using var meter = new Meter("BotNexus.Test.ExtCounter");
        IMetrics platform = new BotNexusMetrics(meter);
        IExtensionTelemetryFactory factory = new ExtensionTelemetryFactory(platform, new NoopUsage());
        var ext = factory.MetricsFor("skills");

        long observed = 0;
        string? observedName = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter)
            {
                observedName = instrument.Name;
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => observed += measurement);
        listener.Start();

        var counter = ext.CreateCounter<long>("loads");
        counter.Add(2);
        counter.Add(3);

        observed.ShouldBe(5);
        observedName.ShouldBe("botnexus.ext.skills.loads");
    }

    // --- Extension records a durable usage counter through injected abstractions ------------

    [Fact]
    public async Task Extension_RecordsDurableUsage_IsolatedToItsNamespace()
    {
        await using var store = new SqliteUsageTelemetryStore(_dbPath);
        IExtensionTelemetryFactory factory = new ExtensionTelemetryFactory(new BotNexusMetrics(), store);
        var usage = factory.UsageFor("skills");

        await usage.IncrementAsync("email-triage", "use");
        await usage.IncrementAsync("email-triage", "use");
        await usage.RecordCreatedAsync("email-triage", "agent-farnsworth[bot]");

        var record = await usage.GetAsync("email-triage");
        record.ShouldNotBeNull();
        record!.GetCounter("use").ShouldBe(2);
        record.CreatedBy.ShouldBe("agent-farnsworth[bot]");

        // Isolation: a different extension id sees nothing, and the shared store confirms the row
        // landed under namespace "skills", never a platform namespace.
        var other = factory.UsageFor("web-tools");
        (await other.GetAsync("email-triage")).ShouldBeNull();

        var raw = await store.GetAsync("skills", "email-triage");
        raw.ShouldNotBeNull();
        raw!.Namespace.ShouldBe("skills");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    // Minimal IUsageTelemetry that records nothing; used where only the metrics path is exercised.
    private sealed class NoopUsage : IUsageTelemetry
    {
        public Task IncrementAsync(string @namespace, string key, string counterName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordCreatedAsync(string @namespace, string key, string createdBy, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPinnedAsync(string @namespace, string key, bool pinned, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UsageRecord>> GetAllAsync(string @namespace, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UsageRecord>>(Array.Empty<UsageRecord>());
        public Task<UsageRecord?> GetAsync(string @namespace, string key, CancellationToken cancellationToken = default)
            => Task.FromResult<UsageRecord?>(null);
    }
}
