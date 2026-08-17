using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// #3229 — the fence. Every model id that the platform SHIPS as a configuration default must be
/// resolvable from <see cref="BuiltInModels"/> ALONE, with no dynamic discovery.
/// <para>
/// The defect this closes: <c>gateway.auxiliary.titling.model</c> shipped as <c>gpt-5.6-luna</c>
/// while <c>BuiltInModels.RegisterAll</c> stopped at <c>gpt-5.4-mini</c>. That id existed only
/// because Copilot dynamic discovery overlaid it at startup, and discovery is explicitly
/// best-effort ("failures fall back to built-in models"). On any run where discovery failed the
/// shipped default resolved to nothing and auto-titling silently took its
/// first-registered-model fallback — the exact #1994 failure.
/// </para>
/// <para>
/// These tests deliberately DISCOVER the default ids by walking the JSON the contributors actually
/// hydrate, rather than restating a constant. A fence that enumerates the ids by reading the same
/// literal it validates proves nothing.
/// </para>
/// </summary>
public sealed class SchemaContributorDefaultModelsResolveTests
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// The contributors the gateway composition root registers. Mirrors
    /// <c>GatewayServiceCollectionExtensions</c> and <c>SchemaContributorBindingTests</c>.
    /// </summary>
    private static IReadOnlyList<IConfigSchemaContributor> BuiltInContributors() =>
    [
        new GatewaySchemaContributor(),
        new CompactionSchemaContributor(),
        new AuxiliarySchemaContributor(),
        new AutoUpdateSchemaContributor(),
        new CronSchemaContributor(),
        new SessionStoreSchemaContributor(),
        new RateLimitSchemaContributor(),
    ];

    /// <summary>
    /// A model id shipped as a default, together with the config path it was found at, so a failure
    /// names the offending setting rather than just the id.
    /// </summary>
    private sealed record DefaultModelId(string Path, string ModelId);

    /// <summary>
    /// Hydrates every built-in contributor into a single config document and walks it, collecting
    /// every non-empty string value whose property name denotes a model id (<c>model</c>,
    /// <c>summarizationModel</c>, <c>defaultModel</c>, ...). Discovery is structural: adding a new
    /// <c>...Model</c> default anywhere brings it under this fence automatically.
    /// </summary>
    private static IReadOnlyList<DefaultModelId> CollectShippedDefaultModelIds()
    {
        var root = new JsonObject();
        foreach (var contributor in BuiltInContributors())
        {
            var defaults = JsonSerializer.SerializeToNode(contributor.GetDefaults(), SerializeOptions);
            ConfigHydrationService.MergeAtPath(root, contributor.SectionPath, (JsonObject)defaults!);
        }

        var found = new List<DefaultModelId>();
        Walk(root, path: string.Empty, found);
        return found;
    }

    private static void Walk(JsonNode? node, string path, List<DefaultModelId> found)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    var childPath = path.Length == 0 ? key : $"{path}.{key}";
                    if (IsModelIdKey(key) && child is JsonValue value &&
                        value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id))
                    {
                        found.Add(new DefaultModelId(childPath, id));
                    }

                    Walk(child, childPath, found);
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    Walk(array[i], $"{path}[{i}]", found);
                break;
        }
    }

    private static bool IsModelIdKey(string key) =>
        key.EndsWith("model", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A registry populated by the built-in table ONLY — no discovery, no config overlay. This is
    /// the registry a gateway has when the Copilot discovery call fails.
    /// </summary>
    private static ModelRegistry BuildDiscoveryFreeRegistry()
    {
        var registry = new ModelRegistry();
        new BuiltInModels().RegisterAll(registry);
        return registry;
    }

    private static LlmModel? ResolveAnyProvider(ModelRegistry registry, string modelId) =>
        registry.GetProviders()
            .Select(provider => registry.GetModel(provider, modelId))
            .FirstOrDefault(model => model is not null);

    [Fact]
    public void ShippedDefaultModelIds_AreDiscoveredFromContributors_AndAreNotVacuous()
    {
        // Non-vacuity floor. A fence that finds zero defaults to check passes forever while
        // checking nothing. Assert the walk actually located the drift-prone titling default.
        var defaults = CollectShippedDefaultModelIds();

        defaults.ShouldNotBeEmpty(
            "the contributor walk found no shipped model-id defaults at all — the fence would be vacuous");
        defaults.Select(d => d.Path).ShouldContain(
            "gateway.auxiliary.titling.model",
            "the titling default is the setting #3229 was filed about; if the walk no longer sees it the fence is blind");
    }

    [Fact]
    public void SchemaContributorDefaultModels_AllResolve_AgainstBuiltInModelsAlone()
    {
        var registry = BuildDiscoveryFreeRegistry();
        var defaults = CollectShippedDefaultModelIds();

        // Guard the assertion loop itself against silently iterating nothing.
        defaults.ShouldNotBeEmpty();

        var unresolved = defaults
            .Where(d => ResolveAnyProvider(registry, d.ModelId) is null)
            .ToList();

        unresolved.ShouldBeEmpty(
            "every model id shipped as a configuration default must resolve from BuiltInModels alone " +
            "(discovery is best-effort and falls back to the built-in table). Unresolved: " +
            string.Join(", ", unresolved.Select(d => $"{d.Path}='{d.ModelId}'")));
    }

    [Fact]
    public async Task AutoTitle_ShippedTitlingDefault_ResolvesWithoutFallback_UnderDiscoveryFreeRegistry()
    {
        // AC4: ResolveModel must not take its fallback branch for the shipped default. Asserted
        // observably — the model that reaches the provider is the configured one, not the
        // first-registered model the fallback would have picked.
        var titlingDefault = CollectShippedDefaultModelIds()
            .SingleOrDefault(d => d.Path == "gateway.auxiliary.titling.model");
        titlingDefault.ShouldNotBeNull();

        var registry = BuildDiscoveryFreeRegistry();
        var expected = ResolveAnyProvider(registry, titlingDefault!.ModelId);
        expected.ShouldNotBeNull($"'{titlingDefault.ModelId}' is not in the built-in table");

        LlmModel? modelSeenByProvider = null;
        var providers = new ApiProviderRegistry();
        providers.Register(new AnyApiCapturingProvider(expected!.Api, "A Title", m => modelSeenByProvider = m));
        var llmClient = new LlmClient(providers, registry);

        var convId = ConversationId.From("conv-3229");
        var agentId = AgentId.From("agent-3229");
        var conv = new Conversation
        {
            ConversationId = convId,
            AgentId = agentId,
            Title = ConversationAutoTitleService.DefaultTitle,
        };
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(convId, It.IsAny<CancellationToken>())).ReturnsAsync(conv);
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new ConversationAutoTitleService(store.Object, llmClient, NullLogger.Instance);

        var result = await svc.GenerateAndSaveAsync(
            convId, agentId, "what do cats eat?", "cats eat...", titlingDefault.ModelId, 30, CancellationToken.None);

        result.ShouldBe("A Title");
        modelSeenByProvider.ShouldNotBeNull();
        modelSeenByProvider!.Id.ShouldBe(titlingDefault.ModelId);
    }

    /// <summary>
    /// Capturing provider bound to whatever <c>Api</c> the resolved built-in model declares, so the
    /// titling call routes through the real registry lookup rather than a bespoke fake model.
    /// </summary>
    private sealed class AnyApiCapturingProvider : IApiProvider
    {
        private readonly string _responseText;
        private readonly Action<LlmModel> _capture;

        public AnyApiCapturingProvider(string api, string responseText, Action<LlmModel> capture)
        {
            Api = api;
            _responseText = responseText;
            _capture = capture;
        }

        public string Api { get; }

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => throw new NotImplementedException("Only StreamSimple is exercised by auto-titling.");

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            _capture(model);
            var msg = new AssistantMessage(
                Content: [new TextContent(_responseText)],
                Api: Api,
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
