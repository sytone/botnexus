using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Moq;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

public class ApiProviderRegistryTests : IDisposable
{
    private readonly ApiProviderRegistry _registry = new();

    public ApiProviderRegistryTests()
    {
        _registry.Clear();
    }

    public void Dispose()
    {
        _registry.Clear();
    }

    private static Mock<IApiProvider> CreateMockProvider(string api)
    {
        var mock = new Mock<IApiProvider>();
        mock.Setup(p => p.Api).Returns(api);
        return mock;
    }

    [Fact]
    public void Register_AndRetrieve_ReturnsProvider()
    {
        var mock = CreateMockProvider("test-api");
        _registry.Register(mock.Object);

        var result = _registry.Get("test-api");

        result.ShouldNotBeNull();
        result!.Api.ShouldBe("test-api");
    }

    [Fact]
    public void Get_UnregisteredApi_ReturnsNull()
    {
        var result = _registry.Get("nonexistent");

        result.ShouldBeNull();
    }

    [Fact]
    public void Register_WithSourceId_UnregisterBySourceId()
    {
        var mock = CreateMockProvider("test-api");
        _registry.Register(mock.Object, "source-1");

        _registry.Unregister("source-1");

        _registry.Get("test-api").ShouldBeNull();
    }

    [Fact]
    public void GetAll_ReturnsAllRegistered()
    {
        var mock1 = CreateMockProvider("api-1");
        var mock2 = CreateMockProvider("api-2");
        _registry.Register(mock1.Object);
        _registry.Register(mock2.Object);

        var all = _registry.GetAll();

        all.Count().ShouldBe(2);
    }

    [Fact]
    public void Clear_RemovesAllProviders()
    {
        var mock = CreateMockProvider("test-api");
        _registry.Register(mock.Object);

        _registry.Clear();

        _registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public void Register_ReplaceExisting_ForSameApi()
    {
        var mock1 = CreateMockProvider("same-api");
        var mock2 = CreateMockProvider("same-api");
        _registry.Register(mock1.Object);
        _registry.Register(mock2.Object);

        var result = _registry.Get("same-api");

        result.ShouldNotBeNull();
        result!.Api.ShouldBe("same-api");
    }

    [Fact]
    public void Stream_WithMismatchedModelApi_Throws()
    {
        var expectedStream = new LlmStream();
        var mock = CreateMockProvider("anthropic-messages");
        mock.Setup(p => p.Stream(It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<StreamOptions?>()))
            .Returns(expectedStream);
        _registry.Register(mock.Object);

        var provider = _registry.Get("anthropic-messages");
        provider.ShouldNotBeNull();
        var model = MakeModel("openai-completions");
        var context = new Context(null, []);

        var act = () => provider!.Stream(model, context);

        act.ShouldThrow<InvalidOperationException>()
            .Message.ShouldBe("Mismatched api: openai-completions expected anthropic-messages");
    }

    [Fact]
    public void StreamSimple_WithMismatchedModelApi_Throws()
    {
        var expectedStream = new LlmStream();
        var mock = CreateMockProvider("openai-completions");
        mock.Setup(p => p.StreamSimple(It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()))
            .Returns(expectedStream);
        _registry.Register(mock.Object);

        var provider = _registry.Get("openai-completions");
        provider.ShouldNotBeNull();
        var model = MakeModel("anthropic-messages");
        var context = new Context(null, []);

        var act = () => provider!.StreamSimple(model, context);

        act.ShouldThrow<InvalidOperationException>()
            .Message.ShouldBe("Mismatched api: anthropic-messages expected openai-completions");
    }

    private static LlmModel MakeModel(string api) => new(
        Id: "test-model",
        Name: "Test Model",
        Api: api,
        Provider: "test",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 8192,
        MaxTokens: 2048
    );
}
