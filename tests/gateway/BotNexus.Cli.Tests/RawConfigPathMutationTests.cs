using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using Shouldly;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Acceptance tests for issue #2057: targeted CLI config mutations must rewrite the exact raw
/// JSON path they mean to change and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Every test seeds a REAL temporary <c>config.json</c> containing the four categories of data the
/// old typed whole-root rewrite silently destroyed: the reserved <c>agents.defaults</c> entry the
/// loader lifts out of <c>Agents</c>, unknown root keys, unknown child keys nested inside known
/// sections, and extension-owned JSON. Secrets and the full set of known provider fields are seeded
/// too so field-erasure regressions surface.
/// </para>
/// <para>
/// Each test then executes exactly ONE targeted mutation and asserts (a) only the intended semantic
/// delta occurred, (b) the file reloads cleanly, and - for the rejection cases - (c) the original
/// file is byte-for-byte unchanged when validation rejects the candidate.
/// </para>
/// </remarks>
[Collection("AnsiConsole")]
public sealed class RawConfigPathMutationTests : IDisposable
{
    private readonly string _rootPath;
    private readonly string _configPath;
    private readonly IAnsiConsole _originalConsole;

    /// <summary>
    /// The canary document. Everything outside the single mutated node must survive verbatim.
    /// </summary>
    private const string SeedConfig = """
        {
          "version": 1,
          "unknownRootField": "keep-me",
          "unknownRootObject": { "nested": { "deep": [1, 2, 3] } },
          "gateway": {
            "listenUrl": "http://localhost:5099",
            "defaultAgentId": "assistant",
            "unknownGatewayField": "gateway-canary",
            "locations": {
              "vault": {
                "type": "filesystem",
                "path": "/data/vault",
                "description": "seeded",
                "unknownLocationField": "location-canary"
              }
            },
            "extensions": {
              "acme-extension": {
                "enabled": true,
                "opaqueExtensionState": { "cursor": "abc123", "tuning": [0.1, 0.2] }
              }
            }
          },
          "providers": {
            "copilot": {
              "enabled": true,
              "apiKey": "sk-super-secret",
              "baseUrl": "https://api.example.test",
              "defaultModel": "gpt-4.1",
              "api": "openai-completions",
              "models": ["gpt-4.1", "gpt-5"],
              "reasoning": true,
              "unknownProviderField": "provider-canary"
            }
          },
          "agents": {
            "defaults": {
              "provider": "copilot",
              "model": "gpt-4.1"
            },
            "assistant": {
              "provider": "copilot",
              "model": "gpt-4.1",
              "displayName": "Assistant",
              "unknownAgentField": "agent-canary"
            }
          }
        }
        """;

