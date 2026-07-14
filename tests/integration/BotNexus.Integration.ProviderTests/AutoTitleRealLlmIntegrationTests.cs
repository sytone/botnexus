using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.GitHubModels;
using BotNexus.Agent.Providers.OpenAICompat;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Integration.ProviderTests;

/// <summary>
/// #1903 D3 — MANDATORY real-LLM end-to-end auto-title test.
/// <para>
/// Auto-titling's entire job is a real LLM round-trip: resolve the titling model + endpoint,
/// build the prompt, call the provider, sanitize the result, and write it to the conversation
/// store. A mock-only gate is not sufficient (this is the exact area #1639 churned), so this
/// suite drives <see cref="ConversationAutoTitleService.GenerateAndSaveAsync"/> against a REAL
/// github-models <see cref="LlmClient"/> and asserts the persisted conversation title flips off
/// the "New conversation" default to a real generated title.
/// </para>
/// <para>
/// Uses the established real-LLM pattern: <see cref="RequiresGitHubTokenFactAttribute"/> +
/// SkippableFact so the test skips gracefully when GITHUB_TOKEN is absent (local dev) or the
/// GitHub Models API is degraded — it never hard-fails on a missing token or an outage. When
/// GITHUB_TOKEN IS set and the API is healthy, the test genuinely calls the model and passes.
/// </para>
/// <para>
/// #1994 note: the reasoning-model empty-text extraction path (a completion with only a
/// ThinkingContent block and no TextContent, which was the live rawLength=0 no-persist bug) is
/// NOT reproducible here because github-models gpt-4o-mini does not emit thinking blocks. That
/// seam is covered deterministically by ConversationAutoTitleServiceTests.ExtractTitleText_*
/// and the ThinkingContent-only GenerateAndSaveAsync unit tests. This suite proves the real
/// round-trip; those prove the extraction fallback.
/// </para>
/// </summary>
[Trait("Category", "ProviderIntegration")]
[Trait("Provider", "github-models")]
[Collection("GitHubModels")]
public sealed class AutoTitleRealLlmIntegrationTests : IAsyncLifetime
{
    private const string ApiDegradedReason =
        "GitHub Models API is degraded (returning empty responses). Skipping integration test.";

    private static readonly AgentId AgentId = AgentId.From("autotitle-e2e-agent");

    private readonly HttpClient _httpClient = new();
    private LlmClient _llmClient = null!;
    private string _modelId = "gpt-4o-mini";
    private bool _apiAvailable;

    public async Task InitializeAsync()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
            return;

        // Register the github-models gpt-4o-mini model (same model the provider tests use) and back
        // it with the real OpenAI-compatible transport. The provider resolves the token from
        // GITHUB_TOKEN via EnvironmentApiKeys at call time, exactly like production.
        var modelRegistry = new ModelRegistry();
        GitHubModelsProvider.RegisterModels(modelRegistry);

        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new OpenAICompatProvider(_httpClient));

        _llmClient = new LlmClient(providerRegistry, modelRegistry);

        _apiAvailable = await ProbeApiAvailabilityAsync();
    }

    public Task DisposeAsync()
    {
        _httpClient.Dispose();
        return Task.CompletedTask;
    }

    [RequiresGitHubTokenFact]
    public async Task GenerateAndSaveAsync_RealModel_UserAssistantExchange_FlipsTitleOffDefault()
    {
        Skip.If(!_apiAvailable, ApiDegradedReason);

        var store = new InMemoryConversationStore();
        var convId = ConversationId.From("c_autotitle_e2e_user");
        await store.SaveAsync(
            new Conversation
            {
                ConversationId = convId,
                AgentId = AgentId,
                Title = ConversationAutoTitleService.DefaultTitle,
            },
            CancellationToken.None);

        var svc = new ConversationAutoTitleService(store, _llmClient, NullLogger.Instance);

        // Real titling round-trip: model resolution + endpoint + prompt + sanitize + store write.
        var title = await svc.GenerateAndSaveAsync(
            convId,
            AgentId,
            userText: "Can you explain how photosynthesis works in plants?",
            assistantText: "Photosynthesis converts sunlight, water, and carbon dioxide into glucose and oxygen inside chloroplasts.",
            preferredModelId: _modelId,
            timeoutSeconds: 30,
            CancellationToken.None);

        // Degraded API returns null (empty completion) — skip rather than fail.
        Skip.If(title is null, ApiDegradedReason);

        title.ShouldNotBeNullOrWhiteSpace();
        ConversationAutoTitleService.IsDefaultTitle(title).ShouldBeFalse(
            "the real model must produce a non-default title");
        title!.Length.ShouldBeLessThanOrEqualTo(80);

        var persisted = await store.GetAsync(convId, CancellationToken.None);
        persisted.ShouldNotBeNull();
        persisted!.Title.ShouldBe(title);
        ConversationAutoTitleService.IsDefaultTitle(persisted.Title).ShouldBeFalse();
    }

    [RequiresGitHubTokenFact]
    public async Task GenerateAndSaveAsync_RealModel_AgentInitiated_AssistantOnly_FlipsTitleOffDefault()
    {
        // #1903 companion: agent-initiated conversations have no user turn (user=0, assistant>=1),
        // so titling uses the new assistant-only prompt. Validate that prompt against a real model.
        Skip.If(!_apiAvailable, ApiDegradedReason);
        await Task.Delay(4000); // rate-limit spacing vs the previous call

        var store = new InMemoryConversationStore();
        var convId = ConversationId.From("c_autotitle_e2e_agent");
        await store.SaveAsync(
            new Conversation
            {
                ConversationId = convId,
                AgentId = AgentId,
                Title = ConversationAutoTitleService.DefaultTitle,
            },
            CancellationToken.None);

        var svc = new ConversationAutoTitleService(store, _llmClient, NullLogger.Instance);

        var title = await svc.GenerateAndSaveAsync(
            convId,
            AgentId,
            userText: null, // assistant-only titling path
            assistantText: "I've finished the nightly database backup and verified the archive checksum.",
            preferredModelId: _modelId,
            timeoutSeconds: 30,
            CancellationToken.None);

        Skip.If(title is null, ApiDegradedReason);

        title.ShouldNotBeNullOrWhiteSpace();
        ConversationAutoTitleService.IsDefaultTitle(title).ShouldBeFalse(
            "the assistant-only prompt must still produce a non-default title against a real model");

        var persisted = await store.GetAsync(convId, CancellationToken.None);
        persisted.ShouldNotBeNull();
        ConversationAutoTitleService.IsDefaultTitle(persisted!.Title).ShouldBeFalse();
    }

    /// <summary>
    /// Probes the titling round-trip with a trivial exchange so a degraded API (200 + empty body)
    /// makes the whole suite skip rather than fail.
    /// </summary>
    private async Task<bool> ProbeApiAvailabilityAsync()
    {
        try
        {
            var store = new InMemoryConversationStore();
            var probeId = ConversationId.From("c_autotitle_e2e_probe");
            await store.SaveAsync(
                new Conversation
                {
                    ConversationId = probeId,
                    AgentId = AgentId,
                    Title = ConversationAutoTitleService.DefaultTitle,
                },
                CancellationToken.None);

            var svc = new ConversationAutoTitleService(store, _llmClient, NullLogger.Instance);
            var title = await svc.GenerateAndSaveAsync(
                probeId, AgentId, "ping", "pong", _modelId, 30, CancellationToken.None);
            return !string.IsNullOrWhiteSpace(title);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}
