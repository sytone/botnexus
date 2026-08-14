using BotNexus.Agent.Providers.Core.Embeddings;

namespace BotNexus.Agent.Providers.Core.Tests.Embeddings;

/// <summary>
/// Fingerprint derivation for hosted embedding endpoints (#2855, acceptance criterion 4).
/// </summary>
/// <remarks>
/// These tests state the rule the fingerprint enforces: every difference the platform can OBSERVE
/// about a hosted endpoint produces a different fingerprint, so vectors from two different hosted
/// models can never be compared. The corresponding <c>EmbeddingIdentity.Matches</c> proof lives in
/// the gateway composition tests, where both halves are in scope.
/// </remarks>
public sealed class HostedEmbeddingFingerprintTests
{
    [Fact]
    public void Derive_IsDeterministic()
    {
        var first = HostedEmbeddingFingerprint.Derive("ollama", "http://localhost:11434/v1", "nomic-embed-text", 768);
        var second = HostedEmbeddingFingerprint.Derive("ollama", "http://localhost:11434/v1", "nomic-embed-text", 768);

        Assert.Equal(first, second);
        Assert.NotEmpty(first);
    }

    [Fact]
    public void Derive_DiffersByModelId()
    {
        Assert.NotEqual(
            HostedEmbeddingFingerprint.Derive("openai", "https://api.openai.com/v1", "text-embedding-3-small", 1536),
            HostedEmbeddingFingerprint.Derive("openai", "https://api.openai.com/v1", "text-embedding-3-large", 1536));
    }

    [Fact]
    public void Derive_DiffersByProviderKey()
    {
        // The same model NAME served by two different services is not the same coordinate space.
        Assert.NotEqual(
            HostedEmbeddingFingerprint.Derive("ollama", "http://host/v1", "shared-name", 768),
            HostedEmbeddingFingerprint.Derive("openai", "http://host/v1", "shared-name", 768));
    }

    [Fact]
    public void Derive_DiffersByBaseUrl()
    {
        Assert.NotEqual(
            HostedEmbeddingFingerprint.Derive("openai", "https://api.openai.com/v1", "m", 768),
            HostedEmbeddingFingerprint.Derive("openai", "https://contoso.openai.azure.com/v1", "m", 768));
    }

    [Fact]
    public void Derive_DiffersByDimensions()
    {
        Assert.NotEqual(
            HostedEmbeddingFingerprint.Derive("openai", "https://api.openai.com/v1", "m", 768),
            HostedEmbeddingFingerprint.Derive("openai", "https://api.openai.com/v1", "m", 1536));
    }

    [Fact]
    public void Derive_IgnoresCosmeticBaseUrlDifferences()
    {
        // A trailing slash or a case change is not a different endpoint; treating it as one would
        // orphan every already-stored vector on a cosmetic config edit.
        var canonical = HostedEmbeddingFingerprint.Derive("ollama", "http://localhost:11434/v1", "m", 768);

        Assert.Equal(canonical, HostedEmbeddingFingerprint.Derive("ollama", "http://localhost:11434/v1/", "m", 768));
        Assert.Equal(canonical, HostedEmbeddingFingerprint.Derive("ollama", "HTTP://LOCALHOST:11434/V1", "m", 768));
        Assert.Equal(canonical, HostedEmbeddingFingerprint.Derive("OLLAMA", "http://localhost:11434/v1", "m", 768));
    }

    [Fact]
    public void Derive_TreatsComponentBoundariesAsSignificant()
    {
        // Without a separator, ("ab","c") and ("a","bc") would hash identically and two distinct
        // endpoints would silently share an identity.
        Assert.NotEqual(
            HostedEmbeddingFingerprint.Derive("ab", "c", "m", 768),
            HostedEmbeddingFingerprint.Derive("a", "bc", "m", 768));
    }

    [Fact]
    public void Derive_AcceptsAnAbsentBaseUrl()
    {
        var fingerprint = HostedEmbeddingFingerprint.Derive("openai", null, "m", 768);

        Assert.NotEmpty(fingerprint);
        Assert.Equal(fingerprint, HostedEmbeddingFingerprint.Derive("openai", "", "m", 768));
    }

    [Fact]
    public void Derive_RejectsBlankRequiredComponents()
    {
        Assert.Throws<ArgumentException>(() => HostedEmbeddingFingerprint.Derive("  ", "http://x", "m", 768));
        Assert.Throws<ArgumentException>(() => HostedEmbeddingFingerprint.Derive("openai", "http://x", "  ", 768));
    }
}
