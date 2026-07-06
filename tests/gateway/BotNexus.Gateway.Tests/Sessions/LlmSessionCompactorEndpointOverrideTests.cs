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
/// Regression guard migrated from #1635/#1638 to #1639. Originally the compaction call had to
/// re-apply the per-provider API-endpoint override (enterprise vs individual GitHub Copilot) to the
/// resolved candidate model, because the model was registered with the static individual BaseUrl.
/// After #1639 the model is BORN with the correct host (resolved at registration in
/// BuiltInModels/discovery), so the compactor applies no override. This guard proves the end-to-end
/// property that still matters: the model that actually reaches the provider carries the ENTERPRISE
/// host - now purely by construction, with NO consumer-side <c>with { BaseUrl }</c> patch in the
/// compaction path.
/// </summary>
public sealed class LlmSessionCompactorEndpointOverrideTests
{
    private const string EnterpriseEndpoint = "https://api.enterprise.githubcopilot.com";
    private const string IndividualEndpoint = "https://api.individual.githubcopilot.com";

    // A copilot-shaped model registered ALREADY carrying the enterprise BaseUrl, exactly as
    // BuiltInModels/discovery would produce it on an enterprise account after #1639.
    private static readonly LlmModel EnterpriseModel = new(
        Id: "claude-opus-4.8", Name: "Opus", Api: "copilot-api", Provider: "github-copilot",
        BaseUrl: EnterpriseEndpoint, Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 128000, MaxTokens: 4096);

    // The same model as an individual account would register it.
    private static readonly LlmModel IndividualModel = EnterpriseModel with { BaseUrl = IndividualEndpoint };

    [Fact]
    public async Task CompactAsync_ModelReachingProvider_CarriesEnterpriseHost_ByConstruction()
    {
        // The registry holds the enterprise-resolved model (no consumer override anywhere).
        LlmModel? modelSeenByProvider = null;
        var compactor = CreateCompactor(
            registeredModel: EnterpriseModel,
            captureModel: m => modelSeenByProvider = m,
            summary: "compacted ok");

        var session = CreateLargeSession(300);
        var options = OverrideOptions();

        var result = await compactor.CompactAsync(session, options);

        result.Succeeded.ShouldBeTrue();
        modelSeenByProvider.ShouldNotBeNull();
        // The model that actually hit the transport must carry the ENTERPRISE BaseUrl - now purely
        // because it was registered that way, with no override in the compaction path.
        modelSeenByProvider!.BaseUrl.ShouldBe(EnterpriseEndpoint);
    }

    [Fact]
    public void BuildCandidateModels_LeavesRegisteredEnterpriseBaseUrlIntact()
    {
        var compactor = CreateCompactor(
            registeredModel: EnterpriseModel,
            captureModel: null,
            summary: "x");

        var candidates = compactor.BuildCandidateModels(EnterpriseModel.Id, EnterpriseModel.Provider);

        candidates.ShouldNotBeEmpty();
        // Candidates flow through untouched - the enterprise host is preserved by construction.
        candidates.ShouldAllBe(c => c.BaseUrl == EnterpriseEndpoint);
    }

    [Fact]
    public void BuildCandidateModels_IndividualModel_KeepsIndividualBaseUrl()
    {
        // An individual account registers the individual host; the compactor must not rewrite it.
        var compactor = CreateCompactor(
            registeredModel: IndividualModel,
            captureModel: null,
            summary: "x");

        var candidates = compactor.BuildCandidateModels(IndividualModel.Id, IndividualModel.Provider);

        candidates.ShouldNotBeEmpty();
        candidates[0].BaseUrl.ShouldBe(IndividualEndpoint);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static CompactionOptions OverrideOptions() => new()
    {
        ContextWindowTokens = 100,
        TokenThresholdRatio = 0.01,
        PreservedTurns = 2,
        MaxSummaryChars = 5000,
        SummarizationModel = EnterpriseModel.Id,
        SummarizationProvider = EnterpriseModel.Provider
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
        Action<LlmModel>? captureModel,
        string summary)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(registeredModel.Provider, registeredModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(p => p.Api).Returns(registeredModel.Api);
        provider.Setup(p => p.StreamSimple(
                It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()))
            .Returns((LlmModel m, Context _, SimpleStreamOptions? _) =>
            {
                captureModel?.Invoke(m);
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
