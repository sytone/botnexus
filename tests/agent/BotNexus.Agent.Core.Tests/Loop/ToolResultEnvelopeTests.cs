using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Loop;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>Contract tests for the single inline-or-stored tool-result envelope (#4309).</summary>
public sealed class ToolResultEnvelopeTests
{
    [Fact]
    public void Inline_SmallScalar_RetainsCurrentValueWithoutReceipt()
    {
        var envelope = ToolResultEnvelope.InlineScalar(JsonSerializer.SerializeToElement(42));

        envelope.Disposition.ShouldBe(ToolResultDisposition.Inline);
        envelope.Kind.ShouldBe(ToolResultKind.Scalar);
        envelope.InlineValue?.GetInt32().ShouldBe(42);
        envelope.Receipt.ShouldBeNull();
        envelope.Preview.ShouldBeNull();
    }

    [Fact]
    public void Stored_LargeObject_CarriesReceiptAndBoundedPreviewWithoutPayload()
    {
        var store = new ToolResultStore(new ToolResultStoreOptions(MaxTotalBytes: 8 * 1024 * 1024));
        var scope = new ToolResultScope("world-a", "agent-a", "conversation-a", "session-a", "policy-a");
        var secretTail = "never-project-this-tail";
        var payload = Encoding.UTF8.GetBytes($"{{\"items\":[{string.Join(',', Enumerable.Repeat("{\"name\":\"row\"}", 250_000))}],\"tail\":\"{secretTail}\"}}");
        var receipt = store.Store(payload, new ToolResultDescriptor(
            ToolResultKind.Object,
            "application/json",
            "urn:example:v1",
            ToolResultProvenance.ForeignUntrusted,
            "example_search",
            "call-42",
            250_000,
            ToolResultCompleteness.Complete,
            ToolResultRetention.Volatile), scope);

        var envelope = ToolResultEnvelope.Stored(receipt, payload, maxPreviewBytes: 512);
        var projection = envelope.ToModelProjection();

        envelope.Disposition.ShouldBe(ToolResultDisposition.Stored);
        envelope.InlineValue.ShouldBeNull();
        envelope.Receipt.ShouldBe(receipt);
        Encoding.UTF8.GetByteCount(envelope.Preview ?? string.Empty).ShouldBeLessThanOrEqualTo(512);
        projection.ShouldContain(receipt.ResultId.Value);
        projection.ShouldContain("foreignuntrusted");
        projection.ShouldNotContain(secretTail);
        Encoding.UTF8.GetByteCount(projection).ShouldBeLessThan(2048);
    }

    [Theory]
    [InlineData(ToolResultKind.Text, "text/plain; charset=utf-8")]
    [InlineData(ToolResultKind.Object, "application/json")]
    [InlineData(ToolResultKind.Table, "application/json")]
    [InlineData(ToolResultKind.Blob, "application/octet-stream")]
    public void Stored_EachKind_UsesABoundedPreview(ToolResultKind kind, string mediaType)
    {
        var payload = kind switch
        {
            ToolResultKind.Text => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("你好🔬", 200))),
            ToolResultKind.Object => Encoding.UTF8.GetBytes("{\"alpha\":1,\"beta\":2,\"long\":\"" + new string('x', 2000) + "\"}"),
            ToolResultKind.Table => Encoding.UTF8.GetBytes("[{\"id\":1},{\"id\":2},{\"id\":3},{\"id\":4}]"),
            _ => Enumerable.Range(0, 2048).Select(value => (byte)(value % 256)).ToArray()
        };
        var receipt = Receipt(kind, mediaType, payload.LongLength);

        var envelope = ToolResultEnvelope.Stored(receipt, payload, maxPreviewBytes: 96);

        var preview = envelope.Preview.ShouldNotBeNull();
        Encoding.UTF8.GetByteCount(preview).ShouldBeLessThanOrEqualTo(96);
        preview.ShouldNotContain('\uFFFD');
        if (kind == ToolResultKind.Blob)
        {
            preview.ShouldContain("binary");
            preview.ShouldNotContain(Convert.ToBase64String(payload));
        }
    }

    [Fact]
    public void Constructor_RejectsAmbiguousOrEmptyEnvelope()
    {
        var scalar = JsonSerializer.SerializeToElement("value");
        var receipt = Receipt(ToolResultKind.Text, "text/plain", 5);

        Should.Throw<ArgumentException>(() => new ToolResultEnvelope(
            ToolResultDisposition.Inline, ToolResultKind.Scalar, scalar, receipt, null));
        Should.Throw<ArgumentException>(() => new ToolResultEnvelope(
            ToolResultDisposition.Stored, ToolResultKind.Text, null, null, "preview"));
    }

    private static ToolResultReceipt Receipt(ToolResultKind kind, string mediaType, long size) => new(
        ToolResultId.Parse("tr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        1,
        kind,
        mediaType,
        null,
        "example_tool",
        "call-1",
        null,
        size,
        ToolResultCompleteness.Complete,
        DateTimeOffset.Parse("2026-09-26T00:00:00Z"),
        DateTimeOffset.Parse("2026-09-26T01:00:00Z"),
        ToolResultRetention.Volatile,
        ToolResultProvenance.ForeignUntrusted,
        new string('a', 64),
        ToolResultTerminalStatus.Complete,
        [ToolResultOperation.Read, ToolResultOperation.Project]);
}
