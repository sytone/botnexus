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
/// Regression tests for #1635: the compaction summarization call must apply the per-provider
/// API-endpoint override (e.g. enterprise vs individual GitHub Copilot) to the resolved candidate
/// model(s), exactly as the live agent path does in <c>InProcessIsolationStrategy</c>. Without this,
/// a model registered with the static individual Copilot BaseUrl is POSTed to the wrong host on an
/// enterprise account, returns HTTP 421 Misdirected Request, the summary comes back empty, and the
/// session is wedged (cannot shed context) until the circuit breaker eventually gives up.
/// </summary>
public sealed class LlmSessionCompactorEndpointOverrideTests
{
    // A copilot-shaped model registered with the STATIC individual BaseUrl (as BuiltInModels does).
    private static readonly LlmModel IndividualModel = new(
        Id: "claude-opus-4.8", Name: "Opus", Api: "copilot-api", Provider: "github-copilot",
        BaseUrl: "https://api.individual.githubcopilot.com", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 128000, MaxTokens: 4096);

    private const string EnterpriseEndpoint = "https://api.enterprise.githubcopilot.com";

    [Fact]
    public async Task CompactAsync_AppliesEndpointOverride_ToModelReachingProvider()
    {
        // The endpoint resolver returns the enterprise endpoint for github-copilot (mirrors
        // GatewayAuthManager.GetApiEndpoint reading auth.json's endpoint field).
        LlmModel? modelSeenByProvider = null;
        var compactor = CreateCompactor(
            endpointResolver: provider =>
                provider == "github-copilot" ? EnterpriseEndpoint : null,
            captureModel: m => modelSeenByProvider = m,
            summary: "compacted ok");

        var session = CreateLargeSession(300);
        var options = OverrideOptions();

        var result = await compactor.CompactAsync(session, options);

        result.Succeeded.ShouldBeTrue();
        modelSeenByProvider.ShouldNotBeNull();
        // The model that actually hit the transport must carry the ENTERPRISE BaseUrl, not the
        // static individual one it was registered with.
        modelSeenByProvider!.BaseUrl.ShouldBe(EnterpriseEndpoint);
    }

    [Fact]
    public void BuildCandidateModels_AppliesEndpointOverride_ToAllCandidates()
    {
        var compactor = CreateCompactor(
            endpointResolver: _ => EnterpriseEndpoint,
            captureModel: null,
            summary: "x");

        var candidates = compactor.BuildCandidateModels(IndividualModel.Id, IndividualModel.Provider);

        candidates.ShouldNotBeEmpty();
        candidates.ShouldAllBe(c => c.BaseUrl == EnterpriseEndpoint);
    }

    [Fact]
    public void BuildCandidateModels_NoEndpointOverride_LeavesBaseUrlUnchanged()
    {
        // Resolver returns null (no override configured) → static BaseUrl preserved.
        var compactor = CreateCompactor(
            endpointResolver: _ => null,
            captureModel: null,
            summary: "x");

        var candidates = compactor.BuildCandidateModels(IndividualModel.Id, IndividualModel.Provider);

        candidates.ShouldNotBeEmpty();
        candidates[0].BaseUrl.ShouldBe(IndividualModel.BaseUrl); // unchanged
    }

    [Fact]
    public void BuildCandidateModels_EmptyEndpoint_LeavesBaseUrlUnchanged()
    {
        // A whitespace/empty endpoint must be treated as "no override".
        var compactor = CreateCompactor(
            endpointResolver: _ => "   ",
            captureModel: null,
            summary: "x");

        var candidates = compactor.BuildCandidateModels(IndividualModel.Id, IndividualModel.Provider);

        candidates[0].BaseUrl.ShouldBe(IndividualModel.BaseUrl);
    }

    [Fact]
    public void BuildCandidateModels_NullResolver_LeavesBaseUrlUnchanged()
    {
        // No resolver at all (e.g. no auth manager) → static BaseUrl preserved, no crash.
        var compactor = CreateCompactor(
            endpointResolver: null,
            captureModel: null,
            summary: "x");

        var candidates = compactor.BuildCandidateModels(IndividualModel.Id, IndividualModel.Provider);

        candidates[0].BaseUrl.ShouldBe(IndividualModel.BaseUrl);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static CompactionOptions OverrideOptions() => new()
    {
        ContextWindowTokens = 100,
        TokenThresholdRatio = 0.01,
        PreservedTurns = 2,
        MaxSummaryChars = 5000,
        SummarizationModel = IndividualModel.Id,
        SummarizationProvider = IndividualModel.Provider
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
        Func<string, string?>? endpointResolver,
        Action<LlmModel>? captureModel,
        string summary)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(IndividualModel.Provider, IndividualModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(p => p.Api).Returns(IndividualModel.Api);
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
            NullLogger<LlmSessionCompactor>.Instance,
            endpointResolver: endpointResolver);
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
