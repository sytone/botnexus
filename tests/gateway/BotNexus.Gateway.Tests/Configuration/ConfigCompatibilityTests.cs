using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Phase 0e of the Copilot provider carve-out (#810): deployment-safety
/// regression. Loads a representative pre-carve-out <c>config.json</c>
/// fixture and asserts that everything a user currently has in their config
/// continues to deserialize and resolve.
///
/// Compatibility surfaces this test guards against the carve-out:
/// - S1: Model ids in <c>providers.*.defaultModel</c> and <c>providers.*.models</c>
///   keep resolving through <see cref="ModelRegistry.GetModel"/>.
/// - S2: Both provider keys users may have typed (<c>"copilot"</c> alias
///   and <c>"github-copilot"</c> canonical) keep loading and keep resolving
///   to Copilot built-in models.
/// - S4: <see cref="ProviderConfig"/> deserialization is additive only —
///   <c>apiKey</c>, <c>baseUrl</c>, <c>defaultModel</c>, <c>models</c>,
///   and <c>enabled</c> all round-trip from a v1 config.
/// - S5: Schema <c>Version</c> stays at 1 — no implicit migration.
///
/// When Phase 1 introduces dedicated <c>github-copilot-*</c> APIs and
/// (optionally) a <c>CopilotProvider</c>, this test must keep passing
/// unchanged — proving prior configs continue to work without user-side
/// edits.
/// </summary>
public sealed class ConfigCompatibilityTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Configs",
        "precarveout-config.json");

    [Fact]
    public void PreCarveOutConfig_Loads_WithoutErrors()
    {
        var config = PlatformConfigLoader.Load(FixturePath);

        config.ShouldNotBeNull();
        config.Version.ShouldBe(1);
        config.Schema.ShouldBe("https://botnexus.dev/schemas/config.json");
        config.Gateway.ShouldNotBeNull();
        config.Gateway!.ListenUrl.ShouldBe("http://localhost:18790");
    }

    [Fact]
    public void PreCarveOutConfig_PreservesAllProviderEntries()
    {
        var config = PlatformConfigLoader.Load(FixturePath);
        config.Providers.ShouldNotBeNull();
        var providers = config.Providers!;

        providers.ShouldContainKey("copilot");
        providers.ShouldContainKey("github-copilot");
        providers.ShouldContainKey("anthropic");
        providers.ShouldContainKey("openai");
    }

    [Fact]
    public void PreCarveOutConfig_CopilotAliasEntry_RoundTripsAllProviderFields()
    {
        var providers = PlatformConfigLoader.Load(FixturePath).Providers!;
        var copilot = providers["copilot"];

        copilot.Enabled.ShouldBeTrue();
        copilot.ApiKey.ShouldBe("ghu_REDACTED_COPILOT_TOKEN");
        copilot.BaseUrl.ShouldBe("https://api.individual.githubcopilot.com");
        copilot.DefaultModel.ShouldBe("claude-sonnet-4");
        copilot.Models.ShouldNotBeNull();
        copilot.Models!.ShouldBe(["claude-haiku-4.5", "claude-sonnet-4", "claude-sonnet-4.5", "gpt-4.1", "gpt-5.4"]);
    }

    [Fact]
    public void PreCarveOutConfig_GitHubCopilotCanonicalEntry_RoundTripsAllProviderFields()
    {
        var providers = PlatformConfigLoader.Load(FixturePath).Providers!;
        var copilot = providers["github-copilot"];

        copilot.Enabled.ShouldBeTrue();
        copilot.ApiKey.ShouldBe("ghu_REDACTED_ENTERPRISE_TOKEN");
        copilot.BaseUrl.ShouldBe("https://api.enterprise.githubcopilot.com");
        copilot.DefaultModel.ShouldBe("claude-opus-4.6");
        copilot.Models.ShouldNotBeNull();
        copilot.Models!.ShouldBe(["claude-opus-4.6", "gpt-5.2"]);
    }

    [Fact]
    public void PreCarveOutConfig_EveryConfiguredCopilotModel_ResolvesThroughAliasAndCanonicalKeys()
    {
        var providers = PlatformConfigLoader.Load(FixturePath).Providers!;
        var modelRegistry = new ModelRegistry();
        new BuiltInModels().RegisterAll(modelRegistry);

        // S2 invariant: the "copilot" alias must keep resolving to Copilot built-ins.
        foreach (var modelId in CollectModelIds(providers["copilot"]))
        {
            modelRegistry.GetModel("copilot", modelId)
                .ShouldNotBeNull($"alias 'copilot' must resolve model '{modelId}'");
        }

        // The canonical "github-copilot" key must of course also resolve.
        foreach (var modelId in CollectModelIds(providers["github-copilot"]))
        {
            modelRegistry.GetModel("github-copilot", modelId)
                .ShouldNotBeNull($"'github-copilot' must resolve model '{modelId}'");
        }
    }

    [Fact]
    public void PreCarveOutConfig_AliasAndCanonical_ResolveToTheSameModel()
    {
        var modelRegistry = new ModelRegistry();
        new BuiltInModels().RegisterAll(modelRegistry);

        var viaAlias = modelRegistry.GetModel("copilot", "claude-sonnet-4");
        var viaCanonical = modelRegistry.GetModel("github-copilot", "claude-sonnet-4");

        viaAlias.ShouldNotBeNull();
        viaCanonical.ShouldNotBeNull();
        ModelRegistry.ModelsAreEqual(viaAlias!, viaCanonical!).ShouldBeTrue();
    }

    [Fact]
    public void PreCarveOutConfig_DirectVendorModels_ResolveUnaffected()
    {
        var providers = PlatformConfigLoader.Load(FixturePath).Providers!;
        var modelRegistry = new ModelRegistry();
        new BuiltInModels().RegisterAll(modelRegistry);

        foreach (var modelId in CollectModelIds(providers["anthropic"]))
        {
            modelRegistry.GetModel("anthropic", modelId)
                .ShouldNotBeNull($"direct anthropic must resolve '{modelId}'");
        }

        foreach (var modelId in CollectModelIds(providers["openai"]))
        {
            modelRegistry.GetModel("openai", modelId)
                .ShouldNotBeNull($"direct openai must resolve '{modelId}'");
        }
    }

    private static IEnumerable<string> CollectModelIds(ProviderConfig provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.DefaultModel))
            yield return provider.DefaultModel!;
        if (provider.Models is null) yield break;
        foreach (var id in provider.Models)
            yield return id;
    }
}
