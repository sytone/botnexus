using System.Text.Json;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Regression fence for #3130. <c>GetErrorMessage</c> runs INSIDE the error-reporting path: it is
/// the code that describes an upstream <c>response.failed</c> event to the operator. It previously
/// checked the ValueKind of the inner element but never the outer one, so a payload whose
/// <c>response</c> property was JSON <c>null</c> threw <see cref="InvalidOperationException"/> out
/// of <c>JsonElement.TryGetProperty</c> and replaced the API's real error with a parser stack
/// trace. These tests lock the contract: a best-effort, non-empty message for ANY JSON shape, and
/// never an exception -- while the well-formed happy path is unchanged.
/// </summary>
public class ResponsesStreamParserErrorMessageTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void GetErrorMessage_WellFormedErrorObject_StillYieldsCodeAndMessage()
    {
        // AC4: existing successful-parse behaviour is unchanged. This test must stay GREEN when the
        // #3130 guard is reverted, proving it is independently load-bearing from the AC1 test.
        var root = Json("""{"response":{"error":{"code":"rate_limit","message":"slow down"}}}""");

        ResponsesStreamParser.GetErrorMessage(root).ShouldBe("rate_limit: slow down");
    }

    [Fact]
    public void GetErrorMessage_WellFormedIncompleteDetails_StillYieldsReason()
    {
        var root = Json("""{"response":{"incomplete_details":{"reason":"max_output_tokens"}}}""");

        ResponsesStreamParser.GetErrorMessage(root).ShouldBe("incomplete: max_output_tokens");
    }

    [Fact]
    public void GetErrorMessage_TopLevelMessageFallback_StillYieldsMessage()
    {
        var root = Json("""{"message":"upstream exploded"}""");

        ResponsesStreamParser.GetErrorMessage(root).ShouldBe("upstream exploded");
    }

    [Fact]
    public void GetErrorMessage_NullResponseProperty_DoesNotThrow_AndReturnsBestEffortMessage()
    {
        // AC1 / the exact live payload shape from the 2026-08-13 gpt-5.6-sol failure. Reverting the
        // outer kind check reddens THIS test by name.
        var root = Json("""{"type":"response.failed","response":null}""");

        var message = Should.NotThrow(() => ResponsesStreamParser.GetErrorMessage(root));

        message.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("""{"response":42}""")]
    [InlineData("""{"response":"boom"}""")]
    [InlineData("""{"response":[1,2,3]}""")]
    [InlineData("""{"response":true}""")]
    public void GetErrorMessage_NonObjectResponseProperty_DoesNotThrow_AndReturnsBestEffortMessage(string json)
    {
        // AC2 (first branch): every non-object kind, not just null.
        var message = Should.NotThrow(() => ResponsesStreamParser.GetErrorMessage(Json(json)));

        message.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("""{"response":{"incomplete_details":null}}""")]
    [InlineData("""{"response":{"incomplete_details":7}}""")]
    [InlineData("""{"response":{"incomplete_details":"why"}}""")]
    [InlineData("""{"response":{"incomplete_details":{"reason":null}}}""")]
    public void GetErrorMessage_MalformedIncompleteDetails_DoesNotThrow_AndReturnsBestEffortMessage(string json)
    {
        // AC2 (second branch): the identical unguarded shape, fixed WITH the first, not left for
        // the next report.
        var message = Should.NotThrow(() => ResponsesStreamParser.GetErrorMessage(Json(json)));

        message.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("""{"response":{"error":null}}""")]
    [InlineData("""{"response":{"error":"nope"}}""")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"bare scalar root\"")]
    [InlineData("[]")]
    [InlineData("""{"message":null}""")]
    [InlineData("""{"message":99}""")]
    public void GetErrorMessage_AnyOtherShape_DoesNotThrow_AndReturnsBestEffortMessage(string json)
    {
        // A parser must treat provider payloads as untrusted input: an unanticipated shape degrades
        // to a worse message, never to an exception. Includes a bare scalar / null / array root,
        // where even the FIRST property access is unsafe.
        var message = Should.NotThrow(() => ResponsesStreamParser.GetErrorMessage(Json(json)));

        message.ShouldNotBeNullOrWhiteSpace();
    }
}
