using System.Text.Json;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Per-capability provider configuration (#2854, part of the providers epic #2500).
/// </summary>
/// <remarks>
/// <para>
/// These tests own the CONFIG-SHAPE half of the slice: that the nested <c>chat</c> and
/// <c>embeddings</c> objects bind, that a pre-#2854 flat document still resolves the same chat
/// model, that the validator names the nested replacement path, and that capability resolution is
/// the union of what the code declares and what the config declares.
/// </para>
/// <para>
/// The union is asserted through <see cref="ProviderCapabilityResolver"/> rather than through a
/// live registry because the resolution rule -- union, then narrowed by <c>enabled</c> -- is the
/// thing #2854 introduces; which providers happen to be registered in a given host is not.
/// </para>
/// </remarks>
public sealed class ProviderCapabilityConfigTests
{
    private static readonly JsonSerializerOptions BindOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static ProviderConfig Bind(string json)
        => JsonSerializer.Deserialize<ProviderConfig>(json, BindOptions)
           ?? throw new InvalidOperationException("provider config did not bind");

    // ---------------------------------------------------------------------
    // AC1 -- the nested capability objects exist and bind.
    // ---------------------------------------------------------------------

    [Fact]
    public void NestedChatAndEmbeddingsObjects_Bind_FromTheDocumentedShape()
    {
        // The exact document from the issue body's "Proposed design" section.
        var config = Bind(
            """
            {
              "enabled": true,
              "baseUrl": "http://localhost:11434",
              "chat":       { "api": "openai-completions", "defaultModel": "llama3.1", "models": ["llama3.1"] },
              "embeddings": { "api": "openai-embeddings",  "model": "nomic-embed-text", "dimensions": 768 }
            }
            """);

        config.BaseUrl.ShouldBe("http://localhost:11434");

        config.Chat.ShouldNotBeNull();
        config.Chat!.Api.ShouldBe("openai-completions");
        config.Chat.DefaultModel.ShouldBe("llama3.1");
        config.Chat.Models.ShouldBe(["llama3.1"]);

        config.Embeddings.ShouldNotBeNull();
        config.Embeddings!.Api.ShouldBe("openai-embeddings");
        config.Embeddings.Model.ShouldBe("nomic-embed-text");
        config.Embeddings.Dimensions.ShouldBe(768);
    }

    [Fact]
    public void NestedChatFields_Win_OverTheFlatEquivalents()
    {
        var config = Bind(
            """
            {
              "api": "flat-api",
              "defaultModel": "flat-model",
              "models": ["flat-model"],
              "contextWindow": 8000,
              "reasoning": false,
              "chat": {
                "api": "nested-api",
                "defaultModel": "nested-model",
                "models": ["nested-model"],
                "contextWindow": 200000,
                "reasoning": true
              }
            }
            """);

        config.ResolveChatApi().ShouldBe("nested-api");
        config.ResolveChatDefaultModel().ShouldBe("nested-model");
        config.ResolveChatModels().ShouldBe(["nested-model"]);
        config.ResolveChatContextWindow().ShouldBe(200000);
        config.ResolveChatReasoning().ShouldBe(true);
    }

    [Fact]
    public void PartialChatObject_FallsBackPerFieldToTheFlatValue()
    {
        // A half-migrated document: the operator moved `defaultModel` but not `api`. Falling back
        // per FIELD rather than per OBJECT is what keeps that intermediate state working.
        var config = Bind(
            """
            {
              "api": "flat-api",
              "models": ["flat-model"],
              "chat": { "defaultModel": "nested-model" }
            }
            """);

        config.ResolveChatDefaultModel().ShouldBe("nested-model");
        config.ResolveChatApi().ShouldBe("flat-api");
        config.ResolveChatModels().ShouldBe(["flat-model"]);
    }

    // ---------------------------------------------------------------------
    // AC2 -- a pre-#2854 flat document resolves the same chat model.
    // ---------------------------------------------------------------------

    [Fact]
    public void PreChangeFlatDocument_ResolvesTheSameChatModel()
    {
        // Byte-for-byte the shape a pre-#2854 config.json used for a local Ollama endpoint.
        var config = Bind(
            """
            {
              "enabled": true,
              "baseUrl": "http://localhost:11434",
              "api": "openai-completions",
              "defaultModel": "llama3.1",
              "models": ["llama3.1", "qwen2.5"],
              "input": ["text", "image"],
              "reasoning": true,
              "supportsExtraHighThinking": true,
              "supportsExtendedContextWindow": false,
              "contextWindow": 32768
            }
            """);

        config.Chat.ShouldBeNull("a pre-change document has no nested chat object at all");

        config.ResolveChatApi().ShouldBe("openai-completions");
        config.ResolveChatDefaultModel().ShouldBe("llama3.1");
        config.ResolveChatModels().ShouldBe(["llama3.1", "qwen2.5"]);
        config.ResolveChatInput().ShouldBe(["text", "image"]);
        config.ResolveChatReasoning().ShouldBe(true);
        config.ResolveChatSupportsExtraHighThinking().ShouldBe(true);
        config.ResolveChatSupportsExtendedContextWindow().ShouldBe(false);
        config.ResolveChatContextWindow().ShouldBe(32768);
    }

    [Fact]
    public void PreChangeFlatDocument_StillResolvesAsChatCapable()
    {
        var config = Bind("""{ "api": "openai-completions", "models": ["llama3.1"] }""");

        ProviderCapabilityResolver
            .Resolve(config, ProviderCapabilitySets.ChatOnly)
            .ShouldBe(new HashSet<ProviderCapability> { ProviderCapability.Chat });
    }

    // ---------------------------------------------------------------------
    // AC3 -- deprecation diagnostic naming the nested replacement path.
    // ---------------------------------------------------------------------

    [Fact]
    public void FlatChatFields_EmitADeprecationWarningNamingTheNestedPath()
    {
        var platform = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["my-ollama"] = new() { Api = "openai-completions", DefaultModel = "llama3.1" },
            },
        };

        var warnings = PlatformConfigValidator.ValidateWarnings(platform);

        warnings.ShouldContain(w =>
            w.Contains("providers.my-ollama.defaultModel", StringComparison.Ordinal) &&
            w.Contains("providers.my-ollama.chat.defaultModel", StringComparison.Ordinal));
        warnings.ShouldContain(w =>
            w.Contains("providers.my-ollama.api", StringComparison.Ordinal) &&
            w.Contains("providers.my-ollama.chat.api", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedOnlyProvider_EmitsNoDeprecationWarning()
    {
        var platform = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["my-ollama"] = new()
                {
                    Chat = new ProviderChatConfig { Api = "openai-completions", DefaultModel = "llama3.1" },
                    Embeddings = new ProviderEmbeddingsConfig { Model = "nomic-embed-text" },
                },
            },
        };

        PlatformConfigValidator.ValidateWarnings(platform)
            .ShouldNotContain(w => w.Contains("deprecated", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------
    // AC4 -- capability resolution is the UNION of code-declared and config-declared.
    // ---------------------------------------------------------------------

    [Fact]
    public void ConfiguredEmbeddingsObject_MakesAChatOnlyProviderEmbeddingsCapable()
    {
        // The clause-4 test. The code side declares chat ONLY -- no embeddings anywhere in the
        // registration -- and the embeddings capability arrives purely from the config document.
        var config = Bind("""{ "embeddings": { "model": "nomic-embed-text", "dimensions": 768 } }""");

        var resolved = ProviderCapabilityResolver.Resolve(config, ProviderCapabilitySets.ChatOnly);

        resolved.ShouldContain(ProviderCapability.Embeddings);
        resolved.ShouldContain(ProviderCapability.Chat);
    }

    [Fact]
    public void CodeDeclaredCapabilities_SurviveWhenTheConfigDeclaresNothing()
    {
        var config = Bind("""{ "enabled": true }""");

        var resolved = ProviderCapabilityResolver.Resolve(
            config,
            new HashSet<ProviderCapability> { ProviderCapability.Chat, ProviderCapability.Embeddings });

        resolved.ShouldBe(new HashSet<ProviderCapability>
        {
            ProviderCapability.Chat,
            ProviderCapability.Embeddings,
        });
    }

    [Fact]
    public void ConfiguredChatObject_MakesAProviderChatCapableWithNoCodeDeclaration()
    {
        var config = Bind("""{ "chat": { "defaultModel": "llama3.1" } }""");

        ProviderCapabilityResolver.Resolve(config, codeDeclared: null)
            .ShouldBe(new HashSet<ProviderCapability> { ProviderCapability.Chat });
    }

    [Fact]
    public void MissingProviderConfig_LeavesTheCodeDeclarationUntouched()
    {
        ProviderCapabilityResolver.Resolve(config: null, ProviderCapabilitySets.ChatOnly)
            .ShouldBe(new HashSet<ProviderCapability> { ProviderCapability.Chat });
    }

    // ---------------------------------------------------------------------
    // AC5 -- `enabled: false` removes EVERY capability, config-side or code-side.
    // ---------------------------------------------------------------------

    [Fact]
    public void DisabledProvider_DeclaresNoCapabilities_EvenWhenBothSidesDeclareThem()
    {
        var config = Bind(
            """
            {
              "enabled": false,
              "chat":       { "defaultModel": "llama3.1" },
              "embeddings": { "model": "nomic-embed-text" }
            }
            """);

        var resolved = ProviderCapabilityResolver.Resolve(
            config,
            new HashSet<ProviderCapability> { ProviderCapability.Chat, ProviderCapability.Embeddings });

        resolved.ShouldBeEmpty();
    }

    [Fact]
    public void DisabledProvider_WithOnlyACodeDeclaration_AlsoResolvesEmpty()
    {
        var config = Bind("""{ "enabled": false }""");

        ProviderCapabilityResolver.Resolve(config, ProviderCapabilitySets.ChatOnly)
            .ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------
    // Sad paths.
    // ---------------------------------------------------------------------

    [Fact]
    public void EmbeddingsObject_WithNoModel_IsAValidationError()
    {
        var platform = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["my-ollama"] = new() { Embeddings = new ProviderEmbeddingsConfig { Dimensions = 768 } },
            },
        };

        PlatformConfigValidator.Validate(platform)
            .ShouldContain("providers.my-ollama.embeddings.model is required when an embeddings capability is configured.");
    }

    [Fact]
    public void EmbeddingsDimensions_MustBePositive()
    {
        var platform = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["my-ollama"] = new()
                {
                    Embeddings = new ProviderEmbeddingsConfig { Model = "nomic-embed-text", Dimensions = 0 },
                },
            },
        };

        PlatformConfigValidator.Validate(platform)
            .ShouldContain("providers.my-ollama.embeddings.dimensions must be greater than zero.");
    }

    [Fact]
    public void DisabledProvider_IsNotValidatedForCapabilityShape()
    {
        // Consistent with the existing provider baseUrl rule, which also skips a disabled provider.
        var platform = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["my-ollama"] = new()
                {
                    Enabled = false,
                    Embeddings = new ProviderEmbeddingsConfig { Dimensions = 768 },
                },
            },
        };

        PlatformConfigValidator.Validate(platform)
            .ShouldNotContain(e => e.Contains("embeddings", StringComparison.OrdinalIgnoreCase));
    }
}
