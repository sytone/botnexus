using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Anthropic model discovery. These cover the two properties that make discovery worth having over
/// the hardcoded table it overlays: an advertised capability always beats the name heuristic, and
/// any failure degrades to "use built-in models" rather than to a shortened list.
/// </summary>
public sealed class AnthropicModelDiscoveryTests
{
    private const string SonnetId = "claude-sonnet-4-5-20250929";

    [Fact]
    public async Task DiscoverModels_NoCredential_ReturnsNullSoBuiltInModelsSurvive()
    {
        var provider = MakeProvider(new StubHandler(_ => Json(PageJson(SonnetId))), apiKey: null);

        var models = await provider.DiscoverModelsAsync();

        models.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverModels_BlankCredential_ReturnsNull()
    {
        var provider = MakeProvider(new StubHandler(_ => Json(PageJson(SonnetId))), apiKey: "   ");

        var models = await provider.DiscoverModelsAsync();

        models.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverModels_SendsApiKeyAndVersionHeaders()
    {
        var handler = new StubHandler(_ => Json(PageJson(SonnetId)));
        var provider = MakeProvider(handler, apiKey: "sk-ant-test");

        await provider.DiscoverModelsAsync();

        var request = handler.Requests.Single();
        request.Headers.GetValues("x-api-key").Single().ShouldBe("sk-ant-test");
        request.Headers.GetValues("anthropic-version").Single().ShouldBe("2023-06-01");
        request.RequestUri!.AbsolutePath.ShouldBe("/v1/models");
    }

    [Fact]
    public async Task DiscoverModels_NonSuccessStatus_ReturnsNullRatherThanEmptyList()
    {
        // A 401 must not be reported as "this account has no models" - that would wipe the
        // built-in entries out of the registry and empty the portal's model picker.
        var provider = MakeProvider(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            apiKey: "sk-ant-test");

        var models = await provider.DiscoverModelsAsync();

        models.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverModels_UnparseableBody_ReturnsNull()
    {
        var provider = MakeProvider(new StubHandler(_ => Json("{not json")), apiKey: "sk-ant-test");

        var models = await provider.DiscoverModelsAsync();

        models.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverModels_FollowsPaginationCursor()
    {
        var page1 = PageJson("claude-opus-5", hasMore: true, lastId: "claude-opus-5");
        var page2 = PageJson(SonnetId);
        var handler = new StubHandler(request =>
            Json(request.RequestUri!.Query.Contains("after_id", StringComparison.Ordinal) ? page2 : page1));
        var provider = MakeProvider(handler, apiKey: "sk-ant-test");

        var models = await provider.DiscoverModelsAsync();

        models.ShouldNotBeNull();
        models.Select(m => m.Id).ShouldBe(["claude-opus-5", SonnetId]);
        handler.Requests[1].RequestUri!.Query.ShouldContain("after_id=claude-opus-5");
    }

    [Fact]
    public async Task DiscoverModels_FailedSecondPage_ReturnsNullRatherThanPartialList()
    {
        // A partial list is worse than none: the models missing from it silently stop being
        // selectable, which looks like they were removed from the account.
        var page1 = PageJson("claude-opus-5", hasMore: true, lastId: "claude-opus-5");
        var handler = new StubHandler(request =>
            request.RequestUri!.Query.Contains("after_id", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(page1));
        var provider = MakeProvider(handler, apiKey: "sk-ant-test");

        var models = await provider.DiscoverModelsAsync();

        models.ShouldBeNull();
    }

    [Fact]
    public void MapToLlmModel_UsesAdvertisedTokenBudgets()
    {
        var info = Info(SonnetId, displayName: "Claude Sonnet 4.5", maxInputTokens: 200_000, maxTokens: 64_000);

        var model = AnthropicModelDiscoveryProvider.MapToLlmModel(info);

        model.ShouldNotBeNull();
        model.Id.ShouldBe(SonnetId);
        model.Name.ShouldBe("Claude Sonnet 4.5");
        model.Api.ShouldBe("anthropic-messages");
        model.Provider.ShouldBe("anthropic");
        model.ContextWindow.ShouldBe(200_000);
        model.MaxTokens.ShouldBe(64_000);
    }

    [Fact]
    public void MapToLlmModel_ContextWindowBeyondStandardCeiling_MarksExtendedContext()
    {
        var info = Info(SonnetId, maxInputTokens: 1_000_000);

        var model = AnthropicModelDiscoveryProvider.MapToLlmModel(info);

        model!.SupportsExtendedContextWindow.ShouldBeTrue();
    }

    [Fact]
    public void MapToLlmModel_LongContextModel_RegistersStandardTierNotAdvertisedMaximum()
    {
        // 1M is opt-in per request, not the default. Registering the advertised maximum would make
        // the beta tier look like the default and inflate every budget derived from ContextWindow.
        // BuiltInModels encodes the same distinction for Sonnet 4.5.
        var info = Info(SonnetId, maxInputTokens: 1_000_000);

        var model = AnthropicModelDiscoveryProvider.MapToLlmModel(info);

        model!.ContextWindow.ShouldBe(200_000);
        model.SupportsExtendedContextWindow.ShouldBeTrue();
    }

    [Fact]
    public void MapToLlmModel_ShortContextModel_KeepsAdvertisedWindow()
    {
        var info = Info("claude-haiku-4-5-20251001", maxInputTokens: 200_000);

        var model = AnthropicModelDiscoveryProvider.MapToLlmModel(info);

        model!.ContextWindow.ShouldBe(200_000);
    }

    [Fact]
    public void MapToLlmModel_MissingDisplayName_FallsBackToId()
    {
        var model = AnthropicModelDiscoveryProvider.MapToLlmModel(Info(SonnetId, displayName: null));

        model!.Name.ShouldBe(SonnetId);
    }

    [Fact]
    public void MapToLlmModel_BlankId_ReturnsNull()
    {
        AnthropicModelDiscoveryProvider.MapToLlmModel(Info("  ")).ShouldBeNull();
    }

    [Fact]
    public void MapToLlmModel_ImageInputUnsupported_OmitsImageModality()
    {
        var info = Info(SonnetId);
        info.Capabilities = new AnthropicModelCapabilities
        {
            ImageInput = new AnthropicCapabilityFlag { Supported = false }
        };

        var model = AnthropicModelDiscoveryProvider.MapToLlmModel(info);

        model!.Input.ShouldBe(["text"]);
    }

    [Fact]
    public void MapToLlmModel_ThinkingUnsupported_IsNotReasoningEvenWhenNameSuggestsOtherwise()
    {
        // The stated capability is ground truth for the account; the name heuristic is a guess.
        var info = Info("claude-opus-5");
        info.Capabilities = new AnthropicModelCapabilities
        {
            Thinking = new AnthropicCapabilityFlag { Supported = false }
        };

        var model = AnthropicModelDiscoveryProvider.MapToLlmModel(info);

        model!.Reasoning.ShouldBeFalse();
    }

    [Fact]
    public void ResolveExtraHighThinking_AdvertisedXHigh_IsTrue()
    {
        var effort = new AnthropicEffortCapability
        {
            Supported = true,
            XHigh = new AnthropicCapabilityFlag { Supported = true }
        };

        AnthropicModelDiscoveryProvider.ResolveExtraHighThinking(effort, "claude-opus-5").ShouldBeTrue();
    }

    [Fact]
    public void ResolveExtraHighThinking_EffortStatedWithoutHighTiers_IsFalse()
    {
        // Authoritative in both directions: an effort node that names neither xhigh nor max pins
        // the model to the lower tiers even though the heuristic would widen "claude-opus-5".
        var effort = new AnthropicEffortCapability { Supported = true };

        AnthropicModelDiscoveryProvider.ResolveExtraHighThinking(effort, "claude-opus-5").ShouldBeFalse();
    }

    [Fact]
    public void ResolveExtraHighThinking_NoEffortNode_FallsBackToNameHeuristic()
    {
        AnthropicModelDiscoveryProvider
            .ResolveExtraHighThinking(null, "claude-opus-5")
            .ShouldBe(Core.Registry.ModelCapabilityHeuristics.SupportsExtraHighThinking("claude-opus-5"));
    }

    private static AnthropicModelDiscoveryProvider MakeProvider(HttpMessageHandler handler, string? apiKey) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com") },
            _ => Task.FromResult(apiKey),
            NullLogger<AnthropicModelDiscoveryProvider>.Instance);

    private static AnthropicModelInfo Info(
        string id,
        string? displayName = "Display Name",
        int? maxInputTokens = 200_000,
        int? maxTokens = 8192) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            MaxInputTokens = maxInputTokens,
            MaxTokens = maxTokens
        };

    private static string PageJson(string id, bool hasMore = false, string? lastId = null)
    {
        var payload = new
        {
            data = new[]
            {
                new
                {
                    type = "model",
                    id,
                    display_name = id,
                    max_input_tokens = 200_000,
                    max_tokens = 8192
                }
            },
            has_more = hasMore,
            last_id = lastId
        };

        return JsonSerializer.Serialize(payload);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Records every request and replies from a caller-supplied factory, so a test can assert on
    /// the pagination cursor as well as on the mapped result.
    /// </summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }
}
