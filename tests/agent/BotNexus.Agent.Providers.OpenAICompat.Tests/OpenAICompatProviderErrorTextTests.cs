using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.OpenAICompat.Tests;

/// <summary>
/// Regression coverage for #3758: a provider failure must name the provider and the model id that
/// was actually transmitted, in the text the user reads, not only in the structured
/// <c>AssistantMessage.Provider</c>/<c>ModelId</c> fields the chat surface never renders.
/// </summary>
/// <remarks>
/// Non-vacuity: every assertion below is a positive substring assertion against the rendered
/// <see cref="TextContent"/>. Against pre-fix <c>main</c> the rendered text is the bare
/// <c>"HTTP 400: {json}"</c> interpolation, which contains neither the provider name nor the model
/// id, so <see cref="Stream_HttpErrorText_NamesProviderAndModel"/> fails there. The suite cannot be
/// satisfied by a provider that produces no message at all: <c>result.Content</c> is asserted
/// non-empty and its text is read.
/// </remarks>
public class OpenAICompatProviderErrorTextTests
{
    private const string Provider = "github-copilot";
    private const string ModelId = "gpt-5.6-sol";

    private const string ModelNotSupportedBody =
        """{"error":{"message":"The requested model is not supported.","code":"model_not_supported","param":"model","type":"invalid_request_error"}}""";

    [Fact]
    public async Task Stream_HttpErrorText_NamesProviderAndModel()
    {
        var result = await RunProviderAsync(HttpStatusCode.BadRequest, ModelNotSupportedBody);

        var text = RenderedText(result);
        text.ShouldContain(Provider);
        text.ShouldContain(ModelId);
    }

    [Fact]
    public async Task Stream_OpenAiShapedErrorBody_SurfacesCodeAndMessageAsReadableText()
    {
        var result = await RunProviderAsync(HttpStatusCode.BadRequest, ModelNotSupportedBody);

        var text = RenderedText(result);
        text.ShouldContain("model_not_supported");
        text.ShouldContain("The requested model is not supported.");
        // The readable rendering replaces the raw JSON envelope rather than merely wrapping it.
        text.ShouldNotContain("\"invalid_request_error\"");
    }

    [Fact]
    public async Task Stream_NonJsonErrorBody_StillNamesProviderAndModelWithoutThrowing()
    {
        var result = await RunProviderAsync(HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>");

        result.StopReason.ShouldBe(StopReason.Error);
        var text = RenderedText(result);
        text.ShouldContain(Provider);
        text.ShouldContain(ModelId);
        // The unparseable body is preserved verbatim rather than swallowed.
        text.ShouldContain("502 Bad Gateway");
    }

    [Fact]
    public async Task Stream_EmptyErrorBody_DegradesWithoutThrowing()
    {
        var result = await RunProviderAsync(HttpStatusCode.InternalServerError, string.Empty);

        result.StopReason.ShouldBe(StopReason.Error);
        var text = RenderedText(result);
        text.ShouldContain(Provider);
        text.ShouldContain(ModelId);
        text.ShouldContain("500");
    }

    [Fact]
    public async Task Stream_ErrorMessage_StructuredProviderAndModelFieldsUnchanged()
    {
        var result = await RunProviderAsync(HttpStatusCode.BadRequest, ModelNotSupportedBody);

        // Acceptance criterion 7: this change ADDS to the rendered text and removes nothing from
        // the structured record.
        result.Provider.ShouldBe(Provider);
        result.ModelId.ShouldBe(ModelId);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DescribeHttpFailure_SentinelDiscardedBody_IsPassedThroughVerbatim()
    {
        // The provider substitutes this sentinel when the error body blows the read cap (#1685).
        const string Sentinel = "<error body exceeded 65536 bytes and was discarded>";

        var text = OpenAICompatErrorText.DescribeHttpFailure(413, Sentinel);

        text.ShouldContain("413");
        text.ShouldContain(Sentinel);
    }

    [Fact]
    public void Describe_BlankDetail_ProducesReadableTextRatherThanADanglingColon()
    {
        var text = OpenAICompatErrorText.Describe(TestModel(), "   ");

        text.ShouldContain(Provider);
        text.ShouldContain(ModelId);
        text.Trim().ShouldNotEndWith(":");
    }

    private static string RenderedText(AssistantMessage result)
    {
        result.Content.ShouldNotBeEmpty();
        var block = result.Content.OfType<TextContent>().FirstOrDefault();
        block.ShouldNotBeNull();
        return block!.Text;
    }

    private static LlmModel TestModel() => new(
        Id: ModelId,
        Name: ModelId,
        Api: "openai-compat",
        Provider: Provider,
        BaseUrl: "http://localhost:11434/v1",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 8192,
        MaxTokens: 2048);

    private static async Task<AssistantMessage> RunProviderAsync(HttpStatusCode status, string body)
    {
        var handler = new FixedResponseHandler(status, body);
        var provider = new OpenAICompatProvider(new HttpClient(handler));
        var context = new Context(
            SystemPrompt: "error-text",
            Messages: [new UserMessage(new UserMessageContent("hi"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

        var stream = provider.Stream(TestModel(), context, new Core.SimpleStreamOptions { ApiKey = "test-key" });
        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
