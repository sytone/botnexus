using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.MicrosoftFoundry;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.MicrosoftFoundry.Tests;

public class MicrosoftFoundryResponsesProviderTests
{
    [Fact]
    public async Task Stream_TwoNamedInstances_RouteToIsolatedEndpointAndAuthentication()
    {
        var alphaHandler = new RecordingHandler();
        var betaHandler = new RecordingHandler();
        using var provider = Provider(
            [
                Instance("alpha-foundry", "https://alpha.services.ai.azure.com", "alpha-key"),
                Instance("beta-foundry", "https://beta.services.ai.azure.com", "beta-key")
            ],
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["alpha-foundry"] = alphaHandler,
                ["beta-foundry"] = betaHandler
            });

        var alpha = await provider.Stream(
                Model("alpha-deployment", "alpha-foundry", "https://alpha.services.ai.azure.com"),
                Context(), new StreamOptions())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var beta = await provider.Stream(
                Model("beta-deployment", "beta-foundry", "https://beta.services.ai.azure.com"),
                Context(), new StreamOptions())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        alpha.StopReason.ShouldNotBe(StopReason.Error);
        beta.StopReason.ShouldNotBe(StopReason.Error);
        provider.Api.ShouldBe("microsoft-foundry-responses");
        alphaHandler.RequestUri.ShouldBe(new Uri("https://alpha.services.ai.azure.com/openai/v1/responses"));
        betaHandler.RequestUri.ShouldBe(new Uri("https://beta.services.ai.azure.com/openai/v1/responses"));
        alphaHandler.Headers["api-key"].ShouldBe("alpha-key");
        betaHandler.Headers["api-key"].ShouldBe("beta-key");
        using var alphaPayload = JsonDocument.Parse(alphaHandler.Body!);
        using var betaPayload = JsonDocument.Parse(betaHandler.Body!);
        alphaPayload.RootElement.GetProperty("model").GetString().ShouldBe("alpha-deployment");
        betaPayload.RootElement.GetProperty("model").GetString().ShouldBe("beta-deployment");
    }

    [Fact]
    public async Task Stream_ApiKeyAuthenticationReplacesConflictingGenericCredentialHeaders()
    {
        var handler = new RecordingHandler();
        using var provider = Provider("team-foundry", handler, MicrosoftFoundryAuthentication.ApiKey("configured-key"));
        var model = Model(
            "deployment",
            "team-foundry",
            "https://example.services.ai.azure.com",
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer model-token",
                ["api-key"] = "model-key"
            });
        var options = new StreamOptions
        {
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer option-token",
                ["api-key"] = "option-key"
            }
        };

        var result = await provider.Stream(model, Context(), options).GetResultAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.ShouldNotBe(StopReason.Error);
        handler.HeaderValues["api-key"].ShouldBe(new[] { "configured-key" });
        handler.HeaderValues.ShouldNotContainKey("Authorization");
    }

    [Fact]
    public async Task Stream_EntraAuthenticationReplacesConflictingGenericCredentialHeaders()
    {
        var handler = new RecordingHandler();
        var credential = new CountingCredential();
        using var provider = Provider("team-foundry", handler, MicrosoftFoundryAuthentication.Entra(credential));
        var model = Model(
            "deployment",
            "team-foundry",
            "https://example.services.ai.azure.com",
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer model-token",
                ["api-key"] = "model-key"
            });
        var options = new StreamOptions
        {
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer option-token",
                ["api-key"] = "option-key"
            }
        };

        var result = await provider.Stream(model, Context(), options).GetResultAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.ShouldNotBe(StopReason.Error);
        handler.HeaderValues["Authorization"].ShouldBe(new[] { "Bearer token" });
        handler.HeaderValues.ShouldNotContainKey("api-key");
        credential.CallCount.ShouldBe(1);
    }

    [Fact]
    public void Stream_UnknownProviderName_IsRejectedBeforeSending()
    {
        var handler = new RecordingHandler();
        using var provider = Provider("team-foundry", handler, MicrosoftFoundryAuthentication.ApiKey("key"));

        Action act = () => provider.Stream(
            Model("deployment", "other-foundry", "https://example.services.ai.azure.com"),
            Context(),
            new StreamOptions());

        Should.Throw<InvalidOperationException>(act).Message.ShouldContain("other-foundry");
        handler.SendCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("http://example.services.ai.azure.com")]
    [InlineData("https://evil.example")]
    [InlineData("https://example.services.ai.azure.com.evil.example")]
    public async Task Stream_RejectsUnapprovedOrNonHttpsOriginBeforeAttachingCredentials(string baseUrl)
    {
        var handler = new RecordingHandler();
        var credential = new CountingCredential();
        using var provider = Provider("team-foundry", handler, MicrosoftFoundryAuthentication.Entra(credential));

        var result = await provider.Stream(Model("deployment", "team-foundry", baseUrl), Context(), new StreamOptions())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.ShouldBe(StopReason.Error);
        credential.CallCount.ShouldBe(0);
        handler.SendCount.ShouldBe(0);
    }

    [Fact]
    public async Task Stream_FailedResponse_UsesFoundryNameAndRedactsSecret()
    {
        const string secret = "synthetic-foundry-secret";
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, $"rejected credential {secret}");
        using var provider = Provider(
            "team-foundry", handler, MicrosoftFoundryAuthentication.ApiKey("key"), new StubRedactor(secret));

        var result = await provider.Stream(
                Model("deployment", "team-foundry", "https://example.services.ai.azure.com"),
                Context(), new StreamOptions())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Microsoft Foundry");
        result.ErrorMessage.ShouldContain("[REDACTED]");
        result.ErrorMessage.ShouldNotContain(secret);
        result.ErrorMessage.ShouldNotContain("OpenAI");
    }

    [Fact]
    public void CreateSecureHandler_DisablesAutomaticRedirects()
    {
        using var handler = MicrosoftFoundryResponsesProvider.CreateSecureHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public async Task Stream_RefusesRedirectWithoutForwardingCredentials()
    {
        var terminal = new RecordingHandler();
        var redirect = new RedirectHandler(terminal);
        using var provider = Provider("team-foundry", redirect, MicrosoftFoundryAuthentication.ApiKey("secret"));

        var result = await provider.Stream(
                Model("deployment", "team-foundry", "https://example.services.ai.azure.com"),
                Context(),
                new StreamOptions())
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.ShouldBe(StopReason.Error);
        terminal.SendCount.ShouldBe(0);
    }

    private static MicrosoftFoundryResponsesProvider Provider(
        string name,
        HttpMessageHandler handler,
        MicrosoftFoundryAuthentication authentication,
        ISecretRedactor? secretRedactor = null) =>
        Provider([Instance(name, "https://example.services.ai.azure.com", authentication)],
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { [name] = handler }, secretRedactor);

    private static MicrosoftFoundryResponsesProvider Provider(
        IEnumerable<MicrosoftFoundryResponsesInstance> instances,
        IReadOnlyDictionary<string, HttpMessageHandler> handlers,
        ISecretRedactor? secretRedactor = null) =>
        new(instances, name => handlers[name], NullLogger<MicrosoftFoundryResponsesProvider>.Instance, secretRedactor);

    private static MicrosoftFoundryResponsesInstance Instance(string name, string origin, string apiKey) =>
        Instance(name, origin, MicrosoftFoundryAuthentication.ApiKey(apiKey));

    private static MicrosoftFoundryResponsesInstance Instance(
        string name, string origin, MicrosoftFoundryAuthentication authentication) =>
        new(name, new Uri(origin), authentication);

    private static LlmModel Model(
        string id,
        string provider,
        string baseUrl,
        IReadOnlyDictionary<string, string>? headers = null) => new(
        Id: id,
        Name: id,
        Api: "microsoft-foundry-responses",
        Provider: provider,
        BaseUrl: baseUrl,
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 4096,
        Headers: headers);

    private static Context Context() => new(
        "You are helpful",
        [new UserMessage(new UserMessageContent("hello"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

    private sealed class RecordingHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseBody = null) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> HeaderValues { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            RequestUri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            HeaderValues = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
            Headers = HeaderValues.ToDictionary(
                pair => pair.Key,
                pair => string.Join(",", pair.Value),
                StringComparer.OrdinalIgnoreCase);

            var body = responseBody ??
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    statusCode == HttpStatusCode.OK ? "text/event-stream" : "application/json")
            };
        }
    }

    private sealed class RedirectHandler(HttpMessageHandler terminal) : DelegatingHandler(terminal)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.example/steal") }
            });
    }

    private sealed class StubRedactor(string secret) : ISecretRedactor
    {
        public string Redact(string input) => input.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        public string RedactForExternalDelivery(string input) => Redact(input);
    }

    private sealed class CountingCredential : TokenCredential
    {
        private int _callCount;
        public int CallCount => _callCount;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
