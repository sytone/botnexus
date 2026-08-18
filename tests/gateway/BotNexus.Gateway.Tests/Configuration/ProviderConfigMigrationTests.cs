using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Shadow;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Tests for the #2854 provider-config auto-migration (Jon's review on PR #3277).
///
/// <para>The load-bearing properties are: values are MOVED not lost, the transform is idempotent,
/// an explicit nested value wins over a stale flat one, and the migrated document produces migrated
/// SQLite store keys — the last of which is what makes the store migration real rather than
/// assumed.</para>
/// </summary>
public sealed class ProviderConfigMigrationTests
{
    // ── The pure transform ──────────────────────────────────────────────────

    [Fact]
    public void Migrate_MovesFlatChatFieldsIntoTheChatObject()
    {
        var root = Parse("""
        {
            "providers": {
                "openai": {
                    "enabled": true,
                    "apiKey": "sk-secret",
                    "defaultModel": "gpt-4o",
                    "models": ["gpt-4o", "gpt-4o-mini"],
                    "api": "openai-completions",
                    "contextWindow": 128000
                }
            }
        }
        """);

        var migrated = ProviderConfigMigration.Migrate(root);

        migrated.ShouldBe(new[] { "openai" });

        var provider = (JsonObject)root["providers"]!["openai"]!;
        var chat = provider["chat"].ShouldBeOfType<JsonObject>();

        chat["defaultModel"]!.GetValue<string>().ShouldBe("gpt-4o");
        chat["api"]!.GetValue<string>().ShouldBe("openai-completions");
        chat["contextWindow"]!.GetValue<int>().ShouldBe(128000);
        chat["models"]!.AsArray().Select(n => n!.GetValue<string>())
            .ShouldBe(new[] { "gpt-4o", "gpt-4o-mini" });

        // Moved, not copied: leaving the flat keys behind would preserve values that no longer have
        // any effect, which reads to an operator exactly like values that do.
        provider.ContainsKey("defaultModel").ShouldBeFalse();
        provider.ContainsKey("models").ShouldBeFalse();
        provider.ContainsKey("api").ShouldBeFalse();
        provider.ContainsKey("contextWindow").ShouldBeFalse();

        // Non-chat provider fields are untouched. A migration that ate the API key would be a
        // catastrophic success.
        provider["apiKey"]!.GetValue<string>().ShouldBe("sk-secret");
        provider["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var root = Parse("""
        { "providers": { "openai": { "defaultModel": "gpt-4o", "api": "openai-completions" } } }
        """);

        ProviderConfigMigration.Migrate(root).ShouldNotBeEmpty();
        var afterFirst = root.ToJsonString();

        // Runs on EVERY gateway start, not once at an upgrade boundary, so a second pass must be a
        // no-op both in effect and in reported outcome.
        var second = ProviderConfigMigration.Migrate(root);

        second.ShouldBeEmpty();
        root.ToJsonString().ShouldBe(afterFirst);
    }

    [Fact]
    public void Migrate_AlreadyMigratedDocument_ReportsNothingChanged()
    {
        var root = Parse("""
        { "providers": { "openai": { "chat": { "defaultModel": "gpt-4o" } } } }
        """);

        ProviderConfigMigration.Migrate(root).ShouldBeEmpty();
    }

    [Fact]
    public void Migrate_ExplicitNestedValue_WinsAndDropsTheStaleFlatField()
    {
        var root = Parse("""
        {
            "providers": {
                "openai": {
                    "defaultModel": "gpt-3.5-turbo",
                    "chat": { "defaultModel": "gpt-4o" }
                }
            }
        }
        """);

        ProviderConfigMigration.Migrate(root);

        var provider = (JsonObject)root["providers"]!["openai"]!;

        // The nested value is what Effective* already honours, so the migration must not silently
        // promote the flat one over it and change which model the operator gets.
        provider["chat"]!["defaultModel"]!.GetValue<string>().ShouldBe("gpt-4o");
        provider.ContainsKey("defaultModel").ShouldBeFalse();
    }

    [Fact]
    public void Migrate_MergesIntoAPartiallyMigratedProvider()
    {
        var root = Parse("""
        {
            "providers": {
                "openai": {
                    "defaultModel": "gpt-4o",
                    "chat": { "api": "openai-completions" }
                }
            }
        }
        """);

        ProviderConfigMigration.Migrate(root);

        var chat = (JsonObject)root["providers"]!["openai"]!["chat"]!;

        // Per-field, not per-object: an existing chat object must not cause the remaining flat fields
        // to be abandoned.
        chat["api"]!.GetValue<string>().ShouldBe("openai-completions");
        chat["defaultModel"]!.GetValue<string>().ShouldBe("gpt-4o");
    }

    [Fact]
    public void Migrate_PreservesExplicitNullAndFalseValues()
    {
        var root = Parse("""
        {
            "providers": {
                "openai": { "reasoning": false, "models": null }
            }
        }
        """);

        ProviderConfigMigration.Migrate(root);

        var chat = (JsonObject)root["providers"]!["openai"]!["chat"]!;

        // `false` and explicit `null` are values an operator deliberately wrote. A truthiness-based
        // transform would drop both and silently re-enable inference.
        chat.ContainsKey("reasoning").ShouldBeTrue();
        chat["reasoning"]!.GetValue<bool>().ShouldBeFalse();
        chat.ContainsKey("models").ShouldBeTrue();
        chat["models"].ShouldBeNull();
    }

    [Fact]
    public void Migrate_DocumentWithoutProviders_IsANoOp()
    {
        var root = Parse("""{ "version": 1, "gateway": {} }""");

        ProviderConfigMigration.Migrate(root).ShouldBeEmpty();
        root.ToJsonString().ShouldBe(Parse("""{ "version": 1, "gateway": {} }""").ToJsonString());
    }

    [Fact]
    public void Migrate_NullDocument_IsANoOp() =>
        ProviderConfigMigration.Migrate(null).ShouldBeEmpty();

    [Fact]
    public void Migrate_EmbeddingsProvider_IsLeftAlone()
    {
        var root = Parse("""
        { "providers": { "ollama": { "embeddings": { "model": "nomic-embed-text" } } } }
        """);

        ProviderConfigMigration.Migrate(root).ShouldBeEmpty();
        root["providers"]!["ollama"]!["embeddings"]!["model"]!.GetValue<string>()
            .ShouldBe("nomic-embed-text");
    }

    // ── The SQLite store half of Jon's review ───────────────────────────────

    [Fact]
    public void Migrate_AlsoMigratesFlattenedStoreKeys()
    {
        var root = Parse("""
        { "providers": { "openai": { "defaultModel": "gpt-4o" } } }
        """);

        ConfigDocumentFlattener.Flatten(root).Keys
            .ShouldContain("providers.openai.defaultModel");

        ProviderConfigMigration.Migrate(root);

        var keys = ConfigDocumentFlattener.Flatten(root).Keys;

        // This is the whole reason no separate store-key migration exists: store keys are DERIVED by
        // flattening the document, so migrating the document migrates the store. If a future store
        // stops deriving its keys this way, this test fails and the assumption stops being silent.
        keys.ShouldContain("providers.openai.chat.defaultModel");
        keys.ShouldNotContain("providers.openai.defaultModel");
    }

    // ── The startup service ─────────────────────────────────────────────────

    [Fact]
    public async Task Service_RewritesConfigFileAndWritesABackup()
    {
        const string original = """
        { "providers": { "openai": { "apiKey": "sk-secret", "defaultModel": "gpt-4o" } } }
        """;
        var (service, fs, configPath) = BuildService(original);

        await service.TryMigrateAsync(configPath, CancellationToken.None);

        var rewritten = Parse(fs.File.ReadAllText(configPath));
        rewritten["providers"]!["openai"]!["chat"]!["defaultModel"]!.GetValue<string>()
            .ShouldBe("gpt-4o");

        // Byte-for-byte original, so rollback is a file copy rather than a reconstruction.
        var backupPath = configPath + ProviderConfigMigrationHostedService.BackupSuffix;
        fs.File.Exists(backupPath).ShouldBeTrue();
        fs.File.ReadAllText(backupPath).ShouldBe(original);
    }

    [Fact]
    public async Task Service_AlreadyMigratedConfig_IsNotRewrittenAndGetsNoBackup()
    {
        const string original = """
        { "providers": { "openai": { "chat": { "defaultModel": "gpt-4o" } } } }
        """;
        var (service, fs, configPath) = BuildService(original);

        await service.TryMigrateAsync(configPath, CancellationToken.None);

        // Untouched, so repeated restarts cannot churn the file or pile up backups that each overwrite
        // the last good one.
        fs.File.ReadAllText(configPath).ShouldBe(original);
        fs.File.Exists(configPath + ProviderConfigMigrationHostedService.BackupSuffix).ShouldBeFalse();
    }

    [Fact]
    public async Task Service_MalformedConfig_DoesNotThrowAndLeavesTheFileIntact()
    {
        const string original = "{ this is not json";
        var (service, fs, configPath) = BuildService(original);

        // Must not throw: BackgroundServiceExceptionBehavior is StopHost (#2731), so an escaping
        // exception here would take the whole gateway down over a config it could simply have ignored.
        await Should.NotThrowAsync(() => service.TryMigrateAsync(configPath, CancellationToken.None));

        fs.File.ReadAllText(configPath).ShouldBe(original);
    }

    [Fact]
    public async Task Service_MissingConfigFile_IsANoOp()
    {
        var fs = new MockFileSystem();
        var service = new ProviderConfigMigrationHostedService(
            fs, NullLogger<ProviderConfigMigrationHostedService>.Instance);

        await Should.NotThrowAsync(
            () => service.TryMigrateAsync(@"C:\nope\config.json", CancellationToken.None));
    }

    [Fact]
    public async Task Service_MigratedFile_BindsToTheSameEffectiveValues()
    {
        const string original = """
        {
            "providers": {
                "openai": {
                    "enabled": true,
                    "defaultModel": "gpt-4o",
                    "api": "openai-completions",
                    "contextWindow": 200000,
                    "reasoning": true
                }
            }
        }
        """;
        var before = Bind(original).Providers!["openai"];
        var (service, fs, configPath) = BuildService(original);

        await service.TryMigrateAsync(configPath, CancellationToken.None);

        var after = Bind(fs.File.ReadAllText(configPath)).Providers!["openai"];

        // Behaviour parity stated as an equality, which is the only form of it worth asserting: the
        // migration is a shape change and must be invisible to every consumer of Effective*.
        after.EffectiveDefaultModel.ShouldBe(before.EffectiveDefaultModel);
        after.EffectiveApi.ShouldBe(before.EffectiveApi);
        after.EffectiveContextWindow.ShouldBe(before.EffectiveContextWindow);
        after.EffectiveReasoning.ShouldBe(before.EffectiveReasoning);
        after.EffectiveDefaultModel.ShouldBe("gpt-4o");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static JsonObject Parse(string json) =>
        (JsonObject)JsonNode.Parse(json, nodeOptions: new JsonNodeOptions { PropertyNameCaseInsensitive = true })!;

    private static PlatformConfig Bind(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<PlatformConfig>(
            json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            })!;

    private static (ProviderConfigMigrationHostedService service, MockFileSystem fs, string configPath)
        BuildService(string configJson)
    {
        var configPath = @"C:\home\.botnexus\config.json";
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [configPath] = new MockFileData(configJson),
        });

        var service = new ProviderConfigMigrationHostedService(
            fs, NullLogger<ProviderConfigMigrationHostedService>.Instance);

        return (service, fs, configPath);
    }
}
