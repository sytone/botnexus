using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ConversationAutoTitleServiceTests
{
    private static readonly AgentId AgentId = Domain.Primitives.AgentId.From("agent-a");
    private static readonly ConversationId ConvId = Domain.Primitives.ConversationId.From("conv-1");

    // -----------------------------------------------------------------------
    // Unit helpers for static methods
    // -----------------------------------------------------------------------

    [Fact]
    public void IsDefaultTitle_NullOrEmpty_ReturnsTrue()
    {
        ConversationAutoTitleService.IsDefaultTitle(null).ShouldBeTrue();
        ConversationAutoTitleService.IsDefaultTitle("").ShouldBeTrue();
        ConversationAutoTitleService.IsDefaultTitle("   ").ShouldBeTrue();
    }

    [Fact]
    public void IsDefaultTitle_DefaultValue_ReturnsTrue()
    {
        ConversationAutoTitleService.IsDefaultTitle("New conversation").ShouldBeTrue();
        ConversationAutoTitleService.IsDefaultTitle("new conversation").ShouldBeTrue(); // case-insensitive
    }

    [Fact]
    public void IsDefaultTitle_CustomTitle_ReturnsFalse()
    {
        ConversationAutoTitleService.IsDefaultTitle("My custom title").ShouldBeFalse();
        ConversationAutoTitleService.IsDefaultTitle("Discussion about something").ShouldBeFalse();
    }

    [Fact]
    public void BuildPrompt_ContainsUserAndAssistantText()
    {
        var prompt = ConversationAutoTitleService.BuildPrompt("hello world", "hi there");

        prompt.ShouldContain("hello world");
        prompt.ShouldContain("hi there");
        prompt.ShouldContain("5 words or fewer");
    }

    [Theory]
    [InlineData("\"Hello World\"", "Hello World")]
    [InlineData("'Chat About Cats'", "Chat About Cats")]
    [InlineData("  Some Title  ", "Some Title")]
    public void SanitizeTitle_RemovesQuotesAndTrims(string raw, string expected)
    {
        ConversationAutoTitleService.SanitizeTitle(raw).ShouldBe(expected);
    }

    [Fact]
    public void SanitizeTitle_LongTitle_TruncatesAt80()
    {
        var longTitle = new string('A', 100);
        var result = ConversationAutoTitleService.SanitizeTitle(longTitle);
        result.Length.ShouldBeLessThanOrEqualTo(80);
    }

    // -----------------------------------------------------------------------
    // GenerateAndSaveAsync -- integration tests with mocks
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GenerateAndSaveAsync_CustomTitle_SkipsGeneration()
    {
        var store = new Mock<IConversationStore>();
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = "My Custom Title"
        };
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Generated Title"),
            NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "user text", "assistant text", null, CancellationToken.None);

        result.ShouldBeNull();
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAndSaveAsync_NullConversation_ReturnsNull()
    {
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync((Conversation?)null);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Generated Title"),
            NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "user", "assistant", null, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateAndSaveAsync_DefaultTitle_GeneratesAndSaves()
    {
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = ConversationAutoTitleService.DefaultTitle
        };
        var store = new Mock<IConversationStore>();
        // Return same conversation on both GetAsync calls.
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Chat About Cats"),
            NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "What do cats eat?", "Cats eat...", null, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldBe("Chat About Cats");
        store.Verify(s => s.SaveAsync(It.Is<Conversation>(c => c.Title == "Chat About Cats"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAndSaveAsync_PushesSignalRNotification()
    {
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = ConversationAutoTitleService.DefaultTitle
        };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var notifier = new Mock<IConversationChangeNotifier>();
        notifier.Setup(n => n.NotifyConversationChangedAsync("updated", AgentId.Value, ConvId.Value, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Notification Test"),
            NullLogger.Instance,
            notifier.Object);

        await svc.GenerateAndSaveAsync(ConvId, AgentId, "user", "assistant", null, CancellationToken.None);

        notifier.Verify(n => n.NotifyConversationChangedAsync("updated", AgentId.Value, ConvId.Value,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAndSaveAsync_TitleChangedBeforeSecondRead_SkipsSave()
    {
        // First read: default title. Second read (guard): custom title already set by another path.
        var conv1 = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = ConversationAutoTitleService.DefaultTitle };
        var conv2 = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = "Already Set" };

        var call = 0;
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++call == 1 ? conv1 : conv2);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Race Condition Title"),
            NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(ConvId, AgentId, "user", "assistant", null, CancellationToken.None);

        result.ShouldBeNull();
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a minimal LlmClient backed by a fake provider that returns the given title text.
    /// </summary>
    private static LlmClient CreateFakeLlmClient(string responseText)
    {
        var modelRegistry = new ModelRegistry();
        var fakeModel = new LlmModel(
            Id: "fake-model",
            Name: "fake-model",
            Api: "fake-api",
            Provider: "fake",
            BaseUrl: "https://fake.example.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 4096,
            MaxTokens: 512);

        modelRegistry.Register("fake", fakeModel);

        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new FakeApiProvider(responseText));

        return new LlmClient(providerRegistry, modelRegistry);
    }

    private sealed class FakeApiProvider : IApiProvider
    {
        private readonly string _responseText;

        public FakeApiProvider(string responseText) => _responseText = responseText;

        public string Api => "fake-api";

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        {
            throw new NotImplementedException("FakeApiProvider only supports StreamSimple/CompleteSimple");
        }

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var msg = new AssistantMessage(
                Content: [new TextContent(_responseText)],
                Api: "fake-api",
                Provider: "fake",
                ModelId: "fake-model",
                Usage: new Usage(),
                StopReason: StopReason.Stop,
                ErrorMessage: null,
                ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var stream = new LlmStream();
            stream.Push(new DoneEvent(StopReason.Stop, msg));
            return stream;
        }
    }
}
