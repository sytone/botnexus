using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// Pins the session-scoped prompt-cache routing hints.
///
/// A prefix cache lives on one machine. Without a stable per-conversation key a follow-up turn can
/// land elsewhere and re-process a prefix that was already cached, at full input price, reporting
/// nothing but a zero <c>cached_tokens</c>. The two hints here are the only levers the wire format
/// offers, and both are gated: an unknown field is a 400 on strict OpenAI-compatible servers, so
/// the key must never leak to an endpoint that has not been confirmed to accept it.
/// </summary>
public class PromptCacheRoutingTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.individual.githubcopilot.com")]
    [InlineData("https://api.business.githubcopilot.com")]
    public void CacheKey_IsSentToEndpointsKnownToAcceptIt(string baseUrl)
    {
        PromptCacheRouting.ResolveCacheKey(Model(baseUrl), Options("session-1"))
            .ShouldBe("session-1");
    }

    [Theory]
    [InlineData("https://api.x.ai/v1")]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("https://api.groq.com/openai/v1")]
    [InlineData("http://localhost:11434/v1")]
    public void CacheKey_IsWithheldFromEveryOtherEndpoint(string baseUrl)
    {
        PromptCacheRouting.ResolveCacheKey(Model(baseUrl), Options("session-1"))
            .ShouldBeNull();
    }

    [Fact]
    public void CacheKey_IsWithheldWhenCachingIsOff()
    {
        PromptCacheRouting.ResolveCacheKey(
            Model("https://api.openai.com/v1"),
            Options("session-1", CacheRetention.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CacheKey_IsWithheldWithoutASession(string? sessionId)
    {
        PromptCacheRouting.ResolveCacheKey(Model("https://api.openai.com/v1"), Options(sessionId))
            .ShouldBeNull();
    }

    [Fact]
    public void GrokConversationId_TracksTheProviderName()
    {
        PromptCacheRouting.ResolveGrokConversationId(
            Model("https://some-proxy.example.com/v1", provider: "xai"),
            Options("session-1")).ShouldBe("session-1");
    }

    [Fact]
    public void GrokConversationId_TracksTheBaseUrl()
    {
        PromptCacheRouting.ResolveGrokConversationId(
            Model("https://api.x.ai/v1", provider: "openai-compat"),
            Options("session-1")).ShouldBe("session-1");
    }

    [Fact]
    public void GrokConversationId_IsWithheldFromNonXaiModels()
    {
        PromptCacheRouting.ResolveGrokConversationId(
            Model("https://api.openai.com/v1"),
            Options("session-1")).ShouldBeNull();
    }

    private static LlmModel Model(string baseUrl, string provider = "openai") => new(
        Id: "test-model",
        Name: "Test Model",
        Api: "openai-completions",
        Provider: provider,
        BaseUrl: baseUrl,
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(1.0m, 2.0m, 0.1m, 1.25m),
        ContextWindow: 128000,
        MaxTokens: 16384);

    private static StreamOptions Options(
        string? sessionId,
        CacheRetention retention = CacheRetention.Short) => new()
    {
        SessionId = sessionId,
        CacheRetention = retention
    };
}
