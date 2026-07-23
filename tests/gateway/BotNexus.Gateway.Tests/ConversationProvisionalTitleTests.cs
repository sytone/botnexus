using System.Text.Json;
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

/// <summary>
/// #2126: provisional conversation titling from the first user message alone (before the assistant
/// turn completes) and the refine-once policy that lets the post-response path refine a provisional
/// title exactly once. Guards (custom title, non-human, already-titled) and client notification are
/// covered here at the service level; the GatewayHost wiring is covered by the gateway integration
/// tests in <c>GatewayHostTests</c>.
/// </summary>
public sealed class ConversationProvisionalTitleTests
{
    private static readonly AgentId AgentId = Domain.Primitives.AgentId.From("agent-a");
    private static readonly ConversationId ConvId = Domain.Primitives.ConversationId.From("conv-1");

    // ── ShouldTriggerProvisionalTitle guard ────────────────────────────────

    [Fact]
    public void ShouldTriggerProvisional_FirstUserNoAssistant_ReturnsUserText()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "Help me plan a trip to Kyoto" }
        };

        ConversationAutoTitleService.ShouldTriggerProvisionalTitle(history)
            .ShouldBe("Help me plan a trip to Kyoto");
    }

    [Fact]
    public void ShouldTriggerProvisional_AssistantAlreadyPresent_ReturnsNull()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "Q" },
            new() { Role = MessageRole.Assistant, Content = "A" }
        };

        ConversationAutoTitleService.ShouldTriggerProvisionalTitle(history).ShouldBeNull();
    }

    [Fact]
    public void ShouldTriggerProvisional_SecondUserTurn_ReturnsNull()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "Q1" },
            new() { Role = MessageRole.User, Content = "Q2" }
        };

        ConversationAutoTitleService.ShouldTriggerProvisionalTitle(history).ShouldBeNull();
    }

    [Fact]
    public void ShouldTriggerProvisional_EmptyHistory_ReturnsNull()
    {
        ConversationAutoTitleService.ShouldTriggerProvisionalTitle([]).ShouldBeNull();
    }

    [Fact]
    public void ShouldTriggerProvisional_AgentInitiatedAssistantFirst_ReturnsNull()
    {
        // An agent-initiated conversation stamps the first entry Assistant; provisional titling is
        // for human-first turns only, so this must not fire.
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.Assistant, Content = "Starting the nightly job." }
        };

        ConversationAutoTitleService.ShouldTriggerProvisionalTitle(history).ShouldBeNull();
    }

    [Fact]
    public void ShouldTriggerProvisional_BlankUserContent_ReturnsNull()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "   " }
        };

        ConversationAutoTitleService.ShouldTriggerProvisionalTitle(history).ShouldBeNull();
    }

    [Fact]
    public void BuildProvisionalPrompt_ContainsUserTextAndNoAssistant()
    {
        var prompt = ConversationAutoTitleService.BuildProvisionalPrompt("book me a flight");

        prompt.ShouldContain("book me a flight");
        prompt.ShouldContain("5 words or fewer");
        prompt.ShouldNotContain("Assistant:");
    }

    // ── IsProvisionalTitle ──────────────────────────────────────────────────

    [Fact]
    public void IsProvisionalTitle_NoFlag_ReturnsFalse()
    {
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId };
        ConversationAutoTitleService.IsProvisionalTitle(conv).ShouldBeFalse();
    }

    [Fact]
    public void IsProvisionalTitle_BoolFlag_ReturnsTrue()
    {
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId };
        conv.Metadata[ConversationAutoTitleService.ProvisionalTitleMetadataKey] = true;
        ConversationAutoTitleService.IsProvisionalTitle(conv).ShouldBeTrue();
    }

    [Fact]
    public void IsProvisionalTitle_JsonElementTrue_ReturnsTrue()
    {
        // Round-tripped through JSON persistence the flag comes back as a JsonElement, not a bool.
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId };
        using var doc = JsonDocument.Parse("true");
        conv.Metadata[ConversationAutoTitleService.ProvisionalTitleMetadataKey] = doc.RootElement.Clone();
        ConversationAutoTitleService.IsProvisionalTitle(conv).ShouldBeTrue();
    }

    [Fact]
    public void IsProvisionalTitle_JsonElementFalse_ReturnsFalse()
    {
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId };
        using var doc = JsonDocument.Parse("false");
        conv.Metadata[ConversationAutoTitleService.ProvisionalTitleMetadataKey] = doc.RootElement.Clone();
        ConversationAutoTitleService.IsProvisionalTitle(conv).ShouldBeFalse();
    }

    // ── GenerateProvisionalAndSaveAsync ─────────────────────────────────────

    [Fact]
    public async Task GenerateProvisional_DefaultTitle_SavesTitleWithProvisionalFlag()
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

        var svc = new ConversationAutoTitleService(store.Object, CreateFakeLlmClient("Kyoto Trip Plan"), NullLogger.Instance);

        var result = await svc.GenerateProvisionalAndSaveAsync(
            ConvId, AgentId, "Help me plan a trip to Kyoto", null, 30, CancellationToken.None);

        result.ShouldBe("Kyoto Trip Plan");
        store.Verify(s => s.SaveAsync(
            It.Is<Conversation>(c => c.Title == "Kyoto Trip Plan"
                && ConversationAutoTitleService.IsProvisionalTitle(c)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateProvisional_CustomTitle_NeverOverwritten()
    {
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = "Human Named This" };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);

        var svc = new ConversationAutoTitleService(store.Object, CreateFakeLlmClient("Generated"), NullLogger.Instance);

        var result = await svc.GenerateProvisionalAndSaveAsync(
            ConvId, AgentId, "user text", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateProvisional_PushesSignalRNotification()
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
            store.Object, CreateFakeLlmClient("Notify Me"), NullLogger.Instance, notifier.Object);

        await svc.GenerateProvisionalAndSaveAsync(ConvId, AgentId, "user", null, 30, CancellationToken.None);

        notifier.Verify(n => n.NotifyConversationChangedAsync("updated", AgentId.Value, ConvId.Value,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateProvisional_TitleChangedBeforeSecondRead_SkipsSave()
    {
        var conv1 = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = ConversationAutoTitleService.DefaultTitle };
        var conv2 = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = "Already Set" };
        var call = 0;
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++call == 1 ? conv1 : conv2);

        var svc = new ConversationAutoTitleService(store.Object, CreateFakeLlmClient("Race"), NullLogger.Instance);

        var result = await svc.GenerateProvisionalAndSaveAsync(ConvId, AgentId, "user", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateProvisional_ProviderFailure_ReturnsNull_NoSave_NoThrow()
    {
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = ConversationAutoTitleService.DefaultTitle
        };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);

        var svc = new ConversationAutoTitleService(store.Object, CreateThrowingLlmClient(), NullLogger.Instance);

        var result = await svc.GenerateProvisionalAndSaveAsync(ConvId, AgentId, "user", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Refine-once policy ──────────────────────────────────────────────────

    [Fact]
    public async Task Refine_ProvisionalTitle_IsOverwrittenAndFlagCleared()
    {
        // A provisional-flagged conversation is refined ONCE by the post-response path, replacing
        // the user-only provisional title with one that also considers the assistant response, and
        // clearing the provisional flag so subsequent turns hit the standard custom-title guard.
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = "Provisional Guess"
        };
        conv.Metadata[ConversationAutoTitleService.ProvisionalTitleMetadataKey] = true;

        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(store.Object, CreateFakeLlmClient("Refined Final Title"), NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "user question", "assistant answer", null, 30, CancellationToken.None);

        result.ShouldBe("Refined Final Title");
        store.Verify(s => s.SaveAsync(
            It.Is<Conversation>(c => c.Title == "Refined Final Title"
                && !ConversationAutoTitleService.IsProvisionalTitle(c)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refine_NonProvisionalCustomTitle_NeverOverwritten()
    {
        // A final (non-provisional) custom title must survive - the refine path only touches
        // default or provisional titles.
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = "Final Custom Title" };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);

        var svc = new ConversationAutoTitleService(store.Object, CreateFakeLlmClient("Should Not Win"), NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "user", "assistant", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refine_OnlyOnce_SecondPostResponsePassIsGuardedByFinalTitle()
    {
        // After the refine save clears the provisional flag, a later post-response pass sees a
        // final custom title and does not re-title (refine-once, not refine-every-turn).
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = "Provisional" };
        conv.Metadata[ConversationAutoTitleService.ProvisionalTitleMetadataKey] = true;

        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<Conversation, CancellationToken>((c, _) => { conv.Title = c.Title; });

        var svc = new ConversationAutoTitleService(store.Object, CreateFakeLlmClient("First Refine"), NullLogger.Instance);

        var first = await svc.GenerateAndSaveAsync(ConvId, AgentId, "u", "a", null, 30, CancellationToken.None);
        first.ShouldBe("First Refine");

        // Second pass: flag now cleared, title is final custom -> no re-title.
        var second = await svc.GenerateAndSaveAsync(ConvId, AgentId, "u", "a", null, 30, CancellationToken.None);
        second.ShouldBeNull();

        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static LlmClient CreateFakeLlmClient(string responseText)
    {
        var modelRegistry = new ModelRegistry();
        var fakeModel = new LlmModel(
            Id: "fake-model", Name: "fake-model", Api: "fake-api", Provider: "fake",
            BaseUrl: "https://fake.example.com", Reasoning: false, Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 4096, MaxTokens: 512);
        modelRegistry.Register("fake", fakeModel);
        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new FakeApiProvider(responseText));
        return new LlmClient(providerRegistry, modelRegistry);
    }

    private static LlmClient CreateThrowingLlmClient()
    {
        var modelRegistry = new ModelRegistry();
        var fakeModel = new LlmModel(
            Id: "fake-model", Name: "fake-model", Api: "fake-api", Provider: "fake",
            BaseUrl: "https://fake.example.com", Reasoning: false, Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 4096, MaxTokens: 512);
        modelRegistry.Register("fake", fakeModel);
        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new ThrowingApiProvider());
        return new LlmClient(providerRegistry, modelRegistry);
    }

    private sealed class FakeApiProvider : IApiProvider
    {
        private readonly string _responseText;
        public FakeApiProvider(string responseText) => _responseText = responseText;
        public string Api => "fake-api";
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => throw new NotImplementedException("FakeApiProvider only supports StreamSimple/CompleteSimple");
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var msg = new AssistantMessage(
                Content: [new TextContent(_responseText)], Api: "fake-api", Provider: "fake",
                ModelId: "fake-model", Usage: new Usage(), StopReason: StopReason.Stop,
                ErrorMessage: null, ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var stream = new LlmStream();
            stream.Push(new DoneEvent(StopReason.Stop, msg));
            return stream;
        }
    }

    private sealed class ThrowingApiProvider : IApiProvider
    {
        public string Api => "fake-api";
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => throw new NotImplementedException();
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
            => throw new InvalidOperationException("No API key for provider (simulated auth failure).");
    }
}
