using System.Text.Json;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Api.Configuration;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Tests for the per-capability provider config split (issue #2854).
///
/// <para>The load-bearing property is <b>behaviour parity</b>: the flat chat fields on
/// <c>ProviderConfig</c> are retained and every existing config must bind and resolve exactly as
/// before. The nested <c>chat</c> / <c>embeddings</c> objects are additive, so these tests pin both
/// halves — the legacy path unchanged, and the new path overriding it field-by-field.</para>
/// </summary>
public sealed class ProviderCapabilityConfigTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static ProviderConfig Bind(string json) =>
        JsonSerializer.Deserialize<ProviderConfig>(json, JsonOptions)!;

    // ── AC1: the nested objects exist and bind ────────────────────────────────

    [Fact]
    public void Ac1_ChatObject_BindsEveryChatField()
    {
        var provider = Bind("""
        {
          "enabled": true,
          "baseUrl": "http://localhost:11434",
          "chat": {
            "api": "openai-completions",
            "defaultModel": "llama3.1",
            "models": ["llama3.1", "qwen2.5"],
            "input": ["text", "image"],
            "reasoning": true,
            "supportsExtraHighThinking": true,
            "supportsExtendedContextWindow": false,
            "contextWindow": 65536
          }
        }
        """);

        provider.Chat.ShouldNotBeNull();
        provider.Chat!.Api.ShouldBe("openai-completions");
        provider.Chat.DefaultModel.ShouldBe("llama3.1");
        provider.Chat.Models.ShouldBe(["llama3.1", "qwen2.5"]);
        provider.Chat.Input.ShouldBe(["text", "image"]);
        provider.Chat.Reasoning.ShouldBe(true);
        provider.Chat.SupportsExtraHighThinking.ShouldBe(true);
        provider.Chat.SupportsExtendedContextWindow.ShouldBe(false);
        provider.Chat.ContextWindow.ShouldBe(65536);
    }

    [Fact]
    public void Ac1_EmbeddingsObject_CarriesItsOwnModelSlot_SeparateFromChat()
    {
        // The whole point of the split: a provider serving both capabilities has TWO model ids, not
        // one overloaded 'defaultModel'.
        var provider = Bind("""
        {
          "enabled": true,
          "baseUrl": "http://localhost:11434",
          "chat":       { "api": "openai-completions", "defaultModel": "llama3.1" },
          "embeddings": { "api": "openai-embeddings", "model": "nomic-embed-text", "dimensions": 768 }
        }
        """);

        provider.Embeddings.ShouldNotBeNull();
        provider.Embeddings!.Api.ShouldBe("openai-embeddings");
        provider.Embeddings.Model.ShouldBe("nomic-embed-text");
        provider.Embeddings.Dimensions.ShouldBe(768);

        // Two distinct model ids coexist — unrepresentable before this change.
        provider.EffectiveDefaultModel.ShouldBe("llama3.1");
        provider.Embeddings.Model.ShouldNotBe(provider.EffectiveDefaultModel);
    }

    // ── AC2: pre-change configs bind and resolve identically ──────────────────

    [Fact]
    public void Ac2_PreChangeFlatConfig_ResolvesTheSameChatModel()
    {
        // Byte-for-byte the shape a pre-#2854 config.json uses: no nested objects at all.
        var provider = Bind("""
        {
          "enabled": true,
          "baseUrl": "http://localhost:11434",
          "api": "openai-completions",
          "defaultModel": "claude-sonnet-4",
          "models": ["claude-sonnet-4", "gpt-4.1"],
          "input": ["text"],
          "reasoning": true,
          "supportsExtraHighThinking": true,
          "supportsExtendedContextWindow": true,
          "contextWindow": 200000
        }
        """);

        provider.Chat.ShouldBeNull("a pre-change config declares no nested chat object");

        provider.EffectiveDefaultModel.ShouldBe("claude-sonnet-4");
        provider.EffectiveModels.ShouldBe(["claude-sonnet-4", "gpt-4.1"]);
        provider.EffectiveApi.ShouldBe("openai-completions");
        provider.EffectiveInput.ShouldBe(["text"]);
        provider.EffectiveReasoning.ShouldBe(true);
        provider.EffectiveSupportsExtraHighThinking.ShouldBe(true);
        provider.EffectiveSupportsExtendedContextWindow.ShouldBe(true);
        provider.EffectiveContextWindow.ShouldBe(200000);
    }

    [Fact]
    public void Ac2_NestedChatField_OverridesItsFlatEquivalent()
    {
        var provider = Bind("""
        { "defaultModel": "legacy-model", "chat": { "defaultModel": "nested-model" } }
        """);

        provider.EffectiveDefaultModel.ShouldBe("nested-model");
    }

    [Fact]
    public void Ac2_PrecedenceIsPerField_NotPerObject()
    {
        // A chat object that states only 'api' must NOT wipe a flat defaultModel. Object-level
        // precedence would silently drop the operator's existing default model.
        var provider = Bind("""
        { "defaultModel": "legacy-model", "chat": { "api": "openai-completions" } }
        """);

        provider.EffectiveApi.ShouldBe("openai-completions");
        provider.EffectiveDefaultModel.ShouldBe("legacy-model");
    }

    [Fact]
    public void Ac2_PreCarveOutFixture_StillBindsAndResolvesThroughEffectiveAccessors()
    {
        // The existing deployment-safety fixture, read through the NEW accessors: the compatibility
        // path is only real if the shipped fixture resolves through it unchanged.
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Configs", "precarveout-config.json");
        var providers = PlatformConfigLoader.Load(fixturePath).Providers!;

        var registry = new ModelRegistry();
        new BuiltInModels().RegisterAll(registry);

        foreach (var (providerKey, providerConfig) in providers)
        {
            providerConfig.EffectiveDefaultModel.ShouldNotBeNullOrWhiteSpace(
                $"providers.{providerKey} must still expose its default model");
            registry.GetModel(providerKey, providerConfig.EffectiveDefaultModel!)
                .ShouldNotBeNull($"providers.{providerKey}.defaultModel must still resolve");

            foreach (var modelId in providerConfig.EffectiveModels ?? [])
            {
                registry.GetModel(providerKey, modelId)
                    .ShouldNotBeNull($"providers.{providerKey} model '{modelId}' must still resolve");
            }
        }
    }

    // ── AC3: deprecation diagnostic names the nested replacement ──────────────

    [Fact]
    public void Ac3_FlatChatFields_EmitDeprecationWarningNamingNestedPath()
    {
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["my-ollama"] = new()
                {
                    DefaultModel = "llama3.1",
                    Api = "openai-completions",
                    ContextWindow = 65536
                }
            }
        };

        var warnings = PlatformConfigValidator.ValidateWarnings(config);

        warnings.ShouldContain("providers.my-ollama.defaultModel is deprecated; use providers.my-ollama.chat.defaultModel instead. The flat field is still honoured.");
        warnings.ShouldContain("providers.my-ollama.api is deprecated; use providers.my-ollama.chat.api instead. The flat field is still honoured.");
        warnings.ShouldContain("providers.my-ollama.contextWindow is deprecated; use providers.my-ollama.chat.contextWindow instead. The flat field is still honoured.");
    }

    [Fact]
    public void Ac3_DeprecationDiagnostic_IsAWarningNotAnError()
    {
        // A flat-field config must still LOAD. If the diagnostic were an error this would be a
        // breaking migration rather than a deprecation.
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["my-ollama"] = new() { DefaultModel = "llama3.1", BaseUrl = "http://localhost:11434" }
            }
        };

        PlatformConfigValidator.Validate(config).ShouldBeEmpty();
    }

    [Fact]
    public void Ac3_ProviderUsingOnlyNestedFields_EmitsNoDeprecationWarning()
    {
        // Positive pin: the diagnostic must not fire on a fully migrated entry, or it is noise.
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["my-ollama"] = new()
                {
                    BaseUrl = "http://localhost:11434",
                    Chat = new ProviderChatConfig { Api = "openai-completions", DefaultModel = "llama3.1" }
                }
            }
        };

        PlatformConfigValidator.ValidateWarnings(config)
            .ShouldNotContain(w => w.Contains("deprecated", StringComparison.Ordinal));
    }

    [Fact]
    public void Ac3_EmbeddingsWithoutModel_IsAValidationError()
    {
        // There is deliberately no fallback to the chat default model: borrowing a chat model id for
        // an embedding request is the exact overloading #2854 removes.
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["my-ollama"] = new()
                {
                    BaseUrl = "http://localhost:11434",
                    Embeddings = new ProviderEmbeddingsConfig { Api = "openai-embeddings" }
                }
            }
        };

        PlatformConfigValidator.Validate(config)
            .ShouldContain(e => e.Contains("providers.my-ollama.embeddings.model is required", StringComparison.Ordinal));
    }

    // ── AC4: capability resolution is the union of code- and config-declared ──

    [Fact]
    public void Ac4_ConfiguredEmbeddingsObject_MakesProviderEmbeddingsCapable_WithNoCodeSideDeclaration()
    {
        // The clause-4 scenario exactly: the registry declares CHAT ONLY (the #2853 default for every
        // registration on main), and the ONLY source of the embeddings capability is config.
        var codeDeclared = ProviderCapabilitySets.ChatOnly;
        codeDeclared.ShouldNotContain(ProviderCapability.Embeddings,
            "Precondition: the code-side half must NOT declare embeddings, or the union proves nothing.");

        var provider = new ProviderConfig
        {
            Enabled = true,
            BaseUrl = "http://localhost:11434",
            Embeddings = new ProviderEmbeddingsConfig { Api = "openai-embeddings", Model = "nomic-embed-text" }
        };

        var effective = provider.ResolveCapabilities(codeDeclared);

        effective.ShouldContain(ProviderCapability.Embeddings);
        effective.ShouldContain(ProviderCapability.Chat, "the code-declared half must survive the union");
    }

    [Fact]
    public void Ac4_CodeDeclaredCapability_SurvivesWhenConfigDeclaresNothing()
    {
        // The other half of "union": a bare `{ "enabled": true }` entry (the github-copilot shape in
        // the issue) keeps everything the code declares.
        var provider = new ProviderConfig { Enabled = true };

        var effective = provider.ResolveCapabilities(ProviderCapabilitySets.ChatOnly);

        effective.ShouldBe(new HashSet<ProviderCapability> { ProviderCapability.Chat }, ignoreOrder: true);
    }

    [Fact]
    public void Ac4_UnregisteredApi_StillResolvesConfigDeclaredCapabilities()
    {
        // A local endpoint whose api has no registration at all: null code-side half must not erase
        // the operator's declaration.
        var provider = new ProviderConfig
        {
            Enabled = true,
            Embeddings = new ProviderEmbeddingsConfig { Model = "nomic-embed-text" }
        };

        provider.ResolveCapabilities(codeDeclared: null)
            .ShouldBe(new HashSet<ProviderCapability> { ProviderCapability.Embeddings }, ignoreOrder: true);
    }

    [Fact]
    public void Ac4_LegacyFlatChatFields_CountAsAConfigSideChatDeclaration()
    {
        // The flat fields have always meant "this provider does chat", so they must contribute to the
        // config-declared half — otherwise a pre-change config would silently declare nothing.
        new ProviderConfig { DefaultModel = "llama3.1" }
            .DeclaredCapabilities()
            .ShouldContain(ProviderCapability.Chat);
    }

    // ── AC5: enabled:false removes every capability, from either half ─────────

    [Fact]
    public void Ac5_DisabledProvider_HasNoCapabilities_EvenWhenBothHalvesDeclareThem()
    {
        var provider = new ProviderConfig
        {
            Enabled = false,
            DefaultModel = "llama3.1",
            Embeddings = new ProviderEmbeddingsConfig { Model = "nomic-embed-text" }
        };

        provider.DeclaredCapabilities().ShouldNotBeEmpty(
            "Precondition: the entry must declare capabilities, or 'disabled removes them' is vacuous.");

        provider.ResolveCapabilities(ProviderCapabilitySets.ChatOnly).ShouldBeEmpty();
    }

    // ── AC6: portal config metadata is complete for the new objects ───────────

    [Fact]
    public void Ac6_NestedCapabilityFields_CarryUiSchemaMetadata()
    {
        var schema = ConfigSchemaBuilder.Build();

        var chatDefaultModel = GetPropertyNode(schema, "providers", "chat", "defaultModel");
        chatDefaultModel["x-ui-widget"]!.GetValue<string>().ShouldBe("select");
        chatDefaultModel["x-ui-options-source"]!.GetValue<string>().ShouldBe("models");
        chatDefaultModel["x-ui-group"]!.GetValue<string>().ShouldBe("chat");

        var embeddingsModel = GetPropertyNode(schema, "providers", "embeddings", "model");
        embeddingsModel["x-ui-widget"]!.GetValue<string>().ShouldBe("text");
        embeddingsModel["x-ui-group"]!.GetValue<string>().ShouldBe("embeddings");

        var dimensions = GetPropertyNode(schema, "providers", "embeddings", "dimensions");
        dimensions["x-ui-widget"]!.GetValue<string>().ShouldBe("number");
    }

    [Fact]
    public void Ac6_EffectiveAccessors_AreNotEmittedIntoConfigJson()
    {
        // The Effective* helpers are derived reads, not settings. Serialising them would write
        // duplicated, stale values into the user's config.json on every round-trip.
        var json = JsonSerializer.Serialize(
            new ProviderConfig { DefaultModel = "llama3.1" },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.ShouldNotContain("effective", Case.Insensitive);
    }

    private static System.Text.Json.Nodes.JsonObject GetPropertyNode(
        System.Text.Json.Nodes.JsonObject schema,
        params string[] propertyPath)
    {
        var node = schema["schema"]!.AsObject();
        foreach (var name in propertyPath)
        {
            var props = ResolveProperties(node)
                ?? throw new InvalidOperationException($"No 'properties' bag while resolving '{name}'.");
            var child = props[name]?.AsObject()
                ?? throw new InvalidOperationException($"Property '{name}' not found in schema.");
            node = child["additionalProperties"] is System.Text.Json.Nodes.JsonObject map ? map : child;
        }

        return node;
    }

    /// <summary>
    /// Returns a node's property bag, descending through an <c>anyOf</c>/<c>oneOf</c> wrapper when the
    /// exporter emits one for a nullable object property. Without this the helper would report
    /// "no properties bag" for exactly the nested capability objects under test.
    /// </summary>
    private static System.Text.Json.Nodes.JsonObject? ResolveProperties(System.Text.Json.Nodes.JsonObject node)
    {
        if (node["properties"] is System.Text.Json.Nodes.JsonObject direct)
            return direct;

        foreach (var key in new[] { "anyOf", "oneOf" })
        {
            if (node[key] is not System.Text.Json.Nodes.JsonArray branches)
                continue;

            foreach (var branch in branches)
            {
                if (branch is System.Text.Json.Nodes.JsonObject candidate &&
                    candidate["properties"] is System.Text.Json.Nodes.JsonObject nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
