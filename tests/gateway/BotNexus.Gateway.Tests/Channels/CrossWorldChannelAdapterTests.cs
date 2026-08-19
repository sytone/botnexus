using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
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

    // -----------------------------------------------------------------------------------------
    // #3399: relay error bodies must be bounded and redacted.
    //
    // The relay authenticates with a shared X-Cross-World-Key header. A peer world that echoes the
    // request headers into its error page hands that credential straight back, and the old error
    // path interpolated the whole body verbatim into an InvalidOperationException message. The two
    // defects are independent, so they are pinned independently.
    // -----------------------------------------------------------------------------------------

    /// <summary>The relay key used by the #3399 tests. Synthetic; matches no real credential.</summary>
    private const string SyntheticRelayKey = "cwk-FAKE00000000000000000000000000TESTONLY";

    [Fact]
    public async Task ExchangeAsync_ErrorBodyEchoingTheRelayKey_DoesNotLeakItIntoTheException()
    {
        // AC3: the peer reflects the credential it was authenticated with. This is the whole defect.
        var ex = await CaptureRelayFailureAsync(
            errorBody: $"{{\"error\":\"rejected header X-Cross-World-Key: {SyntheticRelayKey}\"}}",
            redactor: new StubRedactor());

        ex.Message.ShouldNotContain(SyntheticRelayKey,
            customMessage: "#3399: an error body echoing X-Cross-World-Key must be redacted before it " +
                "reaches the exception message. That message is surfaced and persisted, so the " +
                "shared cross-world credential would otherwise leak from a single misbehaving peer.");
        ex.Message.ShouldContain("[REDACTED]");
        // Redaction must not cost the diagnosis: the status code and surrounding context survive.
        ex.Message.ShouldContain("502");
        ex.Message.ShouldContain("rejected header");
    }

    [Fact]
    public async Task ExchangeAsync_ReasonPhraseEchoingTheRelayKey_DoesNotLeakItIntoTheException()
    {
        // The reason phrase is as remote-controlled as the body. Scrubbing only the body would leave
        // an obvious second channel for the same credential.
        var ex = await CaptureRelayFailureAsync(
            errorBody: "nothing here",
            redactor: new StubRedactor(),
            reasonPhrase: $"denied {SyntheticRelayKey}");

        ex.Message.ShouldNotContain(SyntheticRelayKey,
            customMessage: "#3399: ReasonPhrase is peer-controlled text and must go through the " +
                "redactor too.");
    }

    [Fact]
    public async Task ExchangeAsync_WithoutARedactor_StillLeaksTheKey_NonVacuityPin()
    {
        // Non-vacuity: with no redactor wired the key survives, proving the assertions above pass
        // because the redactor ran, not because some other layer happens to scrub the text.
        var ex = await CaptureRelayFailureAsync(
            errorBody: $"echo {SyntheticRelayKey}",
            redactor: null);

        ex.Message.ShouldContain(SyntheticRelayKey,
            customMessage: "Non-vacuity pin: with a null redactor the adapter is documented to be a " +
                "no-op. If this fails, the redaction assertions above are not testing what they claim.");
    }

    [Fact]
    public async Task ExchangeAsync_OversizedErrorBody_IsTruncatedToTheDocumentedBound()
    {
        // AC4: an unbounded ReadAsStringAsync against an arbitrary peer is an availability problem
        // on the same path. The message must be bounded regardless of how much the peer sends.
        var oversized = new string('q', ProviderHttpErrorHelper.MaxErrorDetailChars * 50);

        var ex = await CaptureRelayFailureAsync(errorBody: oversized, redactor: null);

        ex.Message.Length.ShouldBeLessThan(oversized.Length,
            customMessage: "#3399: the relay error body must be read as a bounded prefix, not " +
                "materialised whole into the exception message.");
        ex.Message.ShouldContain(ProviderHttpErrorHelper.TruncationMarker,
            customMessage: "A truncated body must say so; a silently clipped message is " +
                "indistinguishable from a complete one.");
        ex.Message.ShouldEndWith(ProviderHttpErrorHelper.TruncationMarker);
    }

    [Fact]
    public async Task ExchangeAsync_SmallErrorBody_IsPreservedWholeAndUnmarked()
    {
        // The bound must not degrade the ordinary case: a small body still reaches the operator in
        // full, and carries no truncation marker.
        var ex = await CaptureRelayFailureAsync(errorBody: "target agent not found", redactor: null);

        ex.Message.ShouldContain("target agent not found");
        ex.Message.ShouldNotContain(ProviderHttpErrorHelper.TruncationMarker);
    }

    [Fact]
    public async Task ExchangeAsync_SuccessfulRelay_DoesNotConsultTheRedactor()
    {
        // The redaction seam belongs to the error path only. A healthy relay must not pay for it,
        // and must not have its payload rewritten.
        var redactor = new StubRedactor();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CrossWorldRelayResponse
                {
                    Response = SyntheticRelayKey,
                    Status = "active",
                    SessionId = "remote-session-1"
                })
            }));

        var adapter = new CrossWorldChannelAdapter(
            NullLogger<CrossWorldChannelAdapter>.Instance,
            new HttpClient(handler),
            options: null,
            secretRedactor: redactor);

        var result = await adapter.ExchangeAsync(BuildOutbound());

        result.Response.ShouldBe(SyntheticRelayKey,
            customMessage: "The success path must return the peer's payload untouched - redaction is " +
                "an error-path concern and must not rewrite legitimate relay content.");
        redactor.RedactCallCount.ShouldBe(0);
    }

    private static async Task<InvalidOperationException> CaptureRelayFailureAsync(
        string errorBody,
        ISecretRedactor? redactor,
        string? reasonPhrase = null)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(errorBody, Encoding.UTF8, "text/plain")
            };
            if (reasonPhrase is not null)
                response.ReasonPhrase = reasonPhrase;
            return Task.FromResult(response);
        });

        var adapter = new CrossWorldChannelAdapter(
            NullLogger<CrossWorldChannelAdapter>.Instance,
            new HttpClient(handler),
            options: null,
            secretRedactor: redactor);

        return await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.ExchangeAsync(BuildOutbound()));
    }

    private static OutboundMessage BuildOutbound() => new()
    {
        ChannelType = ChannelKey.From("cross-world"),
        ChannelAddress = ChannelAddress.From("gateway-b"),
        Content = "hello",
        Metadata = new Dictionary<string, object?>
        {
            ["endpoint"] = "https://gateway-b.internal",
            ["sourceWorldId"] = "world-a",
            ["sourceAgentId"] = "init",
            ["targetAgentId"] = "tgt",
            ["conversationId"] = ConversationId.Create().Value,
            ["apiKey"] = SyntheticRelayKey
        }
    };

    /// <summary>
    /// Minimal redactor that removes the synthetic relay key. Deliberately a test double rather than
    /// the gateway's concrete <c>SecretRedactor</c>: what is under test here is that the adapter
    /// invokes the seam before interpolation, not that the gateway's pattern set is correct (that is
    /// pinned by <c>SecretRedactionFenceArchitectureTests</c>).
    /// </summary>
    private sealed class StubRedactor : ISecretRedactor
    {
        public int RedactCallCount { get; private set; }

        public string Redact(string input)
        {
            RedactCallCount++;
            return input.Replace(SyntheticRelayKey, "[REDACTED]", StringComparison.Ordinal);
        }

        public string RedactForExternalDelivery(string input) => Redact(input);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
