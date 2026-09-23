using System.Text;
using BotNexus.Agent.Core.Loop;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>Contract tests for the typed, scoped tool-result store foundation (#4309).</summary>
public sealed class ToolResultStoreTests
{
    [Fact]
    public void Store_LargeStructuredResult_ReturnsCompactCompleteReceipt()
    {
        var store = new ToolResultStore(new ToolResultStoreOptions(MaxTotalBytes: 8 * 1024 * 1024));
        var scope = Scope("conversation-a", "session-a");
        var payload = Encoding.UTF8.GetBytes(new string('x', 5 * 1024 * 1024));
        var descriptor = new ToolResultDescriptor(
            ToolResultKind.Object,
            "application/json",
            "urn:botnexus:test-schema:v1",
            ToolResultProvenance.ForeignUntrusted,
            "example_search",
            "call-42",
            Count: 50,
            ToolResultCompleteness.Complete,
            ToolResultRetention.Volatile);

        var receipt = store.Store(payload, descriptor, scope);

        receipt.ResultId.Value.ShouldStartWith("tr_");
        receipt.ResultId.Value.Length.ShouldBeGreaterThan(40);
        receipt.Revision.ShouldBe(1);
        receipt.Kind.ShouldBe(ToolResultKind.Object);
        receipt.MediaType.ShouldBe("application/json");
        receipt.Schema.ShouldBe("urn:botnexus:test-schema:v1");
        receipt.SourceTool.ShouldBe("example_search");
        receipt.SourceCallId.ShouldBe("call-42");
        receipt.Count.ShouldBe(50);
        receipt.SizeBytes.ShouldBe(payload.LongLength);
        receipt.Completeness.ShouldBe(ToolResultCompleteness.Complete);
        receipt.Provenance.ShouldBe(ToolResultProvenance.ForeignUntrusted);
        receipt.SupportedOperations.ShouldBe([ToolResultOperation.Read, ToolResultOperation.Project]);
        receipt.IntegritySha256.ShouldNotBeNullOrWhiteSpace();
        receipt.ToModelProjection().Length.ShouldBeLessThan(1024);

        var read = store.Read(receipt.ResultId, receipt.Revision, scope);
        read.Status.ShouldBe(ToolResultReadStatus.Ok);
        read.Payload.ToArray().ShouldBe(payload);
        read.Receipt?.Provenance.ShouldBe(ToolResultProvenance.ForeignUntrusted);
    }

    [Fact]
    public void Read_ForeignScope_IsDeniedAndDoesNotReturnMetadata()
    {
        var store = new ToolResultStore();
        var owner = Scope("conversation-a", "session-a");
        var receipt = store.Store(
            Encoding.UTF8.GetBytes("sensitive"),
            Descriptor(),
            owner);

        var denied = store.Read(receipt.ResultId, receipt.Revision, Scope("conversation-b", "session-b"));

        denied.Status.ShouldBe(ToolResultReadStatus.AccessDenied);
        denied.Payload.IsEmpty.ShouldBeTrue();
        denied.Receipt.ShouldBeNull();
    }

    [Fact]
    public void Read_StaleRevision_IsDistinctFromUnknownAndExpired()
    {
        var now = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        var store = new ToolResultStore(
            new ToolResultStoreOptions(DefaultRetention: TimeSpan.FromMinutes(5)),
            () => now);
        var scope = Scope("conversation-a", "session-a");
        var receipt = store.Store(Encoding.UTF8.GetBytes("value"), Descriptor(), scope);

        store.Read(receipt.ResultId, 99, scope).Status.ShouldBe(ToolResultReadStatus.StaleRevision);
        store.Read(ToolResultId.Parse("tr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 1, scope).Status
            .ShouldBe(ToolResultReadStatus.Unknown);

        now = now.AddMinutes(6);
        store.Read(receipt.ResultId, receipt.Revision, scope).Status.ShouldBe(ToolResultReadStatus.Expired);
    }

    [Fact]
    public void Store_ExceedsCapacity_EvictsOldestVolatileResult()
    {
        var store = new ToolResultStore(new ToolResultStoreOptions(MaxEntries: 1, MaxTotalBytes: 1024));
        var scope = Scope("conversation-a", "session-a");
        var first = store.Store(Encoding.UTF8.GetBytes("first"), Descriptor(), scope);
        var second = store.Store(Encoding.UTF8.GetBytes("second"), Descriptor(), scope);

        store.Read(first.ResultId, first.Revision, scope).Status.ShouldBe(ToolResultReadStatus.Evicted);
        store.Read(second.ResultId, second.Revision, scope).Status.ShouldBe(ToolResultReadStatus.Ok);
    }

    [Fact]
    public void DurableStore_RestartPreservesScopedResultOutsideMetadata()
    {
        using var directory = new TemporaryDirectory();
        var scope = Scope("conversation-a", "session-a");
        var descriptor = Descriptor() with { Retention = ToolResultRetention.Durable };
        var secret = "complete-redacted-payload-not-metadata";
        var first = new DurableToolResultStore(directory.Path);

        var receipt = first.Store(Encoding.UTF8.GetBytes(secret), descriptor, scope);
        var restarted = new DurableToolResultStore(directory.Path);
        var read = restarted.Read(receipt.ResultId, receipt.Revision, scope);

        read.Status.ShouldBe(ToolResultReadStatus.Ok);
        Encoding.UTF8.GetString(read.Payload.Span).ShouldBe(secret);
        read.Receipt.ShouldNotBeNull();
        read.Receipt.ResultId.ShouldBe(receipt.ResultId);
        read.Receipt.Revision.ShouldBe(receipt.Revision);
        read.Receipt.IntegritySha256.ShouldBe(receipt.IntegritySha256);
        read.Receipt.Provenance.ShouldBe(receipt.Provenance);
        Directory.GetFiles(directory.Path, "*.payload").ShouldHaveSingleItem();
        var metadata = File.ReadAllText(Directory.GetFiles(directory.Path, "*.json").ShouldHaveSingleItem());
        metadata.ShouldNotContain(secret);
    }

    [Fact]
    public void DurableStore_RestartRetainsScopeIntegrityAndExpiryOutcomes()
    {
        using var directory = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        var options = new ToolResultStoreOptions(DefaultRetention: TimeSpan.FromMinutes(5));
        var scope = Scope("conversation-a", "session-a");
        var first = new DurableToolResultStore(directory.Path, options, () => now);
        var receipt = first.Store(
            Encoding.UTF8.GetBytes("value"),
            Descriptor() with { Retention = ToolResultRetention.Durable },
            scope);

        var restarted = new DurableToolResultStore(directory.Path, options, () => now);
        restarted.Read(receipt.ResultId, receipt.Revision, Scope("conversation-b", "session-b")).Status
            .ShouldBe(ToolResultReadStatus.AccessDenied);
        restarted.Read(receipt.ResultId, receipt.Revision + 1, scope).Status
            .ShouldBe(ToolResultReadStatus.StaleRevision);

        File.WriteAllText(Path.Combine(directory.Path, $"{receipt.ResultId.Value}.payload"), "corrupt");
        restarted.Read(receipt.ResultId, receipt.Revision, scope).Status
            .ShouldBe(ToolResultReadStatus.Corrupt);

        var expiring = first.Store(
            Encoding.UTF8.GetBytes("expires"),
            Descriptor() with { Retention = ToolResultRetention.Durable },
            scope);
        now = now.AddMinutes(6);
        var afterExpiry = new DurableToolResultStore(directory.Path, options, () => now);
        afterExpiry.Read(expiring.ResultId, expiring.Revision, scope).Status
            .ShouldBe(ToolResultReadStatus.Expired);
    }

    [Fact]
    public void ContinuationStore_SharedInitializesBeforeAnyExplicitInstance()
    {
        var handle = ToolOutputContinuationStore.Shared.Store("shared", "example_tool");

        var slice = ToolOutputContinuationStore.Shared.Read(handle, 0, 16);

        slice.Status.ShouldBe(ToolOutputContinuationStatus.Ok);
        slice.Text.ShouldBe("shared");
    }

    [Fact]
    public void ContinuationStore_UsesSharedTypedStoreForTextResults()
    {
        var scope = Scope("conversation-a", "session-a");
        var resultStore = new ToolResultStore();
        var continuation = new ToolOutputContinuationStore(resultStore, scope);
        var handle = continuation.Store("abcdef", "example_tool");

        handle.ShouldStartWith("toc_");

        var slice = continuation.Read(handle, 2, 3);
        slice.Status.ShouldBe(ToolOutputContinuationStatus.Ok);
        slice.Text.ShouldBe("cde");
    }

    [Fact]
    public void ContinuationStore_EvictsLegacyAliasWithItsConfiguredEntryBound()
    {
        var continuation = new ToolOutputContinuationStore(maxEntries: 1);
        var first = continuation.Store("first");
        var second = continuation.Store("second");

        continuation.Read(first, 0, 16).Status.ShouldBe(ToolOutputContinuationStatus.UnknownHandle);
        continuation.Read(second, 0, 16).Status.ShouldBe(ToolOutputContinuationStatus.Ok);
    }

    private static ToolResultScope Scope(string conversation, string session) =>
        new("world-a", "agent-a", conversation, session, "policy-a");

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"botnexus-result-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static ToolResultDescriptor Descriptor() => new(
        ToolResultKind.Text,
        "text/plain; charset=utf-8",
        Schema: null,
        ToolResultProvenance.LocalTrusted,
        "example_tool",
        "call-1",
        Count: null,
        ToolResultCompleteness.Complete,
        ToolResultRetention.Volatile);
}
