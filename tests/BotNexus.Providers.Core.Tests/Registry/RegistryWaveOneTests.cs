using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using FluentAssertions;
using Moq;

namespace BotNexus.Providers.Core.Tests.Registry;

public sealed class RegistryWaveOneTests
{
    private static LlmModel MakeModel(string id = "test-model", string api = "test-api", string provider = "test-provider") => new(
        Id: id,
        Name: id,
        Api: api,
        Provider: provider,
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 32000,
        MaxTokens: 4096);

    private static Context MakeContext() => new(
        SystemPrompt: "You are helpful",
        Messages: [new UserMessage(new UserMessageContent("hello"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

    [Fact]
    public void ApiProviderRegistry_RegisterProvider_ResolvesByApiName()
    {
        var registry = new ApiProviderRegistry();
        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns("wave-api");

        registry.Register(provider.Object);
        var resolved = registry.Get("wave-api");

        resolved.Should().NotBeNull();
        resolved!.Api.Should().Be("wave-api");
    }

    [Fact]
    public void ApiProviderRegistry_GetUnknownApi_ReturnsNull()
    {
        var registry = new ApiProviderRegistry();

        var resolved = registry.Get("unknown-api");

        resolved.Should().BeNull();
    }

    [Fact]
    public void ApiProviderRegistry_TwoInstances_DoNotShareState()
    {
        var first = new ApiProviderRegistry();
        var second = new ApiProviderRegistry();
        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns("isolated-api");

        first.Register(provider.Object);
        var resolved = second.Get("isolated-api");

        resolved.Should().BeNull();
    }

    [Fact]
    public void ModelRegistry_RegisterModel_ResolvesById()
    {
        var registry = new ModelRegistry();
        var model = MakeModel(id: "model-1", provider: "provider-1");

        registry.Register("provider-1", model);
        var resolved = registry.GetModel("provider-1", "model-1");

        resolved.Should().BeEquivalentTo(model);
    }

    [Fact]
    public void ModelRegistry_GetModels_ReturnsRegisteredModels()
    {
        var registry = new ModelRegistry();
        registry.Register("provider-a", MakeModel(id: "a"));
        registry.Register("provider-a", MakeModel(id: "b"));

        var models = registry.GetModels("provider-a");

        models.Should().HaveCount(2);
    }

    [Fact]
    public void ModelRegistry_RegisterBuiltIns_RegistersCopilotModels()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var model = registry.GetModel("github-copilot", "claude-sonnet-4.6");

        model.Should().NotBeNull();
    }

    [Fact]
    public void ModelRegistry_TwoInstances_DoNotShareState()
    {
        var first = new ModelRegistry();
        var second = new ModelRegistry();
        first.Register("provider-a", MakeModel(id: "shared-model"));

        var resolved = second.GetModel("provider-a", "shared-model");

        resolved.Should().BeNull();
    }

    [Fact]
    public void LlmClient_UsesInjectedApiProviderRegistry()
    {
        var apiRegistry = new ApiProviderRegistry();
        var modelRegistry = new ModelRegistry();
        var client = new LlmClient(apiRegistry, modelRegistry);
        var model = MakeModel();
        var context = MakeContext();
        var stream = new LlmStream();
        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns("test-api");
        provider.Setup(item => item.Stream(model, context, null)).Returns(stream);
        apiRegistry.Register(provider.Object);

        var result = client.Stream(model, context);

        result.Should().BeSameAs(stream);
    }

    [Fact]
    public void LlmClient_UsesProvidedRegistryInstancesForResolution()
    {
        var populatedRegistry = new ApiProviderRegistry();
        var emptyRegistry = new ApiProviderRegistry();
        var modelRegistry = new ModelRegistry();
        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns("test-api");
        populatedRegistry.Register(provider.Object);
        var context = MakeContext();
        var model = MakeModel();
        var client = new LlmClient(emptyRegistry, modelRegistry);

        var act = () => client.Stream(model, context);

        act.Should().Throw<InvalidOperationException>();
    }
}
