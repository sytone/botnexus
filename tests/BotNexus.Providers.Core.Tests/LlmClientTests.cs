using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;
using FluentAssertions;
using Moq;

namespace BotNexus.Providers.Core.Tests;

public class LlmClientTests : IDisposable
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public LlmClientTests()
    {
        ApiProviderRegistry.Clear();
    }

    public void Dispose()
    {
        ApiProviderRegistry.Clear();
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
        ApiProviderRegistry.Register(mockProvider.Object);

        var result = LlmClient.Stream(model, context);

        result.Should().BeSameAs(expectedStream);
    }

    [Fact]
    public void Stream_ThrowsWhenNoProviderRegistered()
    {
        var model = MakeModel("unregistered-api");
        var context = MakeContext();

        var act = () => LlmClient.Stream(model, context);

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
        ApiProviderRegistry.Register(mockProvider.Object);

        var result = await LlmClient.CompleteAsync(model, context);

        result.StopReason.Should().Be(StopReason.Stop);
        result.Content.Should().ContainSingle();
    }
}
