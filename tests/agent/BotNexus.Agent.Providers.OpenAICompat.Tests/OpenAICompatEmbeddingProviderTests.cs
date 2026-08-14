using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Embeddings;
using BotNexus.Agent.Providers.OpenAICompat;

namespace BotNexus.Agent.Providers.OpenAICompat.Tests;

/// <summary>
/// Wire-shape and failure tests for the OpenAI-compatible embeddings capability (#2855).
/// </summary>
/// <remarks>
/// Acceptance criterion 3 requires the end-to-end path be exercised against a REAL endpoint shape
/// rather than a stubbed provider, so every test here drives the actual
/// <see cref="OpenAICompatEmbeddingProvider"/> through an <see cref="HttpMessageHandler"/> that
/// captures or breaks the request. A stub returning a canned vector would prove the test double
/// works and nothing about the request the platform actually emits.
/// </remarks>
public sealed class OpenAICompatEmbeddingProviderTests
{
    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>A handler that never produces a response: the transport itself is broken.</summary>
    private sealed class FaultInjectingHandler(Exception fault) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw fault;
        }
    }

    /// <summary>Redactor that masks one known secret, so the assertion is about the wiring not the algorithm.</summary>
    private sealed class StubRedactor(string secret) : BotNexus.Gateway.Abstractions.Security.ISecretRedactor
    {
        public string Redact(string input) => input.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        public string RedactForExternalDelivery(string input) => Redact(input);
    }

    private static OpenAICompatEmbeddingProvider Provider(
        HttpMessageHandler handler,
        string baseUrl = "http://localhost:11434/v1",
        string? apiKey = null)
        => new(
            new HttpClient(handler),
            "ollama",
            baseUrl,
            [new EmbeddingModelDescriptor("nomic-embed-text", 3)],
            apiKey);

    private const string ValidResponse = """
        {"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.25,-0.5,0.75]}],"model":"nomic-embed-text"}
        """;

    // -- AC3: the wire request the platform actually emits --

    [Fact]
    public async Task EmbedAsync_PostsToTheEmbeddingsEndpointWithModelAndInput()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, ValidResponse);

        var vector = await Provider(handler).EmbedAsync("nomic-embed-text", "hello world");

        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("http://localhost:11434/v1/embeddings", handler.Request.RequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("nomic-embed-text", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello world", body.RootElement.GetProperty("input").GetString());

        Assert.NotNull(vector);
        Assert.Equal([0.25f, -0.5f, 0.75f], vector!);
    }

    [Fact]
    public async Task EmbedAsync_SendsBearerToken_WhenApiKeyConfigured()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, ValidResponse);

        await Provider(handler, apiKey: "sk-test-key").EmbedAsync("nomic-embed-text", "hello");

        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test-key", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task EmbedAsync_OmitsAuthorizationHeader_WhenNoApiKeyConfigured()
    {
        // A local Ollama rejects nothing, but sending an empty bearer token to an endpoint that
        // validates one turns a working setup into a 401.
        var handler = new CapturingHandler(HttpStatusCode.OK, ValidResponse);

        await Provider(handler).EmbedAsync("nomic-embed-text", "hello");

        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task EmbedAsync_NormalisesTrailingSlashInBaseUrl()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, ValidResponse);

        await Provider(handler, baseUrl: "http://localhost:11434/v1/").EmbedAsync("nomic-embed-text", "hello");

        Assert.Equal("http://localhost:11434/v1/embeddings", handler.Request!.RequestUri!.ToString());
    }

    // -- AC5 (provider half): endpoint failures surface as faults the memory seam can catch --

    [Fact]
    public async Task EmbedAsync_Throws_OnHttpErrorStatus()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotFound, """{"error":"model not found"}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler).EmbedAsync("nomic-embed-text", "hello"));

        // The operator needs status and body to tell a 404 from a 401 from a model-name typo.
        Assert.Contains("404", ex.Message);
        Assert.Contains("model not found", ex.Message);
    }

    [Fact]
    public async Task EmbedAsync_Throws_AuthenticationException_On401()
    {
        // Routing through ProviderHttpErrorHelper is what makes a 401 self-diagnosing rather than
        // an undiagnosable generic transport error.
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, """{"error":"bad key"}""");

        await Assert.ThrowsAsync<Core.ProviderAuthenticationException>(
            () => Provider(handler).EmbedAsync("nomic-embed-text", "hello"));
    }

    [Fact]
    public async Task EmbedAsync_RedactsSecretsOutOfTheErrorBody()
    {
        // #2881: an endpoint that echoes the offending Authorization header back on a failure must
        // not leak the credential into an exception message, which is persisted session-visibly.
        var handler = new CapturingHandler(HttpStatusCode.BadRequest, """{"error":"rejected sk-super-secret-value"}""");
        var provider = new OpenAICompatEmbeddingProvider(
            new HttpClient(handler),
            "ollama",
            "http://localhost:11434/v1",
            [new EmbeddingModelDescriptor("nomic-embed-text", 3)],
            apiKey: "sk-super-secret-value",
            secretRedactor: new StubRedactor("sk-super-secret-value"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.EmbedAsync("nomic-embed-text", "hello"));

        Assert.DoesNotContain("sk-super-secret-value", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
    }

    [Fact]
    public async Task EmbedAsync_PropagatesTransportFault()
    {
        var handler = new FaultInjectingHandler(new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler).EmbedAsync("nomic-embed-text", "hello"));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsNull_ForBlankInput_WithoutCallingTheEndpoint()
    {
        var handler = new FaultInjectingHandler(new HttpRequestException("should never be called"));

        Assert.Null(await Provider(handler).EmbedAsync("nomic-embed-text", "   "));
        Assert.Equal(0, handler.CallCount);
    }

    // -- Response shapes that are well-formed but carry no vector --

    [Theory]
    [InlineData("""{"object":"list","data":[]}""")]
    [InlineData("""{"object":"list"}""")]
    [InlineData("""{"data":[{"object":"embedding"}]}""")]
    [InlineData("""{"data":[{"embedding":"not-an-array"}]}""")]
    [InlineData("""{"data":[{"embedding":[1.0,"nope"]}]}""")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void ParseVector_ReturnsNull_ForResponsesCarryingNoUsableVector(string payload)
    {
        Assert.Null(OpenAICompatEmbeddingProvider.ParseVector(payload));
    }

    [Fact]
    public void ParseVector_ReadsTheFirstDataEntry()
    {
        var vector = OpenAICompatEmbeddingProvider.ParseVector(
            """{"data":[{"embedding":[1.0,2.0]},{"embedding":[9.0,9.0]}]}""");

        Assert.NotNull(vector);
        Assert.Equal([1f, 2f], vector!);
    }

    // -- Capability shape --

    [Fact]
    public void Provider_DeclaresItsKeyAndModels()
    {
        var provider = Provider(new CapturingHandler(HttpStatusCode.OK, ValidResponse));

        Assert.Equal("ollama", provider.ProviderKey);
        var model = Assert.Single(provider.Models);
        Assert.Equal("nomic-embed-text", model.ModelId);
        Assert.Equal(3, model.Dimensions);
    }

    [Fact]
    public void Constructor_RejectsMissingProviderKeyOrBaseUrl()
    {
        var http = new HttpClient(new CapturingHandler(HttpStatusCode.OK, ValidResponse));
        IReadOnlyList<EmbeddingModelDescriptor> models = [new("m", 3)];

        Assert.Throws<ArgumentException>(() => new OpenAICompatEmbeddingProvider(http, "", "http://x", models));
        Assert.Throws<ArgumentException>(() => new OpenAICompatEmbeddingProvider(http, "ollama", "  ", models));
    }
}
