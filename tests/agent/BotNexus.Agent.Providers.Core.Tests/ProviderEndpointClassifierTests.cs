namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// Tests for the cloud-vs-local provider classification used to decide whether the cron/compaction
/// stream-setup idle cap (StreamSetupTimeoutMs) should be applied (#1652). Cloud endpoints get the
/// cap so a stalled first-token never wedges a background call; local/self-hosted endpoints
/// (ollama, vllm, lmstudio, sglang on localhost/127.0.0.1) are left uncapped because they are
/// legitimately slow to warm up.
/// </summary>
public class ProviderEndpointClassifierTests
{
    [Theory]
    [InlineData("https://api.anthropic.com")]
    [InlineData("https://api.githubcopilot.com")]
    [InlineData("https://api.individual.githubcopilot.com")]
    [InlineData("https://api.enterprise.githubcopilot.com")]
    [InlineData("https://api.openai.com/v1")]
    public void IsLocalProviderBaseUrl_CloudHost_ReturnsFalse(string baseUrl)
    {
        ProviderEndpointClassifier.IsLocalProviderBaseUrl(baseUrl).ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:8000")]
    [InlineData("http://127.0.0.1:8000")]
    [InlineData("http://localhost:1234")]
    [InlineData("http://localhost:30000")]
    [InlineData("http://LOCALHOST:11434")]
    [InlineData("http://127.0.0.1")]
    public void IsLocalProviderBaseUrl_LocalHost_ReturnsTrue(string baseUrl)
    {
        ProviderEndpointClassifier.IsLocalProviderBaseUrl(baseUrl).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsLocalProviderBaseUrl_NullOrEmpty_ReturnsFalse(string? baseUrl)
    {
        // Null/empty means "unknown host"; treat as cloud so the safety cap still applies and a
        // mis-registered model cannot stall a background call forever.
        ProviderEndpointClassifier.IsLocalProviderBaseUrl(baseUrl).ShouldBeFalse();
    }
}