    public RawConfigPathMutationTests()
    {
        _originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(new StringWriter()),
            Interactive = InteractionSupport.No
        });

        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-2057-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
        File.WriteAllText(_configPath, SeedConfig);
    }

    public void Dispose()
    {
        AnsiConsole.Console = _originalConsole;
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private JsonObject ReadRoot() => JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();

    /// <summary>
    /// Asserts every canary that the mutation under test was not supposed to touch is still present
    /// and unchanged, and that the document still reloads through the real loader.
    /// </summary>
    private void AssertCanariesSurvive()
    {
        var root = ReadRoot();

        root["unknownRootField"]!.GetValue<string>().ShouldBe("keep-me");
        root["unknownRootObject"]!["nested"]!["deep"]!.AsArray().Count.ShouldBe(3);

        var gateway = root["gateway"]!.AsObject();
        gateway["unknownGatewayField"]!.GetValue<string>().ShouldBe("gateway-canary");
        gateway["extensions"]!["acme-extension"]!["opaqueExtensionState"]!["cursor"]!
            .GetValue<string>().ShouldBe("abc123");

        // agents.defaults is the reserved entry the loader extracts into [JsonIgnore] AgentDefaults;
        // a typed round-trip deleted it outright.
        root["agents"]!["defaults"]!["model"]!.GetValue<string>().ShouldBe("gpt-4.1");

        // Fresh reload must succeed and be error-free.
        var reloaded = PlatformConfigLoader.Load(_configPath, validateOnLoad: false);
        PlatformConfigLoader.Validate(reloaded).ShouldBeEmpty();
    }

    [Fact]
    public async Task Config_set_rewrites_only_the_addressed_path()
    {
        var before = File.ReadAllText(_configPath);

        var exitCode = await new ConfigCommands(new ConfigPathResolver())
            .ExecuteSetAsync("gateway.listenUrl", "http://localhost:6001", _configPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
        File.ReadAllText(_configPath).ShouldNotBe(before);

        var root = ReadRoot();
        root["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:6001");
        root["gateway"]!["defaultAgentId"]!.GetValue<string>().ShouldBe("assistant");
        AssertCanariesSurvive();
    }

    [Fact]
    public async Task Config_set_preserves_provider_secret_and_all_known_fields()
    {
        var exitCode = await new ConfigCommands(new ConfigPathResolver())
            .ExecuteSetAsync("gateway.listenUrl", "http://localhost:6002", _configPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);

        var provider = ReadRoot()["providers"]!["copilot"]!.AsObject();
        provider["apiKey"]!.GetValue<string>().ShouldBe("sk-super-secret");
        provider["reasoning"]!.GetValue<bool>().ShouldBeTrue();
        provider["unknownProviderField"]!.GetValue<string>().ShouldBe("provider-canary");
        provider["models"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Config_set_leaves_file_untouched_when_the_candidate_is_rejected()
    {
        var before = File.ReadAllText(_configPath);

        // gateway.listenUrl must use http/https; an ftp URL is a well-formed string that the typed
        // coercion accepts but the validator rejects, so it exercises exactly the "candidate built,
        // then rejected" path that must never reach disk.
        var exitCode = await new ConfigCommands(new ConfigPathResolver())
            .ExecuteSetAsync("gateway.listenUrl", "ftp://localhost:6001", _configPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
        File.ReadAllText(_configPath).ShouldBe(before);
    }

    [Fact]
    public async Task Agent_add_inserts_only_the_new_entry()
    {
        var exitCode = await new AgentCommands()
            .ExecuteAddAsync("librarian", "copilot", "gpt-4.1", enabled: true, _configPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);

        var agents = ReadRoot()["agents"]!.AsObject();
        agents["librarian"]!["model"]!.GetValue<string>().ShouldBe("gpt-4.1");
        agents["assistant"]!["unknownAgentField"]!.GetValue<string>().ShouldBe("agent-canary");
        AssertCanariesSurvive();
    }

    [Fact]
    public async Task Agent_remove_deletes_only_the_named_entry_and_keeps_defaults()
    {
        var exitCode = await new AgentCommands()
            .ExecuteRemoveAsync("assistant", _configPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);

        var agents = ReadRoot()["agents"]!.AsObject();
        agents.ContainsKey("assistant").ShouldBeFalse();
        agents.ContainsKey("defaults").ShouldBeTrue();

        var root = ReadRoot();
        root["unknownRootField"]!.GetValue<string>().ShouldBe("keep-me");
        root["providers"]!["copilot"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-super-secret");
    }

    [Fact]
    public async Task Provider_add_patches_only_supplied_fields_and_preserves_capabilities()
    {
        var exitCode = await new ProviderCommand().ExecuteAddAsync(
            _configPath,
            "copilot",
            api: null,
            apiKey: null,
            baseUrl: "https://api.updated.test",
            defaultModel: null,
            models: [],
            enabled: true,
            verbose: false,
            CancellationToken.None);

        exitCode.ShouldBe(0);

        var provider = ReadRoot()["providers"]!["copilot"]!.AsObject();
        provider["baseUrl"]!.GetValue<string>().ShouldBe("https://api.updated.test");

        // Everything not supplied must survive - including the secret, the capability flag, the
        // model allowlist, and the unknown field the typed graph does not model.
        provider["apiKey"]!.GetValue<string>().ShouldBe("sk-super-secret");
        provider["defaultModel"]!.GetValue<string>().ShouldBe("gpt-4.1");
        provider["api"]!.GetValue<string>().ShouldBe("openai-completions");
        provider["reasoning"]!.GetValue<bool>().ShouldBeTrue();
        provider["models"]!.AsArray().Count.ShouldBe(2);
        provider["unknownProviderField"]!.GetValue<string>().ShouldBe("provider-canary");
        AssertCanariesSurvive();
    }

    [Fact]
    public async Task Provider_remove_deletes_only_the_named_provider()
    {
        var exitCode = await new ProviderCommand()
            .ExecuteRemoveAsync(_configPath, "copilot", verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);

        var root = ReadRoot();
        root["providers"]!.AsObject().ContainsKey("copilot").ShouldBeFalse();
        root["unknownRootField"]!.GetValue<string>().ShouldBe("keep-me");
        root["agents"]!["defaults"]!["model"]!.GetValue<string>().ShouldBe("gpt-4.1");
    }

    [Fact]
    public async Task Locations_add_inserts_only_the_new_location()
    {
        var target = Path.Combine(_rootPath, "notes");
        Directory.CreateDirectory(target);

        var exitCode = await new LocationsCommand().ExecuteAddAsync(
            "notes",
            "filesystem",
            target,
            endpoint: null,
            connectionString: null,
            description: "notes location",
            _configPath,
            verbose: false,
            CancellationToken.None);

        exitCode.ShouldBe(0);

        var locations = ReadRoot()["gateway"]!["locations"]!.AsObject();
        locations["notes"]!["type"]!.GetValue<string>().ShouldBe("filesystem");
        locations["vault"]!["unknownLocationField"]!.GetValue<string>().ShouldBe("location-canary");
        AssertCanariesSurvive();
    }

    [Fact]
    public async Task Locations_update_patches_only_supplied_fields()
    {
        var target = Path.Combine(_rootPath, "moved");
        Directory.CreateDirectory(target);

        var exitCode = await new LocationsCommand().ExecuteUpdateAsync(
            "vault",
            target,
            endpoint: null,
            description: null,
            _configPath,
            verbose: false,
            CancellationToken.None);

        exitCode.ShouldBe(0);

        var vault = ReadRoot()["gateway"]!["locations"]!["vault"]!.AsObject();
        vault["path"]!.GetValue<string>().ShouldBe(target);
        vault["description"]!.GetValue<string>().ShouldBe("seeded");
        vault["unknownLocationField"]!.GetValue<string>().ShouldBe("location-canary");
        AssertCanariesSurvive();
    }

    [Fact]
    public async Task Locations_delete_removes_only_the_named_location()
    {
        var exitCode = await new LocationsCommand()
            .ExecuteDeleteAsync("vault", _configPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);

        ReadRoot()["gateway"]!["locations"]!.AsObject().ContainsKey("vault").ShouldBeFalse();
        AssertCanariesSurvive();
    }

    [Fact]
    public async Task Agent_import_replaces_the_entry_without_disturbing_the_rest_of_the_document()
    {
        var template = new AgentTemplate
        {
            Agent = new AgentTemplateDescriptor
            {
                DisplayName = "Imported",
                ApiProvider = "copilot",
                ModelId = "gpt-4.1"
            }
        };
        var templatePath = Path.Combine(_rootPath, "librarian.agent.json");
        await File.WriteAllTextAsync(templatePath, template.ToJson(), CancellationToken.None);

        var exitCode = await new AgentCommands().ExecuteImportAsync(
            templatePath,
            _configPath,
            idOverride: "librarian",
            sets: [],
            overwrite: false,
            verbose: false,
            CancellationToken.None);

        exitCode.ShouldBe(0);

        var agents = ReadRoot()["agents"]!.AsObject();
        agents["librarian"]!["displayName"]!.GetValue<string>().ShouldBe("Imported");
        agents["assistant"]!["unknownAgentField"]!.GetValue<string>().ShouldBe("agent-canary");
        agents.ContainsKey("defaults").ShouldBeTrue();
        AssertCanariesSurvive();
    }

    [Fact]
    public void RawConfigPath_set_creates_missing_intermediate_objects()
    {
        var root = new JsonObject();

        RawConfigPath.TrySet(root, "a.b.c", JsonValue.Create("v"), out var error).ShouldBeTrue();
        error.ShouldBeEmpty();
        root["a"]!["b"]!["c"]!.GetValue<string>().ShouldBe("v");
    }

    [Fact]
    public void RawConfigPath_set_matches_existing_key_casing_instead_of_creating_a_sibling()
    {
        var root = JsonNode.Parse("""{ "Gateway": { "ListenUrl": "old" } }""")!.AsObject();

        RawConfigPath.TrySet(root, "gateway.listenUrl", JsonValue.Create("new"), out _).ShouldBeTrue();

        root.Count.ShouldBe(1);
        root["Gateway"]!.AsObject().Count.ShouldBe(1);
        root["Gateway"]!["ListenUrl"]!.GetValue<string>().ShouldBe("new");
    }

    [Fact]
    public void RawConfigPath_patch_entry_leaves_unsupplied_properties_alone()
    {
        var root = JsonNode.Parse("""{ "providers": { "p": { "a": 1, "b": 2 } } }""")!.AsObject();

        RawConfigPath.TryPatchEntry(root, "providers", "p", new JsonObject { ["b"] = 9 }, out _).ShouldBeTrue();

        root["providers"]!["p"]!["a"]!.GetValue<int>().ShouldBe(1);
        root["providers"]!["p"]!["b"]!.GetValue<int>().ShouldBe(9);
    }

    [Fact]
    public void RawConfigPath_rejects_a_malformed_path()
    {
        RawConfigPath.TrySet(new JsonObject(), "a.[x]", JsonValue.Create(1), out var error).ShouldBeFalse();
        error.ShouldNotBeEmpty();
    }

    [Fact]
    public void RawConfigPath_treats_entry_keys_as_literals_not_paths()
    {
        var root = new JsonObject();

        RawConfigPath.TrySetEntry(root, "gateway.locations", "my.location", JsonValue.Create("v"), out _).ShouldBeTrue();

        root["gateway"]!["locations"]!.AsObject().ContainsKey("my.location").ShouldBeTrue();
    }

    [Fact]
    public void ValidateRawJson_rejects_a_candidate_that_would_not_reload()
    {
        var errors = PlatformConfigLoader.ValidateRawJson("""
            { "gateway": { "listenUrl": "ftp://localhost:1" }, "agents": {} }
            """);

        errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void ValidateRawJson_accepts_the_seeded_document()
    {
        PlatformConfigLoader.ValidateRawJson(SeedConfig).ShouldBeEmpty();
    }

    [Fact]
    public void ValidateRawJson_reports_invalid_json_rather_than_throwing()
    {
        var errors = PlatformConfigLoader.ValidateRawJson("{ not json");

        errors.ShouldNotBeEmpty();
        errors[0].ShouldContain("Invalid JSON");
    }

    [Fact]
    public async Task Writer_does_not_touch_the_file_when_the_candidate_is_rejected()
    {
        var writer = new PlatformConfigWriter(_configPath, new System.IO.Abstractions.FileSystem());
        var before = await File.ReadAllTextAsync(_configPath, CancellationToken.None);

        var errors = await writer.MutateValidatedAsync(
            root =>
            {
                root["gateway"]!["listenUrl"] = "ftp://localhost:1";
                return null;
            },
            "test-rejection",
            CancellationToken.None);

        errors.ShouldNotBeEmpty();
        (await File.ReadAllTextAsync(_configPath, CancellationToken.None)).ShouldBe(before);
    }

    [Fact]
    public async Task Writer_surfaces_a_mutation_abort_without_writing()
    {
        var writer = new PlatformConfigWriter(_configPath, new System.IO.Abstractions.FileSystem());
        var before = await File.ReadAllTextAsync(_configPath, CancellationToken.None);

        var errors = await writer.MutateValidatedAsync(
            _ => "cannot resolve key path",
            "test-abort",
            CancellationToken.None);

        errors.ShouldHaveSingleItem().ShouldBe("cannot resolve key path");
        (await File.ReadAllTextAsync(_configPath, CancellationToken.None)).ShouldBe(before);
    }

    [Fact]
    public async Task Agent_defaults_survives_a_full_mutation_sequence()
    {
        var agents = new AgentCommands();
        var providers = new ProviderCommand();

        (await agents.ExecuteAddAsync("librarian", "copilot", "gpt-4.1", true, _configPath, false, CancellationToken.None))
            .ShouldBe(0);
        (await providers.ExecuteAddAsync(_configPath, "local", "openai-completions", "k", "http://localhost:11434", "llama3", [], true, false, CancellationToken.None))
            .ShouldBe(0);
        (await new ConfigCommands(new ConfigPathResolver())
            .ExecuteSetAsync("gateway.listenUrl", "http://localhost:6003", _configPath, false, CancellationToken.None))
            .ShouldBe(0);
        (await agents.ExecuteRemoveAsync("librarian", _configPath, false, CancellationToken.None)).ShouldBe(0);

        var root = ReadRoot();
        root["agents"]!["defaults"]!["provider"]!.GetValue<string>().ShouldBe("copilot");
        root["unknownRootField"]!.GetValue<string>().ShouldBe("keep-me");
        root["providers"]!["copilot"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-super-secret");
        root["providers"]!["local"]!["defaultModel"]!.GetValue<string>().ShouldBe("llama3");
        root["gateway"]!["extensions"]!["acme-extension"]!["opaqueExtensionState"]!["tuning"]!
            .AsArray().Count.ShouldBe(2);

        var reloaded = PlatformConfigLoader.Load(_configPath, validateOnLoad: false);
        reloaded.AgentDefaults.ShouldNotBeNull();
        PlatformConfigLoader.Validate(reloaded).ShouldBeEmpty();
    }

    [Fact]
    public void Seeded_document_is_a_faithful_baseline()
    {
        // Guards the fixture itself: if the seed ever stops containing the categories under test the
        // preservation assertions above would pass vacuously.
        var root = ReadRoot();
        root.ContainsKey("unknownRootField").ShouldBeTrue();
        root["agents"]!.AsObject().ContainsKey("defaults").ShouldBeTrue();
        root["providers"]!["copilot"]!.AsObject().ContainsKey("reasoning").ShouldBeTrue();
        JsonSerializer.Deserialize<JsonObject>(SeedConfig).ShouldNotBeNull();
    }
}
