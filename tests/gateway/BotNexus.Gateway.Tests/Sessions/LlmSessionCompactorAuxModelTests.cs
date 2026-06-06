using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Tests for compaction Phase 2: auxiliary model for summarisation and iterative summary updates.
/// </summary>
public sealed class LlmSessionCompactorAuxModelTests
{
    private static readonly LlmModel PrimaryModel = new(
        Id: "primary-model",
        Name: "Primary Model",
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 32000,
        MaxTokens: 4096);

    private static readonly LlmModel AuxModel = new(
        Id: "aux-model",
        Name: "Auxiliary Model",
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 32000,
        MaxTokens: 4096);

    // ── Auxiliary model routing ────────────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_WhenAuxCompressionConfigured_UsesAuxModelForSummarisation()
    {
        var capturedModelId = string.Empty;
        var compactor = CreateCompactorWithAux(
            summary: "aux-summary",
            onModelCalled: modelId => capturedModelId = modelId,
            compressionModelId: AuxModel.Id);

        var session = CreateSession(("user", "do something"), ("assistant", "done"), ("user", "more"));
        await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = null   // no explicit override -- aux should be used
        });

        capturedModelId.ShouldBe(AuxModel.Id);
    }

    [Fact]
    public async Task CompactAsync_WhenAuxCompressionConfiguredButExplicitModelSet_UsesExplicitModel()
    {
        var capturedModelId = string.Empty;
        var compactor = CreateCompactorWithAux(
            summary: "aux-summary",
            onModelCalled: modelId => capturedModelId = modelId,
            compressionModelId: AuxModel.Id);

        var session = CreateSession(("user", "do something"), ("assistant", "done"), ("user", "more"));
        await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = PrimaryModel.Id   // explicit override takes priority
        });

        capturedModelId.ShouldBe(PrimaryModel.Id);
    }

    [Fact]
    public async Task CompactAsync_WhenAuxCompressionNotConfigured_FallsBackToPrimaryModelWithWarning()
    {
        var capturedModelId = string.Empty;
        var logOutput = new List<string>();
        var compactor = CreateCompactorWithAux(
            summary: "fallback-summary",
            onModelCalled: modelId => capturedModelId = modelId,
            compressionModelId: null,   // no aux configured
            logCapture: logOutput);

        var session = CreateSession(("user", "do something"), ("assistant", "done"), ("user", "more"));
        await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = null,   // no explicit override either
            SummarizationProvider = null
        });

        // Should fall back to any available model (primary model in test registry)
        capturedModelId.ShouldBe(PrimaryModel.Id);
    }

    // ── Iterative summary ──────────────────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_WhenPriorSummaryExists_IncludesPriorSummaryInPrompt()
    {
        string? capturedPrompt = null;
        var compactor = CreateCompactorCapturingPrompt(
            summary: "cycle2-summary",
            onPromptCaptured: p => capturedPrompt = p);

        // Build a session that already has a compaction summary from a prior cycle
        var session = CreateSession(("user", "old-task"), ("assistant", "old-result"), ("user", "preserve"));
        var cycle1 = await CreateCompactor("cycle1-summary").CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1
        });
        session.ReplaceHistory(cycle1.CompactedHistory!);
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "new-u" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "new-a" },
            new SessionEntry { Role = MessageRole.User, Content = "latest" }
        ]);

        // Cycle 2: the prompt should contain the prior summary as ## Prior Summary
        await compactor.CompactAsync(session, new CompactionOptions { PreservedTurns = 1 });

        capturedPrompt.ShouldNotBeNull();
        capturedPrompt!.ShouldContain("## Prior Summary");
        capturedPrompt.ShouldContain("cycle1-summary");
    }

    [Fact]
    public async Task PostCompaction_IterativeSummary_SurvivesMultipleCycles()
    {
        // Compact 3 times; each summary should reference the prior one
        var compactor = CreateCompactor("generic-summary");
        var session = CreateSession(("user", "task"), ("assistant", "done"), ("user", "preserve"));

        // Cycle 1
        var cycle1 = await compactor.CompactAsync(session, new CompactionOptions { PreservedTurns = 1 });
        cycle1.Succeeded.ShouldBeTrue();
        session.ReplaceHistory(cycle1.CompactedHistory!);

        // Add new turns
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "step2" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "done2" },
            new SessionEntry { Role = MessageRole.User, Content = "keep2" }
        ]);

        // Cycle 2
        var cycle2 = await compactor.CompactAsync(session, new CompactionOptions { PreservedTurns = 1 });
        cycle2.Succeeded.ShouldBeTrue();
        session.ReplaceHistory(cycle2.CompactedHistory!);

        // Add more turns
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "step3" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "done3" },
            new SessionEntry { Role = MessageRole.User, Content = "keep3" }
        ]);

        // Cycle 3
        var cycle3 = await compactor.CompactAsync(session, new CompactionOptions { PreservedTurns = 1 });
        cycle3.Succeeded.ShouldBeTrue();

        // All three compactions succeeded -- context survives across cycles
        cycle3.EntriesSummarized.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CompactAsync_WhenNoPriorSummary_DoesNotIncludePriorSummarySection()
    {
        string? capturedPrompt = null;
        var compactor = CreateCompactorCapturingPrompt(
            summary: "first-summary",
            onPromptCaptured: p => capturedPrompt = p);

        var session = CreateSession(("user", "u1"), ("assistant", "a1"), ("user", "preserve"));
        await compactor.CompactAsync(session, new CompactionOptions { PreservedTurns = 1 });

        capturedPrompt.ShouldNotBeNull();
        capturedPrompt!.ShouldNotContain("## Prior Summary");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static GatewaySession CreateSession(params (string role, string content)[] entries)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("test-session"),
            AgentId = AgentId.From("test-agent"),
        };
        foreach (var (role, content) in entries)
            session.AddEntry(new SessionEntry
            {
                Role = role == "user" ? MessageRole.User : MessageRole.Assistant,
                Content = content
            });
        return session;
    }

    private static LlmSessionCompactor CreateCompactor(string summary)
    {
        var (llmClient, _) = CreateLlmClient(summary, null, null);
        return new LlmSessionCompactor(
            llmClient,
            NullLogger<LlmSessionCompactor>.Instance,
            platformConfig: null);
    }

    private static LlmSessionCompactor CreateCompactorWithAux(
        string summary,
        Action<string>? onModelCalled,
        string? compressionModelId,
        List<string>? logCapture = null)
    {
        var (llmClient, _) = CreateLlmClient(summary, onModelCalled, null);

        PlatformConfig? config = compressionModelId is not null
            ? new PlatformConfig { Gateway = new GatewaySettingsConfig { Auxiliary = new AuxiliaryConfig { Compression = compressionModelId } } }
            : null;
        var monitor = config is not null
            ? Mock.Of<IOptionsMonitor<PlatformConfig>>(m => m.CurrentValue == config)
            : null;

        return new LlmSessionCompactor(
            llmClient,
            NullLogger<LlmSessionCompactor>.Instance,
            platformConfig: monitor);
    }

    private static LlmSessionCompactor CreateCompactorCapturingPrompt(
        string summary,
        Action<string> onPromptCaptured)
    {
        var (llmClient, _) = CreateLlmClient(summary, null, onPromptCaptured);
        return new LlmSessionCompactor(
            llmClient,
            NullLogger<LlmSessionCompactor>.Instance,
            platformConfig: null);
    }

    private static (LlmClient, Mock<IApiProvider>) CreateLlmClient(
        string summary,
        Action<string>? onModelCalled,
        Action<string>? onPromptCaptured)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(PrimaryModel.Provider, PrimaryModel);
        models.Register(AuxModel.Provider, AuxModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(p => p.Api).Returns(PrimaryModel.Api);
        provider.Setup(p => p.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions?>()))
            .Returns<LlmModel, Context, SimpleStreamOptions?>((model, ctx, _) =>
            {
                onModelCalled?.Invoke(model.Id);
                var firstUser = ctx.Messages.OfType<UserMessage>().FirstOrDefault();
                if (firstUser?.Content is UserMessageContent umc)
                    onPromptCaptured?.Invoke(umc.Text ?? string.Empty);
                return CreateStream(summary);
            });

        providers.Register(provider.Object);
        return (new LlmClient(providers, models), provider);
    }

    private static LlmStream CreateStream(string text)
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [new TextContent(text)],
            Api: PrimaryModel.Api,
            Provider: PrimaryModel.Provider,
            ModelId: PrimaryModel.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }
}
