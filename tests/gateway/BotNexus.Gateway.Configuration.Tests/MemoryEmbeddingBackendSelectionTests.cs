using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Backend selection resolution for the memory embedding ladder (#2790, criteria 1 and 2).
/// </summary>
/// <remarks>
/// These tests own the CONFIGURATION half of the ladder: which token maps to which backend, what a
/// typo does, and how a pre-#2790 configuration that only ever knew <c>enabled</c> is interpreted.
/// The composition half - what each resolved backend actually constructs - is proved in
/// <c>MemoryEmbeddingCompositionTests</c>, where the provider stack is in scope.
/// </remarks>
public sealed class MemoryEmbeddingBackendSelectionTests
{
    private static MemoryEmbeddingsConfig Complete(string? backend, bool enabled = false) => new()
    {
        Backend = backend,
        Enabled = enabled,
        Provider = "ollama",
        Model = "nomic-embed-text",
        BaseUrl = "http://localhost:11434/v1",
        Dimensions = 3,
    };

    // ---- AC1: the three documented values all resolve ----

    [Theory]
    [InlineData("none", MemoryEmbeddingBackend.None)]
    [InlineData("local", MemoryEmbeddingBackend.Local)]
    [InlineData("provider", MemoryEmbeddingBackend.Provider)]
    public void ResolveBackend_ResolvesEachDocumentedValue(string token, MemoryEmbeddingBackend expected)
    {
        Complete(token).ResolveBackend(out var unrecognized).ShouldBe(expected);
        unrecognized.ShouldBeNull();
    }

    [Theory]
    [InlineData("  PROVIDER  ")]
    [InlineData("Provider")]
    [InlineData("hosted")]
    public void ResolveBackend_IsCaseAndWhitespaceInsensitive(string token)
    {
        // An operator should not lose vector retrieval to a stray space or a capital letter.
        Complete(token).ResolveBackend().ShouldBe(MemoryEmbeddingBackend.Provider);
    }

    [Fact]
    public void ResolveBackend_TreatsOnnxAsTheLocalBackend()
    {
        Complete("onnx").ResolveBackend().ShouldBe(MemoryEmbeddingBackend.Local);
    }

    // ---- AC1: the shipped default is 'none' and a single key overrides it ----

    [Fact]
    public void ResolveBackend_DefaultsToNone_WhenNothingIsConfigured()
    {
        new MemoryEmbeddingsConfig().ResolveBackend(out var unrecognized).ShouldBe(MemoryEmbeddingBackend.None);
        unrecognized.ShouldBeNull();
    }

    [Fact]
    public void ResolveBackend_IsOverriddenByTheBackendKeyAlone()
    {
        // The whole point of the discriminator: ONE key moves an operator off the default, without
        // touching 'enabled'. If this fails, the default is not genuinely overridable.
        var config = Complete("provider", enabled: false);
        config.Enabled.ShouldBeFalse();
        config.ResolveBackend().ShouldBe(MemoryEmbeddingBackend.Provider);
        config.IsComplete().ShouldBeTrue();
    }

    [Fact]
    public void ResolveBackend_ExplicitNoneOutranksTheLegacyEnabledToggle()
    {
        // Explicit beats implicit in both directions, otherwise 'backend: none' would be advisory.
        Complete("none", enabled: true).ResolveBackend().ShouldBe(MemoryEmbeddingBackend.None);
    }

    // ---- Backward compatibility with the pre-#2790 shape ----

    [Fact]
    public void ResolveBackend_FallsBackToTheLegacyEnabledToggle_WhenBackendIsUnspecified()
    {
        // A configuration written before the discriminator existed must keep working unchanged.
        Complete(backend: null, enabled: true).ResolveBackend().ShouldBe(MemoryEmbeddingBackend.Provider);
        Complete(backend: null, enabled: false).ResolveBackend().ShouldBe(MemoryEmbeddingBackend.None);
        Complete(backend: "   ", enabled: true).ResolveBackend().ShouldBe(MemoryEmbeddingBackend.Provider);
    }

    // ---- Sad path: an unrecognised token is named, not swallowed ----

    [Fact]
    public void ResolveBackend_ReportsAnUnrecognisedToken_AndDegradesToNone()
    {
        var resolved = Complete("aws-bedrock", enabled: true).ResolveBackend(out var unrecognized);

        resolved.ShouldBe(MemoryEmbeddingBackend.None);
        unrecognized.ShouldBe("aws-bedrock", "the offending token must be surfaced so it can be named in the warning");
    }

    [Fact]
    public void ResolveBackend_DoesNotFallBackToEnabled_WhenTheTokenIsUnrecognised()
    {
        // Falling back would turn a typo into a silently different backend, which is worse than
        // degrading: the operator asked for something specific and did not get it.
        Complete("privider", enabled: true).ResolveBackend().ShouldBe(MemoryEmbeddingBackend.None);
    }

    // ---- IsComplete is scoped to the provider backend ----

    [Fact]
    public void IsComplete_IsFalseForBackendsThatNeedNoEndpointConfiguration()
    {
        Complete("none").IsComplete().ShouldBeFalse();
        Complete("local").IsComplete().ShouldBeFalse("the local backend does not consume the hosted endpoint fields");
    }

    [Theory]
    [InlineData(null, "model", "http://x", 3)]
    [InlineData("ollama", null, "http://x", 3)]
    [InlineData("ollama", "model", null, 3)]
    [InlineData("ollama", "model", "http://x", 0)]
    public void IsComplete_IsFalseWhenTheProviderBackendIsHalfConfigured(
        string? provider, string? model, string? baseUrl, int dimensions)
    {
        new MemoryEmbeddingsConfig
        {
            Backend = "provider",
            Provider = provider,
            Model = model,
            BaseUrl = baseUrl,
            Dimensions = dimensions,
        }.IsComplete().ShouldBeFalse();
    }

    // ---- Parser-level sad paths ----

    [Fact]
    public void TryParse_RejectsBlankAndUnknownTokens()
    {
        MemoryEmbeddingBackendParser.TryParse(null, out _).ShouldBeFalse();
        MemoryEmbeddingBackendParser.TryParse("   ", out _).ShouldBeFalse();
        MemoryEmbeddingBackendParser.TryParse("nonsense", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_ReportsNoneForARejectedToken()
    {
        // The out value must be safe to use even on the false path: callers that ignore the bool
        // must not end up with a backend the operator never asked for.
        MemoryEmbeddingBackendParser.TryParse("nonsense", out var backend).ShouldBeFalse();
        backend.ShouldBe(MemoryEmbeddingBackend.None);
    }
}
