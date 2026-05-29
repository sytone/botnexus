using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Pins the metadata lift behaviour of <see cref="CrossWorldChannelAdapter"/>, especially the
/// <c>closeAfterResponse</c> bool lift introduced in P9-C. The lift must tolerate every shape the
/// underlying <see cref="OutboundMessage.Metadata"/> dictionary might carry — raw
/// <see cref="bool"/> (in-process call), <see cref="JsonElement"/> (round-tripped through
/// <see cref="System.Text.Json"/> when sourced from a persisted session), and string fallback.
/// Any unknown shape MUST fall back to <c>false</c> (= receiver reverts to pre-P9-C archive
/// behaviour) — a functional regression, never a wire-protocol corruption.
/// </summary>
public sealed class CrossWorldChannelAdapterTests
{
    [Fact]
    public Task ExchangeAsync_LiftsRawBoolTrue_AsCloseAfterResponseTrue()
        => AssertCloseAfterResponseLift(metadataValue: true, expectedWire: true);

    [Fact]
    public Task ExchangeAsync_LiftsRawBoolFalse_AsCloseAfterResponseFalse()
        => AssertCloseAfterResponseLift(metadataValue: false, expectedWire: false);

    [Fact]
    public Task ExchangeAsync_LiftsJsonElementTrue_AsCloseAfterResponseTrue()
        => AssertCloseAfterResponseLift(
            metadataValue: ParseJson("true"),
            expectedWire: true);

    [Fact]
    public Task ExchangeAsync_LiftsJsonElementFalse_AsCloseAfterResponseFalse()
        => AssertCloseAfterResponseLift(
            metadataValue: ParseJson("false"),
            expectedWire: false);

    [Fact]
    public Task ExchangeAsync_LiftsJsonElementStringTrue_AsCloseAfterResponseTrue()
        => AssertCloseAfterResponseLift(
            metadataValue: ParseJson("\"true\""),
            expectedWire: true);

    [Fact]
    public Task ExchangeAsync_LiftsStringTrue_AsCloseAfterResponseTrue()
        => AssertCloseAfterResponseLift(metadataValue: "true", expectedWire: true);

    [Fact]
    public Task ExchangeAsync_LiftsMissingMetadata_AsCloseAfterResponseFalse()
        => AssertCloseAfterResponseLift(metadataValue: null, expectedWire: false);

    [Fact]
    public Task ExchangeAsync_LiftsUnknownShape_AsCloseAfterResponseFalse()
        => AssertCloseAfterResponseLift(metadataValue: 42, expectedWire: false);

    [Fact]
    public Task ExchangeAsync_LiftsJsonElementGarbageString_AsCloseAfterResponseFalse()
        => AssertCloseAfterResponseLift(
            metadataValue: ParseJson("\"not-a-bool\""),
            expectedWire: false);

    // ---- helpers ----

    private static async Task AssertCloseAfterResponseLift(object? metadataValue, bool expectedWire)
    {
        CrossWorldRelayRequest? wire = null;
        var handler = new StubHttpMessageHandler(async (req, _) =>
        {
            wire = await req.Content!.ReadFromJsonAsync<CrossWorldRelayRequest>();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CrossWorldRelayResponse
                {
                    Response = "ok",
                    Status = "active",
                    SessionId = "remote-session-1"
                })
            };
        });

        var adapter = new CrossWorldChannelAdapter(
            NullLogger<CrossWorldChannelAdapter>.Instance,
            new HttpClient(handler));

        var metadata = new Dictionary<string, object?>
        {
            ["endpoint"] = "https://gateway-b.internal",
            ["sourceWorldId"] = "world-a",
            ["sourceAgentId"] = "init",
            ["targetAgentId"] = "tgt",
            ["conversationId"] = ConversationId.Create().Value,
            ["apiKey"] = "peer-key"
        };
        if (metadataValue is not null)
        {
            metadata["closeAfterResponse"] = metadataValue;
        }

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("cross-world"),
            ChannelAddress = ChannelAddress.From("gateway-b"),
            Content = "hello",
            Metadata = metadata
        };

        await adapter.ExchangeAsync(outbound);

        wire.ShouldNotBeNull();
        wire!.CloseAfterResponse.ShouldBe(expectedWire,
            customMessage: $"TryGetMetadataBool must lift `{metadataValue ?? "<null>"}` " +
                $"(shape: {metadataValue?.GetType().Name ?? "null"}) as CloseAfterResponse={expectedWire} " +
                "on the wire request. P9-C contract: missing/unknown shapes silently fall back to " +
                "false so the receiver reverts to pre-P9-C archive behaviour; recognised truthy " +
                "shapes (bool true, JsonElement(True), JsonElement(String \"true\"), string \"true\") " +
                "must all surface as true.");
    }

    private static JsonElement ParseJson(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
