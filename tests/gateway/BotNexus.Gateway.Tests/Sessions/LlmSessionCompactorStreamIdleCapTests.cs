using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Tests for #1652: the compaction summarization call must set a non-zero stream-setup idle cap
/// (StreamSetupTimeoutMs) when the resolved candidate model is a CLOUD provider, so a stalled
/// first-token fails fast (well inside the outer per-attempt TimeoutSeconds watchdog) and the
/// fallback chain can try the next candidate. For LOCAL/self-hosted models (localhost / 127.0.0.1)
/// the cap stays 0 because those endpoints are legitimately slow to warm up. The cap is the
/// configurable CompactionOptions.CronLlmIdleTimeoutMs (default 60000).
/// </summary>
public sealed class LlmSessionCompactorStreamIdleCapTests
{
    private static readonly LlmModel CloudModel = new(
        Id: "claude-haiku-4.5", Name: "Haiku", Api: "anthropic-api", Provider: "anthropic",
        BaseUrl: "https://api.anthropic.com", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 128000, MaxTokens: 4096);

    private static readonly LlmModel LocalModel = new(
        Id: "qwen2.5", Name: "Qwen Local", Api: "anthropic-api", Provider: "ollama",
        BaseUrl: "http://localhost:11434", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 128000, MaxTokens: 4096);

    [Fact]
    public async Task CompactAsync_CloudModel_AppliesIdleCap()
    {
        SimpleStreamOptions? captured = null;
        var compactor = CreateCompactor(CloudModel, opts => captured = opts, "compacted ok");

        var options = OptionsFor(CloudModel);
        var result = await compactor.CompactAsync(CreateLargeSession(300), options);

        result.Succeeded.ShouldBeTrue();
        captured.ShouldNotBeNull();
        // Default CronLlmIdleTimeoutMs is 60000; a cloud model must carry it.
        captured!.StreamSetupTimeoutMs.ShouldBe(60000);
    }

    [Fact]
    public async Task CompactAsync_LocalModel_LeavesIdleCapDisabled()
    {
        SimpleStreamOptions? captured = null;
        var compactor = CreateCompactor(LocalModel, opts => captured = opts, "compacted ok");

        var options = OptionsFor(LocalModel);
        var result = await compactor.CompactAsync(CreateLargeSession(300), options);

        result.Succeeded.ShouldBeTrue();
        captured.ShouldNotBeNull();
        // A localhost endpoint is legitimately slow; the setup-phase cap must stay disabled.
        captured!.StreamSetupTimeoutMs.ShouldBe(0);
    }

    [Fact]
    public async Task CompactAsync_CloudModel_HonoursConfiguredCap()
    {
        SimpleStreamOptions? captured = null;
        var compactor = CreateCompactor(CloudModel, opts => captured = opts, "compacted ok");

        // A custom (smaller) cap from config must flow through to the provider call.
        var options = OptionsFor(CloudModel) with { CronLlmIdleTimeoutMs = 15000 };
        var result = await compactor.CompactAsync(CreateLargeSession(300), options);

        result.Succeeded.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.StreamSetupTimeoutMs.ShouldBe(15000);
    }

    [Fact]
    public async Task CompactAsync_CloudModel_CapDisabledWhenConfigZero()
    {
        SimpleStreamOptions? captured = null;
        var compactor = CreateCompactor(CloudModel, opts => captured = opts, "compacted ok");

        // Operators can opt out entirely by setting the cap to 0.
        var options = OptionsFor(CloudModel) with { CronLlmIdleTimeoutMs = 0 };
        var result = await compactor.CompactAsync(CreateLargeSession(300), options);

        result.Succeeded.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.StreamSetupTimeoutMs.ShouldBe(0);
    }

    private static CompactionOptions OptionsFor(LlmModel model) => new()
    {
        ContextWindowTokens = 100,
        TokenThresholdRatio = 0.01,
        PreservedTurns = 2,
        MaxSummaryChars = 5000,
        SummarizationModel = model.Id,
        SummarizationProvider = model.Provider
    };

    private static LlmStream SuccessStream(string summary)
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [new TextContent(summary)],
            Api: "any", Provider: "any", ModelId: "any",
            Usage: Usage.Empty(), StopReason: StopReason.Stop,
            ErrorMessage: null, ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }

    private static LlmSessionCompactor CreateCompactor(
        LlmModel registeredModel,
        Action<SimpleStreamOptions?> captureOptions,
        string summary)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(registeredModel.Provider, registeredModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(p => p.Api).Returns(registeredModel.Api);
        provider.Setup(p => p.StreamSimple(
                It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()))
            .Returns((LlmModel _, Context _, SimpleStreamOptions? o) =>
            {
                captureOptions(o);
                return SuccessStream(summary);
            });
        providers.Register(provider.Object);

        var llmClient = new LlmClient(providers, models);
        return new LlmSessionCompactor(
            llmClient,
            NullLogger<LlmSessionCompactor>.Instance);
    }

    private static GatewaySession CreateLargeSession(int entryCount)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = AgentId.From("agent")
        };
        var entries = new List<SessionEntry>();
        for (var i = 0; i < entryCount; i++)
        {
            entries.Add(new SessionEntry
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"message {i} " + new string('x', 50)
            });
        }
        session.AddEntries(entries);
        return session;
    }
}
