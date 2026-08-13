using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// Issue #2432: <see cref="LlmClient.GetCapabilities"/> is the seam the agent loop reads to decide
/// whether a quirk workaround applies. It resolves the capabilities of the provider that will
/// actually serve a given model, so the loop never has to know which provider that is.
/// </summary>
public sealed class LlmClientCapabilitiesTests
{
    private sealed class DeclaringProvider(string api, ProviderCapabilities capabilities) : IApiProvider
    {
        public string Api => api;
        public ProviderCapabilities Capabilities => capabilities;
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null) => new();
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null) => new();
    }

    /// <summary>A provider that declares nothing, exercising the interface's default member.</summary>
    private sealed class SilentProvider(string api) : IApiProvider
    {
        public string Api => api;
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null) => new();
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null) => new();
    }

    private static LlmModel ModelFor(string api) => new(
        Id: "test-model",
        Name: "Test Model",
        Api: api,
        Provider: "test-provider",
        BaseUrl: "http://localhost",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 4096,
        MaxTokens: 1024);

    private static LlmClient ClientWith(IApiProvider provider)
    {
        var apiProviders = new ApiProviderRegistry();
        apiProviders.Register(provider);
        return new LlmClient(apiProviders, new ModelRegistry());
    }

    /// <summary>HAPPY PATH: the declared record reaches the caller through the model's api id.</summary>
    [Fact]
    public void GetCapabilities_ReturnsTheDeclarationOfTheServingProvider()
    {
        var declared = new ProviderCapabilities(
            RecoversLeakedToolCallMarkup: true,
            SystemPromptPlacement: SystemPromptPlacement.DedicatedField);
        var client = ClientWith(new DeclaringProvider("declaring-api", declared));

        var capabilities = client.GetCapabilities(ModelFor("declaring-api"));

        capabilities.RecoversLeakedToolCallMarkup.ShouldBeTrue();
        capabilities.SystemPromptPlacement.ShouldBe(SystemPromptPlacement.DedicatedField);
    }

    /// <summary>
    /// The registry wraps every registration in a guard proxy; the proxy must FORWARD the
    /// declaration rather than shadowing it with the interface default. Without this test the
    /// wrapper could silently flatten every provider's declaration back to Default and the loop
    /// would stop recovering for Copilot -- a regression invisible to any per-provider unit test.
    /// </summary>
    [Fact]
    public void GetCapabilities_IsForwardedThroughTheRegistryGuardProxy()
    {
        var declared = new ProviderCapabilities(RecoversLeakedToolCallMarkup: true);
        var registry = new ApiProviderRegistry();
        registry.Register(new DeclaringProvider("guarded-api", declared));

        registry.Get("guarded-api")!.Capabilities.RecoversLeakedToolCallMarkup.ShouldBeTrue();
    }

    /// <summary>
    /// A provider that declares nothing gets <see cref="ProviderCapabilities.Default"/> -- every
    /// quirk workaround OFF, via the interface's default member.
    /// </summary>
    [Fact]
    public void GetCapabilities_ProviderDeclaringNothing_GetsDefaultsWithQuirksOff()
    {
        var client = ClientWith(new SilentProvider("silent-api"));

        var capabilities = client.GetCapabilities(ModelFor("silent-api"));

        capabilities.RecoversLeakedToolCallMarkup.ShouldBeFalse();
        capabilities.SystemPromptPlacement.ShouldBe(SystemPromptPlacement.FirstMessage);
    }

    /// <summary>
    /// SAD PATH: a model naming an api with no registered provider yields the defaults instead of
    /// throwing. This read happens on the way into a turn; the subsequent stream call already
    /// throws a diagnostic naming the missing api, and a throw here would replace that good error
    /// with a worse one raised from a capability query.
    /// </summary>
    [Fact]
    public void GetCapabilities_UnregisteredApi_ReturnsDefaultsAndDoesNotThrow()
    {
        var client = new LlmClient(new ApiProviderRegistry(), new ModelRegistry());

        var capabilities = client.GetCapabilities(ModelFor("nobody-serves-this"));

        capabilities.ShouldBe(ProviderCapabilities.Default);
        capabilities.RecoversLeakedToolCallMarkup.ShouldBeFalse();
    }

    /// <summary>SAD PATH: a null model is a caller bug and is rejected outright.</summary>
    [Fact]
    public void GetCapabilities_NullModel_Throws()
    {
        var client = new LlmClient(new ApiProviderRegistry(), new ModelRegistry());

        Should.Throw<ArgumentNullException>(() => client.GetCapabilities(null!));
    }

    /// <summary>
    /// <see cref="ProviderCapabilities.Default"/> has every quirk workaround off. This is the
    /// direction of the default and the whole point of #2432: a provider that has never leaked
    /// tool-call markup does not pay for one that has.
    /// </summary>
    [Fact]
    public void Default_HasEveryQuirkWorkaroundOff()
    {
        ProviderCapabilities.Default.RecoversLeakedToolCallMarkup.ShouldBeFalse();
        ProviderCapabilities.Default.SystemPromptPlacement.ShouldBe(SystemPromptPlacement.FirstMessage);
    }
}
