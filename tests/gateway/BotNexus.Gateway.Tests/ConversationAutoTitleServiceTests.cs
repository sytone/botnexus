using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.IO.Abstractions.TestingHelpers;

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

    [Fact]
    public void BuildPrompt_NullUserText_UsesAssistantOnlyPrompt()
    {
        // #1903: agent-initiated conversations have no user turn, so BuildPrompt must fall back to
        // an assistant-only titling prompt seeded from the assistant response.
        var prompt = ConversationAutoTitleService.BuildPrompt(null, "The nightly backup completed.");

        prompt.ShouldContain("The nightly backup completed.");
        prompt.ShouldContain("assistant response");
        prompt.ShouldNotContain("User:");
        prompt.ShouldContain("5 words or fewer");
    }

    [Fact]
    public void BuildPrompt_BlankUserText_UsesAssistantOnlyPrompt()
    {
        var prompt = ConversationAutoTitleService.BuildPrompt("   ", "Assistant kickoff content.");

        prompt.ShouldContain("Assistant kickoff content.");
        prompt.ShouldNotContain("User:");
    }

    [Theory]
    [InlineData("\"Hello World\"", "Hello World")]
    [InlineData("'Chat About Cats'", "Chat About Cats")]
    [InlineData("  Some Title  ", "Some Title")]
    [InlineData("Containerized\nMarkdown  Journal\tNexus", "Containerized Markdown Journal Nexus")]
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
    // #1994: ExtractTitleText — deterministic coverage of the text/thinking fallback seam.
    // The real-LLM E2E suite uses github-models gpt-4o-mini, which does not emit ThinkingContent,
    // so the reasoning-only extraction path is covered here at the unit level rather than via a
    // real round-trip.
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractTitleText_TextContentOnly_ReturnsText()
    {
        var completion = MakeCompletion(new TextContent("Plain Text Title"));
        ConversationAutoTitleService.ExtractTitleText(completion).ShouldBe("Plain Text Title");
    }

    [Fact]
    public void ExtractTitleText_StreamedTextFragments_ConcatenatesWithoutInventingSpaces()
    {
        var completion = MakeCompletion(
            new TextContent("Container"),
            new TextContent("ized"),
            new TextContent("\nMarkdown"),
            new TextContent(" Journal"),
            new TextContent(" Nexus"));

        ConversationAutoTitleService.ExtractTitleText(completion)
            .ShouldBe("Containerized\nMarkdown Journal Nexus");
    }

    [Fact]
    public void ExtractTitleText_StreamedThinkingFragments_ConcatenatesWithoutInventingSpaces()
    {
        var completion = MakeCompletion(
            new ThinkingContent("Container"),
            new ThinkingContent("ized\nMarkdown"),
            new ThinkingContent(" Journal Nexus"));

        ConversationAutoTitleService.ExtractTitleText(completion)
            .ShouldBe("Containerized\nMarkdown Journal Nexus");
    }

    [Fact]
    public void ExtractTitleText_ThinkingOnly_FallsBackToThinking()
    {
        var completion = MakeCompletion(new ThinkingContent("Thinking Block Title"));
        ConversationAutoTitleService.ExtractTitleText(completion).ShouldBe("Thinking Block Title");
    }

    [Fact]
    public void ExtractTitleText_TextAndThinking_PrefersText()
    {
        var completion = MakeCompletion(new ThinkingContent("reasoning trace"), new TextContent("Real Title"));
        ConversationAutoTitleService.ExtractTitleText(completion).ShouldBe("Real Title");
    }

    [Fact]
    public void ExtractTitleText_NeitherTextNorThinking_ReturnsEmpty()
    {
        var completion = MakeCompletion(new ToolCallContent("id", "noop", new Dictionary<string, object?>()));
        ConversationAutoTitleService.ExtractTitleText(completion).ShouldBeNullOrEmpty();
    }

    private static AssistantMessage MakeCompletion(params ContentBlock[] content)
        => new(
            Content: content,
            Api: "fake-api",
            Provider: "fake",
            ModelId: "fake-model",
            Usage: new Usage(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

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
            ConvId, AgentId, "user text", "assistant text", null, 30, CancellationToken.None);

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
            ConvId, AgentId, "user", "assistant", null, 30, CancellationToken.None);

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
            ConvId, AgentId, "What do cats eat?", "Cats eat...", null, 30, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldBe("Chat About Cats");
        store.Verify(s => s.SaveAsync(It.Is<Conversation>(c => c.Title == "Chat About Cats"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAndSaveAsync_AgentInitiated_NullUserText_GeneratesAndSaves()
    {
        // #1903: agent-initiated conversation (user=0, assistant>=1) titles via the assistant-only
        // prompt. A null userText must not throw and must still produce and persist a title.
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = ConversationAutoTitleService.DefaultTitle
        };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Nightly Backup Job"),
            NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, null, "Starting the nightly backup job now.", null, 30, CancellationToken.None);

        result.ShouldBe("Nightly Backup Job");
        store.Verify(s => s.SaveAsync(It.Is<Conversation>(c => c.Title == "Nightly Backup Job"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAndSaveAsync_AgentInitiated_CustomTitle_NeverOverwritten()
    {
        // The IsDefaultTitle re-guard must still protect a custom title even on the agent-initiated
        // (null userText) path.
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = "Human Named This"
        };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Generated Title"),
            NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, null, "Agent opening message.", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
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

        await svc.GenerateAndSaveAsync(ConvId, AgentId, "user", "assistant", null, 30, CancellationToken.None);

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

        var result = await svc.GenerateAndSaveAsync(ConvId, AgentId, "user", "assistant", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -----------------------------------------------------------------------
    // #1994: robust title extraction. A reasoning model returns its answer in a
    // ThinkingContent block, not a TextContent, so OfType<TextContent>() yields nothing and the
    // title sanitises to empty (the live rawLength=0 no-persist bug). Extraction must fall back to
    // thinking content so a reasoning model (or the first-available fallback engaging on a
    // deployment where the preferred titling model is absent) never yields an empty title.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GenerateAndSaveAsync_ThinkingOnlyCompletion_ExtractsTitleFromThinking()
    {
        // RED until extraction falls back to ThinkingContent: a reasoning model returns the title
        // in a thinking block with zero TextContent, which is the exact live rawLength=0 failure.
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = ConversationAutoTitleService.DefaultTitle
        };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateContentLlmClient([new ThinkingContent("Reasoning Model Title")]),
            NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "What do cats eat?", "Cats eat...", null, 30, CancellationToken.None);

        result.ShouldBe("Reasoning Model Title");
        store.Verify(s => s.SaveAsync(It.Is<Conversation>(c => c.Title == "Reasoning Model Title"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAndSaveAsync_TextAndThinkingPresent_PrefersTextContent()
    {
        // When both are present, real answer text (TextContent) must win over the thinking trace.
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = ConversationAutoTitleService.DefaultTitle
        };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateContentLlmClient([
                new ThinkingContent("Let me think about a good title"),
                new TextContent("Actual Answer Title")
            ]),
            NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "q", "a", null, 30, CancellationToken.None);

        result.ShouldBe("Actual Answer Title");
    }

    [Fact]
    public async Task GenerateAndSaveAsync_NoTextOrThinkingContent_LogsEmptyTitle()
    {
        // A completion with neither text nor thinking (e.g. only a tool-call block) is genuinely
        // empty and must still hit the observable #1979 empty-title no-persist log, not throw.
        var logger = new CapturingLogger();
        var conv = new Conversation
        {
            ConversationId = ConvId,
            AgentId = AgentId,
            Title = ConversationAutoTitleService.DefaultTitle
        };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateContentLlmClient([new ToolCallContent("id-1", "noop", new Dictionary<string, object?>())]),
            logger);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "q", "a", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        logger.Messages.ShouldContain(m => m.Contains("empty title after sanitisation"));
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -----------------------------------------------------------------------
    // #1979: the two silent no-persist guards must emit an observable INFO log so a
    // "fires but never titles" symptom is diagnosable (null store-read vs title-comparison miss).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GenerateAndSaveAsync_NullConversation_LogsInitialGuard()
    {
        var logger = new CapturingLogger();
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync((Conversation?)null);
        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Generated Title"),
            logger);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "user", "assistant", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        logger.Messages.ShouldContain(m =>
            m.Contains("not persisting") && m.Contains("initial guard") && m.Contains("conversationLoaded=False"));
    }

    [Fact]
    public async Task GenerateAndSaveAsync_NonDefaultTitle_LogsInitialGuardWithObservedTitle()
    {
        var logger = new CapturingLogger();
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = "My Custom Title" };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Generated Title"),
            logger);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "user", "assistant", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        // The observed non-default title must be surfaced so a comparison miss is visible.
        logger.Messages.ShouldContain(m =>
            m.Contains("not persisting") && m.Contains("initial guard") &&
            m.Contains("conversationLoaded=True") && m.Contains("My Custom Title"));
    }

    [Fact]
    public async Task GenerateAndSaveAsync_TitleChangedBeforeSecondRead_LogsReReadGuard()
    {
        var logger = new CapturingLogger();
        var conv1 = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = ConversationAutoTitleService.DefaultTitle };
        var conv2 = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = "Already Set" };
        var call = 0;
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++call == 1 ? conv1 : conv2);
        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Race Condition Title"),
            logger);

        var result = await svc.GenerateAndSaveAsync(ConvId, AgentId, "user", "assistant", null, 30, CancellationToken.None);

        result.ShouldBeNull();
        logger.Messages.ShouldContain(m =>
            m.Contains("not persisting") && m.Contains("re-read guard") && m.Contains("Already Set"));
    }

    /// <summary>
    /// Minimal <see cref="ILogger"/> that records formatted log messages for assertion. The
    /// Gateway.Tests project has no shared capturing logger, so this lives with the tests that
    /// need to assert #1979's diagnostic output.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    // -----------------------------------------------------------------------
    // Endpoint-resolution regression guard (migrated #1636 -> #1639)
    //
    // Originally the auto-title call had to re-apply the per-provider API-endpoint override
    // (enterprise vs individual GitHub Copilot) to the resolved titling model, because the model was
    // registered with the static individual BaseUrl. After #1639 the model is BORN with the correct
    // host (resolved at registration), so the service applies no override. These guards prove the
    // end-to-end property that still matters: the model that actually reaches the provider carries
    // the host it was registered with - now purely by construction, with NO consumer-side
    // `with { BaseUrl }` patch in the titling path.
    // -----------------------------------------------------------------------

    // A copilot-shaped model registered ALREADY carrying the enterprise BaseUrl, exactly as
    // BuiltInModels/discovery would produce it on an enterprise account after #1639.
    private const string EnterpriseEndpoint = "https://api.enterprise.githubcopilot.com";
    private const string IndividualEndpoint = "https://api.individual.githubcopilot.com";

    private static readonly LlmModel EnterpriseModel = new(
        Id: "title-model", Name: "Title", Api: "capture-api", Provider: "github-copilot",
        BaseUrl: EnterpriseEndpoint, Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 4096, MaxTokens: 512);

    private static readonly LlmModel IndividualModel = EnterpriseModel with { BaseUrl = IndividualEndpoint };

    [Fact]
    public async Task GenerateAndSaveAsync_ModelReachingProvider_CarriesEnterpriseHost_ByConstruction()
    {
        LlmModel? modelSeenByProvider = null;
        var llmClient = CreateCapturingLlmClient("Chat About Cats", EnterpriseModel, m => modelSeenByProvider = m);
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = ConversationAutoTitleService.DefaultTitle };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(store.Object, llmClient, NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "What do cats eat?", "Cats eat...", null, 30, CancellationToken.None);

        result.ShouldBe("Chat About Cats");
        modelSeenByProvider.ShouldNotBeNull();
        // Enterprise host reaches the wire purely because the model was registered that way.
        modelSeenByProvider!.BaseUrl.ShouldBe(EnterpriseEndpoint);
    }

    [Fact]
    public async Task GenerateAndSaveAsync_IndividualModel_ReachesProviderWithIndividualHost()
    {
        LlmModel? modelSeenByProvider = null;
        var llmClient = CreateCapturingLlmClient("Some Title", IndividualModel, m => modelSeenByProvider = m);
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = ConversationAutoTitleService.DefaultTitle };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(store.Object, llmClient, NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "user", "assistant", null, 30, CancellationToken.None);

        result.ShouldBe("Some Title");
        modelSeenByProvider.ShouldNotBeNull();
        modelSeenByProvider!.BaseUrl.ShouldBe(IndividualEndpoint); // unchanged - no override applied
    }

    // -----------------------------------------------------------------------
    // #2025: auto-title must resolve credentials through the SAME seam every other
    // LLM call uses (GatewayAuthManager), not roll its own. Before this fix the
    // service discarded the injected auth manager (`_ = authManager;`) and called
    // the provider with no options, so OAuth providers (github-copilot) fell to the
    // env-key fallback and threw "No API key for github-copilot".
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GenerateAndSaveAsync_ResolvesApiKey_ThroughGatewayAuthManager_ThreadsIntoOptions()
    {
        SimpleStreamOptions? optionsSeenByProvider = null;
        var llmClient = CreateCapturingLlmClient(
            "Chat About Cats", EnterpriseModel, _ => { }, o => optionsSeenByProvider = o);
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = ConversationAutoTitleService.DefaultTitle };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // A real GatewayAuthManager backed by an auth.json OAuth entry for github-copilot.
        var authManager = CreateAuthManagerWithCopilotToken("copilot-oauth-access-token");
        var svc = new ConversationAutoTitleService(store.Object, llmClient, NullLogger.Instance, notifier: null, authManager: authManager);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "What do cats eat?", "Cats eat...", null, 30, CancellationToken.None);

        result.ShouldBe("Chat About Cats");
        optionsSeenByProvider.ShouldNotBeNull();
        // The resolved OAuth token must be threaded into the provider options, exactly as the
        // foreground agent loop and the compactor do.
        optionsSeenByProvider!.ApiKey.ShouldBe("copilot-oauth-access-token");
    }

    [Fact]
    public async Task GenerateAndSaveAsync_NullAuthManager_DegradesGracefully_NullApiKey()
    {
        // Behaviour-preserving: with no auth manager, a null ApiKey is threaded and the provider
        // falls back to environment keys (exactly as passing null options did before #2025).
        SimpleStreamOptions? optionsSeenByProvider = null;
        var llmClient = CreateCapturingLlmClient(
            "Some Title", EnterpriseModel, _ => { }, o => optionsSeenByProvider = o);
        var conv = new Conversation { ConversationId = ConvId, AgentId = AgentId, Title = ConversationAutoTitleService.DefaultTitle };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConvId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(store.Object, llmClient, NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "user", "assistant", null, 30, CancellationToken.None);

        result.ShouldBe("Some Title");
        // With no auth manager, titling passes null options through (exactly as before #2025), so
        // the provider applies its own environment-key fallback. Null options = behaviour-preserving.
        optionsSeenByProvider.ShouldBeNull();
    }

    // Builds a real GatewayAuthManager whose auth.json carries a github-copilot OAuth entry, so
    // the credential-resolution seam under test is exercised for real (never mocked).
    private static GatewayAuthManager CreateAuthManagerWithCopilotToken(string accessToken)
    {
        var fileSystem = new MockFileSystem();
        var configDir = PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem);
        fileSystem.Directory.CreateDirectory(configDir);
        var authFilePath = Path.Combine(configDir, "auth.json");
        fileSystem.File.WriteAllText(authFilePath, $$"""
            {
              "github-copilot": {
                "type": "oauth",
                "refresh": "unused",
                "access": "{{accessToken}}",
                "expires": 4102444800000,
                "endpoint": "https://api.enterprise.githubcopilot.com"
              }
            }
            """);

        var monitor = new StaticOptionsMonitor<PlatformConfig>(new PlatformConfig());
        return new GatewayAuthManager(monitor, NullLogger<GatewayAuthManager>.Instance, fileSystem);
    }

    // -----------------------------------------------------------------------
    // Timeout wiring (#auto-title config): timeoutSeconds is now honoured
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GenerateAndSaveAsync_TinyTimeout_AbandonsSlowCall_NoSave()
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

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateDelayingLlmClient("Slow Title", TimeSpan.FromSeconds(5)),
            NullLogger.Instance);

        // A 1s timeout must abandon the 5s call -> no title saved. A hardcoded 30s timeout would
        // wait it out and save, so this fails RED until timeoutSeconds is actually wired through.
        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "q", "a", null, 1, CancellationToken.None);

        result.ShouldBeNull();
        store.Verify(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAndSaveAsync_GenerousTimeout_AllowsFastCall_Saves()
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

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Fast Title"),
            NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "q", "a", null, 60, CancellationToken.None);

        result.ShouldBe("Fast Title");
    }

    [Fact]
    public async Task GenerateAndSaveAsync_NonPositiveTimeout_ClampsToDefault_AllowsFastCall()
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

        var svc = new ConversationAutoTitleService(
            store.Object,
            CreateFakeLlmClient("Clamped Title"),
            NullLogger.Instance);

        // 0 must clamp to 30s, not cancel instantly; a fast call still succeeds.
        var result = await svc.GenerateAndSaveAsync(
            ConvId, AgentId, "q", "a", null, 0, CancellationToken.None);

        result.ShouldBe("Clamped Title");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// LlmClient whose provider delays before returning so a configured timeout can be exercised.
    /// </summary>
    private static LlmClient CreateDelayingLlmClient(string responseText, TimeSpan delay)
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
        providerRegistry.Register(new DelayingApiProvider(responseText, delay));
        return new LlmClient(providerRegistry, modelRegistry);
    }

    private sealed class DelayingApiProvider : IApiProvider
    {
        private readonly string _responseText;
        private readonly TimeSpan _delay;
        public DelayingApiProvider(string responseText, TimeSpan delay) { _responseText = responseText; _delay = delay; }
        public string Api => "fake-api";
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => throw new NotImplementedException();
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var stream = new LlmStream();
            _ = Task.Run(async () =>
            {
                await Task.Delay(_delay);
                var msg = new AssistantMessage(
                    Content: [new TextContent(_responseText)], Api: "fake-api", Provider: "fake",
                    ModelId: "fake-model", Usage: new Usage(), StopReason: StopReason.Stop,
                    ErrorMessage: null, ResponseId: null,
                    Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                stream.Push(new DoneEvent(StopReason.Stop, msg));
            });
            return stream;
        }
    }

    /// <summary>
    /// #1994: creates an LlmClient whose provider returns a completion with an explicit content
    /// block list, so extraction against reasoning-only (ThinkingContent) completions can be
    /// exercised — the exact live rawLength=0 failure shape.
    /// </summary>
    private static LlmClient CreateContentLlmClient(IReadOnlyList<ContentBlock> content)
    {
        var modelRegistry = new ModelRegistry();
        var fakeModel = new LlmModel(
            Id: "fake-model",
            Name: "fake-model",
            Api: "fake-api",
            Provider: "fake",
            BaseUrl: "https://fake.example.com",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 4096,
            MaxTokens: 512);
        modelRegistry.Register("fake", fakeModel);
        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new ContentApiProvider(content));
        return new LlmClient(providerRegistry, modelRegistry);
    }

    private sealed class ContentApiProvider : IApiProvider
    {
        private readonly IReadOnlyList<ContentBlock> _content;
        public ContentApiProvider(IReadOnlyList<ContentBlock> content) => _content = content;
        public string Api => "fake-api";
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => throw new NotImplementedException("ContentApiProvider only supports StreamSimple/CompleteSimple");
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var msg = new AssistantMessage(
                Content: [.. _content],
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

    /// <summary>
    /// Creates an LlmClient backed by the copilot-shaped <see cref="IndividualModel"/> and a
    /// capturing provider that records the model instance actually passed to the transport, so
    /// the endpoint-override (#1636) can be asserted against the model that hit the wire.
    /// </summary>
    private static LlmClient CreateCapturingLlmClient(string responseText, LlmModel registeredModel, Action<LlmModel> captureModel)
        => CreateCapturingLlmClient(responseText, registeredModel, captureModel, null);

    private static LlmClient CreateCapturingLlmClient(
        string responseText,
        LlmModel registeredModel,
        Action<LlmModel> captureModel,
        Action<SimpleStreamOptions?>? captureOptions)
    {
        var modelRegistry = new ModelRegistry();
        modelRegistry.Register(registeredModel.Provider, registeredModel);

        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new CapturingApiProvider(responseText, captureModel, captureOptions));

        return new LlmClient(providerRegistry, modelRegistry);
    }

    private sealed class CapturingApiProvider : IApiProvider
    {
        private readonly string _responseText;
        private readonly Action<LlmModel> _captureModel;
        private readonly Action<SimpleStreamOptions?>? _captureOptions;

        public CapturingApiProvider(
            string responseText,
            Action<LlmModel> captureModel,
            Action<SimpleStreamOptions?>? captureOptions = null)
        {
            _responseText = responseText;
            _captureModel = captureModel;
            _captureOptions = captureOptions;
        }

        public string Api => "capture-api";

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        {
            throw new NotImplementedException("CapturingApiProvider only supports StreamSimple/CompleteSimple");
        }

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            _captureModel(model);
            _captureOptions?.Invoke(options);
            var msg = new AssistantMessage(
                Content: [new TextContent(_responseText)],
                Api: "capture-api",
                Provider: model.Provider,
                ModelId: model.Id,
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

// File-scoped options monitor so the #2025 auth-seam tests can build a real GatewayAuthManager
// without colliding with the file-scoped StaticOptionsMonitor definitions in sibling test files.
file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
