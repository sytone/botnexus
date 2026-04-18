using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using FluentAssertions;
using Moq;

namespace BotNexus.Providers.Core.Tests;

public class LlmClientTests : IDisposable
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly ApiProviderRegistry _apiProviderRegistry = new();
    private readonly ModelRegistry _modelRegistry = new();
    private readonly LlmClient _llmClient;

    public LlmClientTests()
    {
        _llmClient = new LlmClient(_apiProviderRegistry, _modelRegistry);
    }

    public void Dispose()
    {
        _apiProviderRegistry.Clear();
        _modelRegistry.Clear();
    }

    private static LlmModel MakeModel(string api = "test-api") => new(
        Id: "test-model",
        Name: "Test",
        Api: api,
        Provider: "test",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 4096,
        MaxTokens: 1024);

    private static Context MakeContext() => new(
        SystemPrompt: "You are helpful",
        Messages: [new UserMessage(new UserMessageContent("hi"), Ts)]);

    [Fact]
    public void Stream_DelegatesToRegisteredProvider()
    {
        var model = MakeModel();
        var context = MakeContext();
        var expectedStream = new LlmStream();

        var mockProvider = new Mock<IApiProvider>();
        mockProvider.Setup(p => p.Api).Returns("test-api");
        mockProvider.Setup(p => p.Stream(model, context, null)).Returns(expectedStream);
        _apiProviderRegistry.Register(mockProvider.Object);

        var result = _llmClient.Stream(model, context);

        result.Should().BeSameAs(expectedStream);
    }

    [Fact]
    public void Stream_ThrowsWhenNoProviderRegistered()
    {
        var model = MakeModel("unregistered-api");
        var context = MakeContext();

        var act = () => _llmClient.Stream(model, context);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unregistered-api*");
    }

    [Fact]
    public async Task CompleteAsync_ReturnsResultFromStream()
    {
        var model = MakeModel();
        var context = MakeContext();
        var stream = new LlmStream();

        var finalMessage = new AssistantMessage(
            Content: [new TextContent("response")],
            Api: "test-api",
            Provider: "test",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: Ts);

        stream.Push(new DoneEvent(StopReason.Stop, finalMessage));
        stream.End(finalMessage);

        var mockProvider = new Mock<IApiProvider>();
        mockProvider.Setup(p => p.Api).Returns("test-api");
        mockProvider.Setup(p => p.Stream(It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<StreamOptions>())).Returns(stream);
        _apiProviderRegistry.Register(mockProvider.Object);

        var result = await _llmClient.CompleteAsync(model, context);

        result.StopReason.Should().Be(StopReason.Stop);
        result.Content.Should().ContainSingle();
    }
}
